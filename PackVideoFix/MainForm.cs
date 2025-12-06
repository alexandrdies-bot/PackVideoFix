using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PackVideoFix;

public sealed class MainForm : Form
{
    // ---------- CONFIG ----------
    private AppConfig _cfg = AppConfig.Default();

    // ---------- UI ----------
    private readonly PictureBox _preview;

    private readonly Label _lblPosting;
    private readonly PictureBox _picProduct;
    private readonly Button _btnStopDelete;

    private readonly MenuStrip _menu;
    private readonly StatusStrip _statusStrip;
    private readonly ToolStripStatusLabel _statusLabel;

    private readonly SplitContainer _split;

    // ---------- SCANNER FILTER ----------
    private BarcodeMessageFilter? _scanFilter;

    // ---------- OZON ----------
    private OzonClient? _ozon;
    private CancellationTokenSource? _ozonCts;

    // ---------- CAMERA ----------
    private VideoCapture? _cap;
    private System.Windows.Forms.Timer? _timer;

    // ---------- RECORDING STATE ----------
    private readonly object _recLock = new();
    private VideoWriter? _writer;
    private bool _pendingWriterInit;

    private bool _isRecording;
    private string? _currentBarcode;
    private DateTime _recStartedAt;

    private string? _tempVideoPath;   // local ...part.avi
    private string? _finalVideoPath;  // final storage ...avi
    private string? _metaPath;        // final storage ...json

    public MainForm()
    {
        Text = "PackVideoFix — Упаковка";
        Width = 1300;
        Height = 820;

        LoadConfig();
        RebuildOzonClient();

        // Глобальный перехват сканера
        _scanFilter = new BarcodeMessageFilter(code =>
        {
            BeginInvoke(new Action(() => HandleBarcode(code)));
        });
        Application.AddMessageFilter(_scanFilter);

        // ----- Menu -----
        _menu = new MenuStrip();

        var miSettings = new ToolStripMenuItem("Настройки");
        miSettings.Click += (_, __) =>
        {
            using var f = new SettingsForm(_cfg);
            if (f.ShowDialog(this) == DialogResult.OK)
            {
                _cfg = f.ResultConfig;
                RebuildOzonClient();
                SetStatus($"Настройки сохранены. Station={_cfg.StationName} | Temp={_cfg.TempRootLocal} | Store={_cfg.RecordRoot}");
            }
        };

        var miSearch = new ToolStripMenuItem("Поиск видео");
        miSearch.Click += (_, __) =>
        {
            using var f = new VideoSearchForm(_cfg.RecordRoot);
            f.ShowDialog(this);
        };

        _menu.Items.Add(miSettings);
        _menu.Items.Add(miSearch);
        MainMenuStrip = _menu;
        Controls.Add(_menu);

        // ----- Status bar -----
        _statusStrip = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel("Готово");
        _statusStrip.Items.Add(_statusLabel);
        Controls.Add(_statusStrip);

        // ----- Preview -----
        _preview = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black
        };

        // ----- Right side -----
        _lblPosting = new Label
        {
            Dock = DockStyle.Top,
            Height = 260,
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            Text = "Отправление: —\nТовар: —",
            Padding = new Padding(10)
        };

        _picProduct = new PictureBox
        {
            Dock = DockStyle.Top,
            Height = 260,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.WhiteSmoke
        };

        _btnStopDelete = new Button
        {
            Dock = DockStyle.Bottom,
            Height = 80,
            Text = "СТОП",
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            Enabled = false,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Margin = new Padding(0)
        };
        _btnStopDelete.FlatAppearance.BorderSize = 0;
        _btnStopDelete.Click += (_, __) => StopAndDelete("manual-stop-delete");
        SetStopButtonState(recording: false);

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        right.Controls.Add(_btnStopDelete);
        right.Controls.Add(_picProduct);
        right.Controls.Add(_lblPosting);

        _split = new SplitContainer { Dock = DockStyle.Fill };
        _split.Panel1.Controls.Add(_preview);
        _split.Panel2.Controls.Add(right);

        _split.SizeChanged += (_, __) =>
        {
            if (_split.ClientSize.Width > 0)
                FixSplitter(_split);
        };

        Controls.Add(_split);

        Shown += (_, __) =>
        {
            SetStatus($"Станция: {_cfg.StationName} | Temp: {_cfg.TempRootLocal} | Store: {_cfg.RecordRoot} | Скан=старт/стоп");

            _split.Panel1MinSize = 400;
            _split.Panel2MinSize = 320;
            FixSplitter(_split);

            StartCamera();
        };

        FormClosing += (_, __) =>
        {
            try { _ozonCts?.Cancel(); } catch { }
            try { _ozonCts?.Dispose(); } catch { }
            _ozonCts = null;

            try { _ozon?.Dispose(); } catch { }
            _ozon = null;

            try
            {
                if (_scanFilter != null) Application.RemoveMessageFilter(_scanFilter);
            }
            catch { }

            try { StopAndDelete("app-closing"); } catch { }
            try { StopCamera(); } catch { }
        };
    }

    // =========================
    // Splitter fix
    // =========================
    private static void FixSplitter(SplitContainer split)
    {
        int desiredRight = 380;

        int minLeft = split.Panel1MinSize > 0 ? split.Panel1MinSize : 200;
        int minRight = split.Panel2MinSize > 0 ? split.Panel2MinSize : 200;

        int width = split.ClientSize.Width;
        if (width <= 0) return;

        int left = width - desiredRight;

        int maxLeft = Math.Max(minLeft, width - minRight);
        left = Math.Max(minLeft, Math.Min(left, maxLeft));

        if (left >= minLeft && left <= maxLeft && split.SplitterDistance != left)
        {
            try { split.SplitterDistance = left; } catch { }
        }
    }

    // =========================
    // CONFIG
    // =========================
    private void LoadConfig()
    {
        _cfg = AppConfig.Load();
    }

    private void RebuildOzonClient()
    {
        try { _ozon?.Dispose(); } catch { }
        _ozon = null;

        if (!string.IsNullOrWhiteSpace(_cfg.OzonClientId) && !string.IsNullOrWhiteSpace(_cfg.OzonApiKey))
        {
            _ozon = new OzonClient(_cfg.OzonBaseUrl ?? "https://api-seller.ozon.ru", _cfg.OzonClientId!, _cfg.OzonApiKey!);
        }
    }

    // =========================
    // CAMERA
    // =========================
    private void StartCamera()
    {
        try
        {
            if (_cap != null) return;

            _cap = new VideoCapture(_cfg.CameraIndex, VideoCaptureAPIs.DSHOW);
            if (!_cap.IsOpened())
            {
                _cap.Dispose();
                _cap = null;
                SetStatus("Камера не открылась (проверь CameraIndex 0/1/2).");
                return;
            }

            _cap.Set(VideoCaptureProperties.FrameWidth, 1920);
            _cap.Set(VideoCaptureProperties.FrameHeight, 1080);
            _cap.Set(VideoCaptureProperties.Fps, 30);

            _timer = new System.Windows.Forms.Timer { Interval = 33 };
            _timer.Tick += (_, __) => GrabFrame();
            _timer.Start();

            SetStatus("Камера запущена. Скан 1 = старт, скан 2 (тот же) = стоп.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка камеры: " + ex.Message);
        }
    }

    private void StopCamera()
    {
        try { _timer?.Stop(); } catch { }
        try { _timer?.Dispose(); } catch { }
        _timer = null;

        try { _cap?.Release(); } catch { }
        try { _cap?.Dispose(); } catch { }
        _cap = null;

        var old = _preview.Image;
        _preview.Image = null;
        old?.Dispose();
    }

    private void GrabFrame()
    {
        if (_cap == null) return;

        using var mat = new Mat();
        if (!_cap.Read(mat) || mat.Empty()) return;

        var bmp = BitmapConverter.ToBitmap(mat);
        var old = _preview.Image;
        _preview.Image = bmp;
        old?.Dispose();

        bool autoStop = false;

        lock (_recLock)
        {
            if (!_isRecording)
            {
                // DEBUG: сейчас не записываем
                // SetStatus("DEBUG: GrabFrame -> not recording");
                return;
            }

            SetStatus("DEBUG: GrabFrame -> recording, preparing frame");

            using var frame = new Mat();
            if (mat.Channels() == 4)
                Cv2.CvtColor(mat, frame, ColorConversionCodes.BGRA2BGR);
            else
                mat.CopyTo(frame);

            if (_pendingWriterInit)
            {
                _pendingWriterInit = false;

                if (string.IsNullOrWhiteSpace(_tempVideoPath))
                {
                    _isRecording = false;
                    _currentBarcode = null;
                    SetStopButtonState(false);
                    SetStatus("Ошибка: нет пути временного файла.");
                    return;
                }

                double fps = _cap.Get(VideoCaptureProperties.Fps);
                if (fps <= 1) fps = 30;

                try { _writer?.Dispose(); } catch { }
                _writer = new VideoWriter(_tempVideoPath, FourCC.MJPG, fps, frame.Size());

                if (!_writer.IsOpened())
                {
                    try { _writer?.Dispose(); } catch { }
                    _writer = null;

                    _isRecording = false;
                    _currentBarcode = null;

                    SetStopButtonState(false);
                    SetStatus("Не удалось открыть запись (VideoWriter MJPG).");
                    return;
                }

                SetStatus($"Запись началась: {_currentBarcode} (temp={_tempVideoPath})");
            }

            SetStatus("DEBUG: GrabFrame -> writer write frame");
            _writer?.Write(frame);

            if (_cfg.MaxClipSeconds > 0)
            {
                var elapsed = DateTime.Now - _recStartedAt;
                if (elapsed.TotalSeconds >= _cfg.MaxClipSeconds)
                    autoStop = true;
            }
        } // ← закрываем lock

        if (autoStop)
            StopAndFinalize("auto-max-seconds");
    } // ← закрываем метод

    // =========================
    // BARCODE WORKFLOW
    // =========================
    private void HandleBarcode(string barcode)
    {
        _ = UpdateOzonInfoAsync(barcode);

        if (_cap == null)
        {
            SetStatus("Камера ещё не готова.");
            return;
        }

        bool shouldFinalize = false;

        lock (_recLock)
        {
            if (!_isRecording)
            {
                StartRecordingFlow_WithExistingCheck(barcode);
                return;
            }

            if (!string.Equals(_currentBarcode, barcode, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus($"Идёт запись {_currentBarcode}. Скан {barcode} игнорирован.");
                return;
            }

            shouldFinalize = true;
        }

        if (shouldFinalize)
            StopAndFinalize("second-scan");
    }

    private async Task UpdateOzonInfoAsync(string barcode)
    {
        try { _ozonCts?.Cancel(); } catch { }
        try { _ozonCts?.Dispose(); } catch { }
        _ozonCts = new CancellationTokenSource();
        var ct = _ozonCts.Token;

        _lblPosting.Text = $"Отправление: —\nШтрихкод: {barcode}";
        SetProductImage(null);

        if (_ozon == null)
        {
            _lblPosting.Text =
                "OZON НЕ НАСТРОЕН\n" +
                $"barcode = {barcode}";
            return;
        }

        var (ok, msg, posting) = await _ozon.TryGetPostingByBarcodeAsync(barcode, ct);
        if (ct.IsCancellationRequested) return;

        if (ok && !string.IsNullOrWhiteSpace(posting))
        {
            _lblPosting.Text =
                $"Отправление: {posting}\n" +
                $"Штрихкод: {barcode}";
        }
        else
        {
            _lblPosting.Text =
                $"Отправление: —\n" +
                $"Штрихкод: {barcode}\n" +
                $"(Ozon: {msg})";   // без Trim1Line
        }
    }

    private static string Trim1Line(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "ошибка";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 80 ? s.Substring(0, 80) + "…" : s;
    }

    private void SetProductImage(Image? img)
    {
        var old = _picProduct.Image;
        _picProduct.Image = img;
        old?.Dispose();
    }

    private void StartRecordingFlow_WithExistingCheck(string barcode)
    {
        var existing = FindClipsByBarcode(barcode, limit: 20);

        if (existing.Count > 0)
        {
            var res = MessageBox.Show(
                $"Для штрихкода {barcode} уже найдено {existing.Count} запис(ей).\n\n" +
                "Да = Заменить (переместить старые в _replaced)\n" +
                "Нет = Сохранить как новую версию\n" +
                "Отмена = Не начинать запись",
                "Штрихкод уже существует",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning);

            if (res == DialogResult.Cancel) return;

            if (res == DialogResult.Yes)
                ArchiveExisting(barcode, existing);
        }

        StartRecording(barcode);
    }

    private void StartRecording(string barcode)
    {
        if (_cap == null) return;

        var now = DateTime.Now;
        var dateFolder = now.ToString("yyyy-MM-dd");
        var safeBarcode = MakeSafeFilePart(barcode);
        var stamp = now.ToString("yyyyMMdd_HHmmss_fff");
        var baseName = $"{safeBarcode}_{stamp}_{_cfg.StationName}";

        // TEMP (local)
        var tempFolder = Path.Combine(_cfg.TempRootLocal, dateFolder, _cfg.StationName);
        Directory.CreateDirectory(tempFolder);

        // FINAL (storage, can be NAS)
        var finalFolder = Path.Combine(_cfg.RecordRoot, dateFolder, _cfg.StationName);
        Directory.CreateDirectory(finalFolder);

        _tempVideoPath = Path.Combine(tempFolder, baseName + ".part.avi");
        _finalVideoPath = Path.Combine(finalFolder, baseName + ".avi");
        _metaPath = Path.Combine(finalFolder, baseName + ".json");

        lock (_recLock)
        {
            try { _writer?.Release(); } catch { }
            try { _writer?.Dispose(); } catch { }
            _writer = null;

            _isRecording = true;
            _currentBarcode = barcode;
            _recStartedAt = now;

            _pendingWriterInit = true; // важно для каждой новой записи
            SetStopButtonState(recording: true);
        }
        SetStatus($"DEBUG: StartRecording -> isRecording={_isRecording}, temp={_tempVideoPath}");

        SetStatus($"Ожидание кадра... (старт записи: {barcode})");
        DropFocusFromButton();
    }

    private void StopAndFinalize(string reason)
    {
        string? temp;
        string? final;
        string? meta;
        string? barcode;
        DateTime startedAt;

        lock (_recLock)
        {
            if (!_isRecording) return;

            temp = _tempVideoPath;
            final = _finalVideoPath;
            meta = _metaPath;
            barcode = _currentBarcode;
            startedAt = _recStartedAt;

            try { _writer?.Release(); } catch { }
            try { _writer?.Dispose(); } catch { }
            _writer = null;

            _isRecording = false;
            _pendingWriterInit = false;
            _currentBarcode = null;
            _tempVideoPath = null;
            _finalVideoPath = null;
            _metaPath = null;

            SetStopButtonState(recording: false);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(temp) || string.IsNullOrWhiteSpace(final) ||
                string.IsNullOrWhiteSpace(meta) || string.IsNullOrWhiteSpace(barcode))
            {
                SetStatus("Ошибка финализации: нет путей/штрихкода.");
                return;
            }

            if (!File.Exists(temp))
            {
                SetStatus($"Файл записи не найден (.part.avi). Путь: {temp}");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            Directory.CreateDirectory(Path.GetDirectoryName(meta)!);

            MoveOrCopy(temp, final);

            var clip = new ClipMeta
            {
                Barcode = barcode,
                Station = _cfg.StationName,
                StartedAt = startedAt,
                FinishedAt = DateTime.Now,
                Status = "OK",
                Reason = reason,
                VideoPath = final
            };

            File.WriteAllText(meta, JsonSerializer.Serialize(clip, new JsonSerializerOptions { WriteIndented = true }));

            SetStatus($"Запись сохранена: {Path.GetFileName(final)}");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка сохранения: " + ex.Message);
        }
        finally
        {
            DropFocusFromButton();
        }
    }

    private static void MoveOrCopy(string src, string dst)
    {
        if (File.Exists(dst)) File.Delete(dst);

        try
        {
            File.Move(src, dst);
        }
        catch
        {
            File.Copy(src, dst, overwrite: true);
            try { File.Delete(src); } catch { }
        }
    }

    // STOP button = stop + delete current recording
    private void StopAndDelete(string reason)
    {
        string? temp;
        string? barcode;

        lock (_recLock)
        {
            if (!_isRecording) return;

            temp = _tempVideoPath;
            barcode = _currentBarcode;

            try { _writer?.Release(); } catch { }
            try { _writer?.Dispose(); } catch { }
            _writer = null;

            _isRecording = false;
            _pendingWriterInit = false;
            _currentBarcode = null;
            _tempVideoPath = null;
            _finalVideoPath = null;
            _metaPath = null;

            SetStopButtonState(recording: false);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(temp) && File.Exists(temp))
                File.Delete(temp);

            SetStatus($"Удалено (стоп): {barcode} ({reason})");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка удаления: " + ex.Message);
        }
        finally
        {
            DropFocusFromButton();
        }
    }

    private void DropFocusFromButton()
    {
        BeginInvoke(new Action(() =>
        {
            ActiveControl = null;
            Focus();
        }));
    }

    private void SetStopButtonState(bool recording)
    {
        if (recording)
        {
            _btnStopDelete.Enabled = true;
            _btnStopDelete.BackColor = Color.Red;
            _btnStopDelete.ForeColor = Color.White;
        }
        else
        {
            _btnStopDelete.Enabled = false;
            _btnStopDelete.BackColor = Color.Gainsboro;
            _btnStopDelete.ForeColor = Color.Black;
        }
    }

    // =========================
    // SEARCH / LIST
    // =========================
    internal static ClipMeta? TryReadMeta(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ClipMeta>(text);
        }
        catch
        {
            return null;
        }
    }

    private List<ClipMeta> FindClipsByBarcode(string barcodePart, int limit)
    {
        var result = new List<ClipMeta>();
        if (!Directory.Exists(_cfg.RecordRoot)) return result;

        foreach (var f in Directory.EnumerateFiles(_cfg.RecordRoot, "*.json", SearchOption.AllDirectories))
        {
            var meta = TryReadMeta(f);
            if (meta == null) continue;

            if (!string.IsNullOrWhiteSpace(meta.Barcode) &&
                meta.Barcode.Contains(barcodePart, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(meta);
                if (result.Count >= limit) break;
            }
        }

        return result;
    }

    // =========================
    // ARCHIVE (REPLACE)
    // =========================
    private void ArchiveExisting(string barcode, List<ClipMeta> existing)
    {
        try
        {
            var archiveRoot = Path.Combine(_cfg.RecordRoot, "_replaced", MakeSafeFilePart(barcode));
            Directory.CreateDirectory(archiveRoot);

            foreach (var m in existing)
            {
                if (!string.IsNullOrWhiteSpace(m.VideoPath) && File.Exists(m.VideoPath))
                {
                    var dstVideo = Path.Combine(archiveRoot, Path.GetFileName(m.VideoPath));
                    MoveWithOverwrite(m.VideoPath, dstVideo);
                }

                if (!string.IsNullOrWhiteSpace(m.VideoPath))
                {
                    var jsonGuess = Path.ChangeExtension(m.VideoPath, ".json");
                    if (File.Exists(jsonGuess))
                    {
                        var dstJson = Path.Combine(archiveRoot, Path.GetFileName(jsonGuess));
                        MoveWithOverwrite(jsonGuess, dstJson);
                    }
                }
            }

            SetStatus($"Старые записи по {barcode} перенесены в _replaced.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка архивации: " + ex.Message);
        }
    }

    private static void MoveWithOverwrite(string src, string dst)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (File.Exists(dst)) File.Delete(dst);
        File.Move(src, dst);
    }

    private static string MakeSafeFilePart(string s)
    {
        foreach (var ch in Path.GetInvalidFileNameChars())
            s = s.Replace(ch, '_');
        return s.Trim();
    }

    private void SetStatus(string text) => _statusLabel.Text = text;
}

// =========================
// GLOBAL BARCODE INPUT FILTER
// =========================
internal sealed class BarcodeMessageFilter : IMessageFilter
{
    private readonly Action<string> _onScan;
    private string _buf = "";
    private DateTime _last = DateTime.MinValue;

    public BarcodeMessageFilter(Action<string> onScan) => _onScan = onScan;

    public bool PreFilterMessage(ref Message m)
    {
        const int WM_CHAR = 0x0102;
        if (m.Msg != WM_CHAR) return false;

        char ch = (char)m.WParam;

        var now = DateTime.Now;
        if ((now - _last).TotalMilliseconds > 200)
            _buf = "";
        _last = now;

        if (ch == '\r' || ch == '\n')
        {
            var code = _buf.Trim();
            _buf = "";
            if (code.Length > 0) _onScan(code);
            return true;
        }

        if (!char.IsControl(ch))
            _buf += ch;

        return false;
    }
}

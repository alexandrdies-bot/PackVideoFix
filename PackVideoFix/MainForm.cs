// File: /mnt/data/MainForm.cs
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;          // ← ДОБАВЬ ЭТУ СТРОКУ
using System.Net.Http; // ⟵ для скачивания фото
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

    private readonly MenuStrip _menu;
    private readonly StatusStrip _statusStrip;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly SplitContainer _split;

    // верхний блок справа
    private readonly Label _lblPosting;          // старый мультистрочный label для ошибок/служебных сообщений
    private readonly TableLayoutPanel _postingLayout;
    private readonly Label _lblPostingNumber;
    private readonly Label _lblBarcode;
    private readonly Label _lblStatus;
    private readonly Label _lblItemsCount;

    private readonly Panel _pnlImages;
    private readonly PictureBox _picProduct;
    private readonly Button _btnStopDelete;



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

        // современный системный шрифт, как в Windows 10/11
        Font = new Font("Segoe UI", 10F, FontStyle.Regular);
        StartPosition = FormStartPosition.CenterScreen;

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
        // Панель сверху для отправления/штрихкода/статуса/количества
        var postingHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 260,
            Padding = new Padding(10, 10, 10, 0)
        };

        // Старый label для текстовых ошибок и служебных сообщений
        _lblPosting = new Label
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            Text = "Отправление: —\nШтрихкод: —",
            TextAlign = ContentAlignment.MiddleLeft
        };

        // Новая таблица 1×4 для «красивого» режима
        _postingLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4
        };
        // чуть подвигаем доли по высоте
        _postingLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 28)); // номер
        _postingLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 17)); // штрихкод
        _postingLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 30)); // статус
        _postingLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25)); // количество

        _lblPostingNumber = new Label
        {
            Dock = DockStyle.Fill,
            Text = "—",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 32f, FontStyle.Regular),
            Margin = new Padding(0, 0, 0, 4)   // небольшой отступ вниз
        };

        _lblBarcode = new Label
        {
            Dock = DockStyle.Fill,
            Text = "",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 16f, FontStyle.Regular),
            Margin = new Padding(0, 0, 0, 12)  // побольше отступ под штрихкодом
        };

        _lblStatus = new Label
        {
            Dock = DockStyle.Fill,
            Text = "",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 24f, FontStyle.Bold),
            Margin = new Padding(0, 12, 0, 8)  // статус висит "сам по себе", как на образце
        };

        _lblItemsCount = new Label
        {
            Dock = DockStyle.Fill,
            Text = "",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 18f, FontStyle.Regular),
            Margin = new Padding(0, 8, 0, 0)   // чуть отодвигаем от статуса
        };


        _postingLayout.Controls.Add(_lblPostingNumber, 0, 0);
        _postingLayout.Controls.Add(_lblBarcode, 0, 1);
        _postingLayout.Controls.Add(_lblStatus, 0, 2);
        _postingLayout.Controls.Add(_lblItemsCount, 0, 3);

        postingHeader.Controls.Add(_postingLayout);
        postingHeader.Controls.Add(_lblPosting);
        _lblPosting.Visible = false; // по умолчанию показываем красивый режим

        _picProduct = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Normal,
            BackColor = Color.WhiteSmoke
        };

        _pnlImages = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.WhiteSmoke
        };
        _pnlImages.Controls.Add(_picProduct);


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
        right.Controls.Add(_btnStopDelete);  // снизу
        right.Controls.Add(_pnlImages);      // центр с автоскроллом
        right.Controls.Add(postingHeader);   // сверху (таблица + старый label)




                _split = new SplitContainer { Dock = DockStyle.Fill };
        _split.Panel1.Controls.Add(_preview);
        _split.Panel2.Controls.Add(right);

        _split.SizeChanged += (_, __) =>
        {
            if (_split.ClientSize.Width > 0)
                FixSplitter(_split);
        };

        _split.Panel2.Resize += (_, __) => AdjustPostingHeaderFonts();

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

    private void ShowFancyPostingHeader(bool fancy)
    {
        // fancy = true  -> показываем 4 строки разными размерами
        // fancy = false -> показываем только старый мультистрочный _lblPosting
        if (_postingLayout != null)
            _postingLayout.Visible = fancy;
        if (_lblPosting != null)
            _lblPosting.Visible = !fancy;
    }

    /// <summary>
    /// Подбирает размер шрифта так, чтобы текст влез по ширине label.
    /// Работает только по ширине (без учёта высоты).
    /// </summary>
    private void AdjustLabelFontToFit(Label label, int maxFontSize, int minFontSize)
    {
        if (label == null || label.Parent == null || string.IsNullOrEmpty(label.Text))
            return;

        // Если ширина ещё не посчитана (например, форма только создаётся) — выходим.
        if (label.Width <= 0)
            return;

        float size = maxFontSize;
        Font baseFont = label.Font;
        Font bestFont = baseFont;

        using (var g = label.CreateGraphics())
        {
            while (size >= minFontSize)
            {
                using (var testFont = new Font(baseFont.FontFamily, size, baseFont.Style))
                {
                    var textSize = TextRenderer.MeasureText(
                      g,
                      label.Text,
                      testFont,
                      new System.Drawing.Size(int.MaxValue, int.MaxValue),
                      TextFormatFlags.SingleLine);


                    if (textSize.Width <= label.Width - 4)
                    {
                        bestFont = (Font)testFont.Clone();
                        break;
                    }
                }

                size -= 1.0f;
            }
        }

        label.Font = bestFont;
    }

    /// <summary>
    /// Подгоняет размеры шрифтов всех заголовков справа
    /// под текущую ширину панели.
    /// </summary>
    private void AdjustPostingHeaderFonts()
    {
        // Номер отправления
        AdjustLabelFontToFit(_lblPostingNumber, 32, 16);

        // Штрихкод
        AdjustLabelFontToFit(_lblBarcode, 18, 10);

        // Статус — только если он в одну строку (для "ОТМЕНЁН\nНЕ УПАКОВЫВАТЬ!" не трогаем)
        if (!string.IsNullOrEmpty(_lblStatus.Text) && !_lblStatus.Text.Contains("\n"))
            AdjustLabelFontToFit(_lblStatus, 26, 12);

        // "Товаров в отправлении..."
        AdjustLabelFontToFit(_lblItemsCount, 18, 10);
    }

    private void SetPostingHeaderColor(Color color)
    {
        _lblPosting.ForeColor = color;
        _lblPostingNumber.ForeColor = color;
        _lblBarcode.ForeColor = color;
        _lblStatus.ForeColor = color;
        _lblItemsCount.ForeColor = color;
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
                return;
            }

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

            _writer?.Write(frame);

            if (_cfg.MaxClipSeconds > 0)
            {
                var elapsed = DateTime.Now - _recStartedAt;
                if (elapsed.TotalSeconds >= _cfg.MaxClipSeconds)
                    autoStop = true;
            }
        }

        if (autoStop)
            StopAndFinalize("auto-max-seconds");
    }

    // =========================
    // BARCODE WORKFLOW
    // =========================
    private void HandleBarcode(string barcode)
    {
        // Камера должна быть готова для записи
        if (_cap == null)
        {
            SetStatus("Камера ещё не готова.");
            return;
        }

        bool startNew = false;
        bool finalizeCurrent = false;
        bool needPrompt = false;
        string? recordingBarcode = null;

        lock (_recLock)
        {
            if (!_isRecording)
            {
                // Ничего не пишется — первый скан -> старт новой записи
                startNew = true;
            }
            else
            {
                // Уже идёт запись
                recordingBarcode = _currentBarcode;

                if (string.Equals(_currentBarcode, barcode, StringComparison.OrdinalIgnoreCase))
                {
                    // Повторный скан того же штрихкода -> стоп записи и сохранение
                    finalizeCurrent = true;
                }
                else
                {
                    // Во время записи отсканирован другой штрихкод -> спрашиваем оператора
                    needPrompt = true;
                }
            }
        }

        // 1) Старт новой записи (проверка статуса в Ozon)
        if (startNew)
        {
            // Внутри UpdateOzonInfoAsync решаем, можно ли начинать запись по статусу.
            _ = UpdateOzonInfoAsync(barcode, fromScanStart: true);
            return;
        }


        // 2) Стоп записи по повторному скану того же штрихкода
        if (finalizeCurrent)
        {
            StopAndFinalize("second-scan");
            return;
        }

        // 3) Идёт запись для другого штрихкода — показываем диалог
        if (needPrompt)
        {
            // true = "Завершить", false = "Продолжить"
            bool finish = ShowRecordingConflictDialog(recordingBarcode ?? "?", barcode);

            if (finish)
            {
                // Завершаем и УДАЛЯЕМ текущую запись
                StopAndDelete("stop-from-other-barcode");
                SetStatus("Текущая запись остановлена и удалена. Отсканируйте нужный штрихкод ещё раз.");
            }
            else
            {
                // Продолжаем текущую запись, новый штрихкод игнорируем
                SetStatus($"Продолжается запись для {recordingBarcode}.");
            }

            return;
        }

        // На всякий случай — сюда попадать не должны
        SetStatus("Неизвестное состояние записи при обработке штрихкода.");
    }

    /// <summary>
    /// Диалог "уже идёт запись".
    /// true = пользователь выбрал "Завершить", false = "Продолжить".
    /// </summary>
    private bool ShowRecordingConflictDialog(string currentBarcode, string newBarcode)
    {
        using (var form = new Form())
        {
            form.Text = "Уже идёт запись";
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterParent;
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.ShowInTaskbar = false;
            form.ClientSize = new System.Drawing.Size(520, 240);

            var lbl = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.TopLeft,
                Padding = new Padding(10),
                Text =
                    $"Сейчас уже идёт запись для штрихкода: {currentBarcode}.\n\n" +
                    $"Новый штрихкод: {newBarcode}.\n\n" +
                    "Нажмите \"Завершить\", чтобы остановить и удалить текущую запись.\n" +
                    "После этого отсканируйте нужный штрихкод ещё раз.\n\n" +
                    "Нажмите \"Продолжить\", чтобы игнорировать новый штрихкод и продолжить текущую запись."
            };

            var btnFinish = new Button
            {
                Text = "Завершить",
                DialogResult = DialogResult.OK,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(5, 0, 0, 0)
            };

            var btnContinue = new Button
            {
                Text = "Продолжить",
                DialogResult = DialogResult.Cancel,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(5, 0, 0, 0)
            };

            var panelButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 50,
                Padding = new Padding(10),
                AutoSize = false
            };

            panelButtons.Controls.Add(btnContinue);
            panelButtons.Controls.Add(btnFinish);

            form.Controls.Add(panelButtons);
            form.Controls.Add(lbl);

            form.AcceptButton = btnFinish;   // Enter = "Завершить"
            form.CancelButton = btnContinue; // Esc = "Продолжить"

            var result = form.ShowDialog(this);
            return result == DialogResult.OK; // true = Завершить, false = Продолжить/закрыто
        }
    }



    private async Task UpdateOzonInfoAsync(string barcode, bool fromScanStart = false)
    {
        try { _ozonCts?.Cancel(); } catch { }
        try { _ozonCts?.Dispose(); } catch { }
        _ozonCts = new CancellationTokenSource();
        var ct = _ozonCts.Token;

        SetPostingHeaderColor(Color.Black);
        ShowFancyPostingHeader(true);

        _lblPostingNumber.Text = "—";
        _lblBarcode.Text = barcode;
        _lblStatus.Text = "";
        _lblItemsCount.Text = "";

        _lblPosting.Text = $"Отправление: —\nШтрихкод: {barcode}"; // на всякий случай, для режима ошибок
        SetProductImage(null);


        if (_ozon == null)
        {
            ShowFancyPostingHeader(false);
            SetPostingHeaderColor(Color.Black);
            _lblPosting.Text =
                "OZON НЕ НАСТРОЕН\n" +
                $"barcode = {barcode}";
            return;
        }


        // Новый вызов: номер + список url картинок всех товаров в отправлении + статус
        var (ok, msg, posting, status, imgUrls) = await _ozon.TryGetPostingAndImagesByBarcodeAsync(barcode, ct);
        if (ct.IsCancellationRequested) return;

        // по умолчанию чёрный цвет, чтобы после отменённого заказа всё вернуть
        SetPostingHeaderColor(Color.Black);

        if (!ok || string.IsNullOrWhiteSpace(posting))
        {
            ShowFancyPostingHeader(false);
            _lblPosting.Text =
                $"Отправление: —\n" +
                $"Штрихкод: {barcode}\n" +
                $"(Ozon: {msg})";
            SetProductImage(null);
            return;
        }


        // --- Отправление найдено ---
        ShowFancyPostingHeader(true);

        _lblPostingNumber.Text = posting;
        _lblBarcode.Text = barcode;
        _lblStatus.Text = "";
        _lblItemsCount.Text = "";

        // по умолчанию запись разрешена
        bool canRecord = true;

        // Добавляем строку статуса...
        if (!string.IsNullOrWhiteSpace(status))
        {
            var sLower = status.Trim().ToLowerInvariant();

            // Статусы, при которых МОЖНО упаковывать
            bool isAwaiting =
                sLower.StartsWith("awaiting_")   // английские статусы
                || sLower == "ожидает";          // иногда приходит по-русски

            canRecord = isAwaiting;

            // Текст статуса для экрана
            string statusRu;

            if (sLower == "cancelled")
            {
                // отдельный случай — отменён
                statusRu = "ОТМЕНЁН";
            }
            else if (isAwaiting)
            {
                // ВСЕ статусы, при которых можно паковать, показываем одинаково
                statusRu = "ГОТОВ К УПАКОВКЕ";
            }
            else
            {
                // всё остальное — через переводчик как есть
                statusRu = TranslateOzonStatusToRussian(status);
            }

            // для красивого вида делаем ВЕРХНИЙ РЕГИСТР
            statusRu = statusRu.ToUpperInvariant();

            if (sLower == "cancelled")
            {
                // ЯВНОЕ предупреждение, что упаковывать нельзя
                SetPostingHeaderColor(Color.Red);
                _lblStatus.Text = statusRu + "\nНЕ УПАКОВЫВАТЬ!";
                try
                {
                    System.Media.SystemSounds.Exclamation.Play();
                }
                catch { /* звук не критичен */ }
            }
            else
            {
                SetPostingHeaderColor(Color.Black);
                _lblStatus.Text = statusRu;

                if (!canRecord)
                {
                    // пояснение отдельной строкой под статусом
                    _lblStatus.Text += "\n(запись видео не требуется)";
                }
            }
        }
        else
        {
            SetPostingHeaderColor(Color.Black);
        }



        if (imgUrls != null && imgUrls.Count > 0)
        {
            _lblItemsCount.Text = $"Товаров в отправлении: {imgUrls.Count}";
        }
        else
        {
            _lblItemsCount.Text = "";
        }

        // Все тексты выставлены — подгоняем шрифты под текущую ширину
        AdjustPostingHeaderFonts();

        // Если это вызов из сканера (первый скан) — решаем, начинать ли запись
        if (fromScanStart)
        {
            bool shouldStart = false;

            lock (_recLock)
            {
                // Запускаем запись только если сейчас ничего не пишется
                // и статус разрешает упаковку (awaiting_*).
                if (!_isRecording && canRecord)
                    shouldStart = true;
            }

            if (shouldStart)
            {
                StartRecordingFlow_WithExistingCheck(barcode);
            }
            else if (!canRecord)
            {
                // Статус не "готов к отгрузке" — запись не начинаем
                SetStatus("Запись не начата: статус отправления не позволяет упаковку.");
            }
        }



        if (imgUrls == null || imgUrls.Count == 0)
        {
            SetProductImage(null);
            return;
        }

        try
        {
            using var http = new HttpClient(); // без ozon-хедеров, чтобы не ломать CDN

            // группируем по URL, чтобы понять, сколько одинаковых товаров
            var groups = imgUrls
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .GroupBy(u => u, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (groups.Count == 0)
            {
                SetProductImage(null);
                return;
            }

            // дальше оставляем твой код: режим одного товара / грид 3×N
            // ...


            // ----- РЕЖИМ ОДНОГО ТОВАРА -----
            if (groups.Count == 1)
            {
                var g = groups[0];
                var url = g.Key;
                var count = g.Count(); // количество одинаковых товаров

                var bytes = await http.GetByteArrayAsync(url, ct);
                if (ct.IsCancellationRequested) return;

                using var ms = new MemoryStream(bytes);
                using var img = Image.FromStream(ms);

                // если count > 1 — рисуем цифру в углу, иначе просто картинка
                var big = BuildSingleImageWithCount(img, count > 1 ? count : 0);
                SetProductImage(big);
                return;
            }

            // ----- НЕСКОЛЬКО РАЗНЫХ ТОВАРОВ — ГРИД 3×N -----
            var images = new List<Image>();
            var counts = new List<int>();

            const int maxGroups = 30; // максимум разных картинок на экране

            for (int i = 0; i < groups.Count && i < maxGroups; i++)
            {
                var g = groups[i];
                var url = g.Key;
                var count = g.Count();

                var bytes = await http.GetByteArrayAsync(url, ct);
                if (ct.IsCancellationRequested) return;

                using var ms = new MemoryStream(bytes);
                using var img = Image.FromStream(ms);

                images.Add((Image)img.Clone());
                counts.Add(count);
            }

            if (images.Count > 0)
            {
                var collage = BuildImageGrid(images, counts);
                SetProductImage(collage);
            }
            else
            {
                SetProductImage(null);
            }

            // исходные отдельные картинки больше не нужны
            foreach (var img in images)
                img.Dispose();
        }
        catch
        {
            // намеренно тихо — картинка не критична
            SetProductImage(null);
        }
    }

    private static string TranslateOzonStatusToRussian(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return "НЕИЗВЕСТЕН";

        var s = status.Trim().ToLowerInvariant();

        switch (s)
        {
            case "awaiting_packaging":
            case "ожидает":
                return "ГОТОВ К УПАКОВКЕ";

            case "awaiting_deliver":
            case "awaiting_delivery":
            case "awaiting_deliver_pickup":
                return "ОЖИДАЕТ ДОСТАВКУ";
                            
            case "awaiting_approve":
                return "ОЖИДАЕТ ПОДТВЕРЖДЕНИЯ";
            case "awaiting_verification":
                return "ОЖИДАЕТ ПРОВЕРКИ";
            case "delivered":
                return "ДОСТАВЛЕН";
            case "cancelled":
                return "ОТМЕНЁН";
            default:
                return status.Replace('_', ' ');
        }

    }

    private static string Trim1Line(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "ошибка";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 80 ? s.Substring(0, 80) + "…" : s;
    }

    // Рисуем грид: 3 картинки в ряд, остальные — следующими рядами вниз.
    // Ширина подстраивается под правую панель, высота — rows * cellHeight.
    private void SetProductImage(Image? img)
    {
        var old = _picProduct.Image;
        _picProduct.Image = img;

        if (_pnlImages != null)
        {
            if (img != null)
            {
                _picProduct.SizeMode = PictureBoxSizeMode.Normal;
                _picProduct.Size = img.Size;
            }
            else
            {
                _picProduct.Size = _pnlImages.ClientSize;
            }
        }

        old?.Dispose();

        // При смене картинки возвращаем скролл в начало,
        // чтобы первый ряд всегда был виден.
        _pnlImages?.ScrollControlIntoView(_picProduct);
    }

    /// <summary>
    /// Строит сетку 3×N из картинок с цифрой количества в углу.
    /// </summary>
    private Image BuildImageGrid(IReadOnlyList<Image> images, IReadOnlyList<int> counts)
    {
        if (images == null || images.Count == 0)
            throw new ArgumentException(nameof(images));

        int columns = 3;
        int padding = 5;

        int panelWidth = _pnlImages?.ClientSize.Width ?? 320;
        if (panelWidth < 100) panelWidth = 320;

        int cellWidth = (panelWidth - padding * (columns + 1)) / columns;
        if (cellWidth < 40) cellWidth = 40;
        int cellHeight = cellWidth;

        int n = images.Count;
        int rows = (int)Math.Ceiling(n / (double)columns);

        int totalHeight = rows * cellHeight + padding * (rows + 1);

        var bmp = new Bitmap(panelWidth, totalHeight);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            for (int i = 0; i < n; i++)
            {
                int row = i / columns;
                int col = i % columns;

                int x = padding + col * (cellWidth + padding);
                int y = padding + row * (cellHeight + padding);

                var src = images[i];
                if (src == null || src.Width <= 0 || src.Height <= 0)
                    continue;

                // Масштабируем, сохраняя пропорции, чтобы вписать в ячейку
                float scale = Math.Min(
                    (float)cellWidth / src.Width,
                    (float)cellHeight / src.Height);

                int w = Math.Max(1, (int)(src.Width * scale));
                int h = Math.Max(1, (int)(src.Height * scale));

                // Центруем картинку внутри ячейки
                int dx = x + (cellWidth - w) / 2;
                int dy = y + (cellHeight - h) / 2;

                g.DrawImage(src, new Rectangle(dx, dy, w, h));

                int count = (counts != null && i < counts.Count) ? counts[i] : 1;
                if (count > 1)
                {
                    string txt = count.ToString();
                    using var font = new Font("Segoe UI", 10, FontStyle.Bold);
                    var size = g.MeasureString(txt, font);

                    int boxW = (int)size.Width + 8;
                    int boxH = (int)size.Height + 4;
                    int bx = x + cellWidth - boxW - 2;  // привязка к ячейке, а не к самой картинке
                    int by = y + cellHeight - boxH - 2;

                    using (var brushBg = new SolidBrush(Color.FromArgb(200, Color.Black)))
                        g.FillRectangle(brushBg, bx, by, boxW, boxH);

                    using (var pen = new Pen(Color.White))
                        g.DrawRectangle(pen, bx, by, boxW, boxH);

                    using (var brushText = new SolidBrush(Color.White))
                        g.DrawString(txt, font, brushText, bx + 4, by + 2);
                }

            }
        }

        return bmp;
    }

    /// <summary>
    /// Одна большая картинка, вписанная в панель, с цифрой количества в правом нижнем углу (если count > 1).
    /// </summary>
    private Image BuildSingleImageWithCount(Image src, int count)
    {
        if (src == null)
            throw new ArgumentNullException(nameof(src));

        int panelWidth = _pnlImages?.ClientSize.Width ?? 380;
        int panelHeight = _pnlImages?.ClientSize.Height ?? 400;
        if (panelWidth < 50) panelWidth = 50;
        if (panelHeight < 50) panelHeight = 50;

        int padding = 10;
        int maxWidth = Math.Max(10, panelWidth - padding * 2);
        int maxHeight = Math.Max(10, panelHeight - padding * 2); // можно не использовать, оставим на будущее

        // Масштабируем ТОЛЬКО по ширине, чтобы не "поджимать" по высоте.
        // Если картинка уже уже панели — не увеличиваем, оставляем 1:1.
        float scale = 1f;
        if (src.Width > maxWidth)
            scale = (float)maxWidth / src.Width;

        int w = Math.Max(1, (int)(src.Width * scale));
        int h = Math.Max(1, (int)(src.Height * scale));


        // итоговый холст: не меньше панели по ширине и высоте
        int bmpW = Math.Max(panelWidth, w + padding * 2);
        int bmpH = Math.Max(panelHeight, h + padding * 2);

        var bmp = new Bitmap(bmpW, bmpH);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // центрируем картинку
            int x = (bmpW - w) / 2;
            int y = (bmpH - h) / 2;

            g.DrawImage(src, new Rectangle(x, y, w, h));

            if (count > 1)
            {
                string txt = count.ToString();
                using var font = new Font("Segoe UI", 16, FontStyle.Bold);
                var size = g.MeasureString(txt, font);

                int boxW = (int)size.Width + 10;
                int boxH = (int)size.Height + 6;
                int bx = x + w - boxW - 4;
                int by = y + h - boxH - 4;

                using (var brushBg = new SolidBrush(Color.FromArgb(200, Color.Black)))
                    g.FillRectangle(brushBg, bx, by, boxW, boxH);

                using (var pen = new Pen(Color.White))
                    g.DrawRectangle(pen, bx, by, boxW, boxH);

                using (var brushText = new SolidBrush(Color.White))
                    g.DrawString(txt, font, brushText, bx + 5, by + 3);
            }
        }

        return bmp;
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

        var tempFolder = Path.Combine(_cfg.TempRootLocal, dateFolder, _cfg.StationName);
        Directory.CreateDirectory(tempFolder);

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

            _pendingWriterInit = true;
            SetStopButtonState(recording: true);
        }
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
            _btnStopDelete.BackColor = System.Drawing.Color.Red;
            _btnStopDelete.ForeColor = System.Drawing.Color.White;
        }
        else
        {
            _btnStopDelete.Enabled = false;
            _btnStopDelete.BackColor = System.Drawing.Color.Gainsboro;
            _btnStopDelete.ForeColor = System.Drawing.Color.Black;
        }
    }

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
            return true; // почему: гасим эхо в активном контроле
        }

        if (!char.IsControl(ch))
            _buf += ch;

        return false;
    }
}

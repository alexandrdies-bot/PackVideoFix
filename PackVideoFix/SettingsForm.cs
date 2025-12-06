using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PackVideoFix;

public partial class SettingsForm : Form
{
    private readonly AppConfig _cfg;

    private readonly TextBox _tbRecordRoot;
    private readonly TextBox _tbTempRootLocal;
    private readonly TextBox _tbStation;
    private readonly NumericUpDown _nudCamIndex;
    private readonly NumericUpDown _nudMaxSeconds;

    private readonly TextBox _tbOzonClientId;
    private readonly TextBox _tbOzonApiKey;

    public AppConfig ResultConfig => _cfg;

    public SettingsForm(AppConfig cfg)
    {
        _cfg = Clone(cfg);

        Text = "Настройки";
        StartPosition = FormStartPosition.CenterParent;
        Width = 720;
        Height = 520;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 3,
            RowCount = 10
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));

        // RecordRoot (итоговые видео)
        root.Controls.Add(MkLabel("Папка видео (итог):"), 0, 0);
        _tbRecordRoot = new TextBox { Dock = DockStyle.Fill, Text = _cfg.RecordRoot };
        root.Controls.Add(_tbRecordRoot, 1, 0);
        var btnPickRecord = new Button { Text = "Выбрать…", Dock = DockStyle.Fill };
        btnPickRecord.Click += (_, __) => PickFolder(_tbRecordRoot);
        root.Controls.Add(btnPickRecord, 2, 0);

        // TempRootLocal (только локально)
        root.Controls.Add(MkLabel("Папка TEMP (только локально):"), 0, 1);
        _tbTempRootLocal = new TextBox { Dock = DockStyle.Fill, Text = _cfg.TempRootLocal };
        root.Controls.Add(_tbTempRootLocal, 1, 1);
        var btnPickTemp = new Button { Text = "Выбрать…", Dock = DockStyle.Fill };
        btnPickTemp.Click += (_, __) => PickFolder(_tbTempRootLocal);
        root.Controls.Add(btnPickTemp, 2, 1);

        // Station
        root.Controls.Add(MkLabel("Станция:"), 0, 2);
        _tbStation = new TextBox { Dock = DockStyle.Fill, Text = _cfg.StationName };
        root.Controls.Add(_tbStation, 1, 2);
        root.Controls.Add(new Label(), 2, 2);

        // CameraIndex
        root.Controls.Add(MkLabel("CameraIndex:"), 0, 3);
        _nudCamIndex = new NumericUpDown { Dock = DockStyle.Left, Width = 120, Minimum = 0, Maximum = 10, Value = _cfg.CameraIndex };
        root.Controls.Add(_nudCamIndex, 1, 3);
        root.Controls.Add(new Label(), 2, 3);

        // MaxClipSeconds
        root.Controls.Add(MkLabel("Авто-стоп (сек, 0=выкл):"), 0, 4);
        _nudMaxSeconds = new NumericUpDown { Dock = DockStyle.Left, Width = 120, Minimum = 0, Maximum = 24 * 3600, Value = _cfg.MaxClipSeconds };
        root.Controls.Add(_nudMaxSeconds, 1, 4);
        root.Controls.Add(new Label(), 2, 4);

        // Ozon header
        var header = MkHeader("Ozon API (ключи храним в настройках)");
        root.Controls.Add(header, 0, 5);
        root.SetColumnSpan(header, 3);

        // Ozon ClientId
        root.Controls.Add(MkLabel("Ozon Client-Id:"), 0, 6);
        _tbOzonClientId = new TextBox { Dock = DockStyle.Fill, Text = _cfg.OzonClientId ?? "" };
        root.Controls.Add(_tbOzonClientId, 1, 6);
        root.Controls.Add(new Label(), 2, 6);

        // Ozon ApiKey
        root.Controls.Add(MkLabel("Ozon API Key:"), 0, 7);
        _tbOzonApiKey = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, Text = _cfg.OzonApiKey ?? "" };
        root.Controls.Add(_tbOzonApiKey, 1, 7);
        root.Controls.Add(new Label(), 2, 7);

        // Buttons
        var pnlBtns = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Height = 48 };
        var btnOk = new Button { Text = "Сохранить", Width = 120, Height = 34 };
        var btnCancel = new Button { Text = "Отмена", Width = 120, Height = 34 };

        btnOk.Click += (_, __) =>
        {
            if (!ValidateAndStore()) return;
            _cfg.Save();
            DialogResult = DialogResult.OK;
            Close();
        };

        btnCancel.Click += (_, __) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        // --- Добавляем кнопку для теста подключения ---
        var testConnectionButton = new Button
        {
            Text = "Тест подключения",
            Dock = DockStyle.Fill,
            Height = 40
        };

        // Привязываем событие для кнопки
        testConnectionButton.Click += TestConnectionButton_Click;
        pnlBtns.Controls.Add(testConnectionButton); // Добавляем кнопку в панель


        pnlBtns.Controls.Add(btnOk);
        pnlBtns.Controls.Add(btnCancel);

        root.Controls.Add(pnlBtns, 0, 9);
        root.SetColumnSpan(pnlBtns, 3);

        Controls.Add(root);
    }

    private static Label MkLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static Label MkHeader(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        Font = new Font("Segoe UI", 11, FontStyle.Bold),
        Padding = new Padding(0, 16, 0, 6)
    };

    private static void PickFolder(TextBox target)
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = target.Text };
        if (dlg.ShowDialog() == DialogResult.OK)
            target.Text = dlg.SelectedPath;
    }

    private bool ValidateAndStore()
    {
        var recordRoot = (_tbRecordRoot.Text ?? "").Trim();
        var tempRoot = (_tbTempRootLocal.Text ?? "").Trim();

        if (string.IsNullOrWhiteSpace(recordRoot))
        {
            MessageBox.Show("Укажи папку для хранения видео.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(tempRoot))
        {
            MessageBox.Show("Укажи TEMP папку (только локально).");
            return false;
        }

        if (IsNetworkPath(tempRoot))
        {
            MessageBox.Show("TEMP папка не должна быть на сетевом диске/NAS. Выбери локальную папку (например C:\\Temp).");
            return false;
        }

        try { Directory.CreateDirectory(recordRoot); } catch { MessageBox.Show("Не удалось создать/открыть папку видео."); return false; }
        try { Directory.CreateDirectory(tempRoot); } catch { MessageBox.Show("Не удалось создать/открыть TEMP папку."); return false; }

        _cfg.RecordRoot = recordRoot;
        _cfg.TempRootLocal = tempRoot;

        _cfg.StationName = (_tbStation.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(_cfg.StationName)) _cfg.StationName = "PACK-01";

        _cfg.CameraIndex = (int)_nudCamIndex.Value;
        _cfg.MaxClipSeconds = (int)_nudMaxSeconds.Value;

        _cfg.OzonClientId = (_tbOzonClientId.Text ?? "").Trim();
        _cfg.OzonApiKey = (_tbOzonApiKey.Text ?? "").Trim();

        return true;
    }

    private static bool IsNetworkPath(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)) return true;
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root)) return false;
            var di = new DriveInfo(root);
            return di.DriveType == DriveType.Network;
        }
        catch
        {
            return true;
        }
    }

    private static AppConfig Clone(AppConfig src) => new()
    {
        RecordRoot = src.RecordRoot,
        TempRootLocal = src.TempRootLocal,
        StationName = src.StationName,
        CameraIndex = src.CameraIndex,
        MaxClipSeconds = src.MaxClipSeconds,
        OzonClientId = src.OzonClientId,
        OzonApiKey = src.OzonApiKey,
        OzonBaseUrl = src.OzonBaseUrl
    };
    private async void TestConnectionButton_Click(object? sender, EventArgs e)
    {
        var clientId = _tbOzonClientId.Text.Trim();
        var apiKey = _tbOzonApiKey.Text.Trim();

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(apiKey))
        {
            MessageBox.Show("Пожалуйста, введите Ozon Client-Id и API Key.");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            using var ozonClient = new OzonClient(_cfg.OzonBaseUrl ?? "https://api-seller.ozon.ru", clientId, apiKey);

            // НОВЫЙ ВЫЗОВ: с токеном и распаковкой кортежа
            var (ok, msg) = await ozonClient.TestConnectionAsync(cts.Token);

            if (ok)
            {
                MessageBox.Show(
                    "Подключение успешно!\n\n" + msg,
                    "Ozon",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(
                    "Ошибка подключения.\n\n" + msg,
                    "Ozon",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Ошибка при проверке подключения:\n" + ex.Message,
                "Ozon",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }



}

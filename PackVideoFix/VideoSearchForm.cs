using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PackVideoFix;

public sealed class VideoSearchForm : Form
{
    private readonly string _root;

    private readonly TextBox _tb;
    private readonly Button _btn;
    private readonly ListView _lv;

    public VideoSearchForm(string recordRoot)
    {
        _root = recordRoot;

        Text = "Поиск видео";
        Width = 1000;
        Height = 650;

        _tb = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Введите штрихкод или его часть..." };
        _btn = new Button { Dock = DockStyle.Top, Height = 34, Text = "Найти" };
        _btn.Click += (_, __) => Search();

        _lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
        _lv.Columns.Add("Время", 170);
        _lv.Columns.Add("ШК", 240);
        _lv.Columns.Add("Статус", 110);
        _lv.Columns.Add("Файл", 430);

        _lv.DoubleClick += (_, __) => OpenSelected();

        Controls.Add(_lv);
        Controls.Add(_btn);
        Controls.Add(_tb);

        Shown += (_, __) => SearchRecent();
    }

    private void SearchRecent()
    {
        var now = DateTime.Now.Date;
        var days = new[] { now, now.AddDays(-1) };

        var list = new List<MainForm.ClipMeta>();

        foreach (var d in days)
        {
            var folder = Path.Combine(_root, d.ToString("yyyy-MM-dd"));
            if (!Directory.Exists(folder)) continue;

            foreach (var f in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories))
            {
                var meta = MainForm.TryReadMeta(f);
                if (meta != null) list.Add(meta);
            }
        }

        Fill(list.OrderByDescending(x => x.StartedAt).Take(200).ToList());
    }

    private void Search()
    {
        var q = (_tb.Text ?? "").Trim();
        if (q.Length == 0) { SearchRecent(); return; }

        var result = new List<MainForm.ClipMeta>();
        if (!Directory.Exists(_root))
        {
            MessageBox.Show("Папка не найдена: " + _root);
            return;
        }

        foreach (var f in Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories))
        {
            var meta = MainForm.TryReadMeta(f);
            if (meta == null) continue;

            if (!string.IsNullOrWhiteSpace(meta.Barcode) &&
                meta.Barcode.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(meta);
                if (result.Count >= 500) break;
            }
        }

        Fill(result.OrderByDescending(x => x.StartedAt).ToList());
    }

    private void Fill(List<MainForm.ClipMeta> items)
    {
        _lv.BeginUpdate();
        _lv.Items.Clear();

        foreach (var c in items)
        {
            var it = new ListViewItem(c.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            it.SubItems.Add(c.Barcode ?? "");
            it.SubItems.Add(c.Status ?? "");
            it.SubItems.Add(c.VideoPath ?? "");
            it.Tag = c;
            _lv.Items.Add(it);
        }

        _lv.EndUpdate();
    }

    private void OpenSelected()
    {
        if (_lv.SelectedItems.Count == 0) return;
        if (_lv.SelectedItems[0].Tag is not MainForm.ClipMeta c) return;

        if (string.IsNullOrWhiteSpace(c.VideoPath) || !File.Exists(c.VideoPath))
        {
            MessageBox.Show("Файл не найден: " + c.VideoPath);
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = c.VideoPath,
            UseShellExecute = true
        });
    }
}

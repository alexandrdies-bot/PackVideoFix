using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        Width = 1100;
        Height = 650;
        StartPosition = FormStartPosition.CenterParent;

        var top = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8) };
        _tb = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Штрихкод (часть)..." };
        _btn = new Button { Dock = DockStyle.Right, Width = 140, Text = "Найти" };
        _btn.Click += (_, __) => Search();

        top.Controls.Add(_tb);
        top.Controls.Add(_btn);

        _lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
        _lv.Columns.Add("Время", 170);
        _lv.Columns.Add("ШК", 240);
        _lv.Columns.Add("Статус", 110);
        _lv.Columns.Add("Файл", 520);
        _lv.DoubleClick += (_, __) => OpenSelected();

        Controls.Add(_lv);
        Controls.Add(top);
    }

    private void Search()
    {
        _lv.BeginUpdate();
        _lv.Items.Clear();

        if (!Directory.Exists(_root))
        {
            _lv.EndUpdate();
            MessageBox.Show("Папка не найдена: " + _root);
            return;
        }

        var q = (_tb.Text ?? "").Trim();
        var list = new List<ClipMeta>(); // ВАЖНО: ClipMeta (НЕ MainForm.ClipMeta)

        foreach (var f in Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories))
        {
            var meta = MainForm.TryReadMeta(f); // TryReadMeta возвращает ClipMeta
            if (meta == null) continue;

            if (string.IsNullOrWhiteSpace(q) ||
                (!string.IsNullOrWhiteSpace(meta.Barcode) &&
                 meta.Barcode.Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(meta);
            }
        }

        foreach (var c in list.OrderByDescending(x => x.StartedAt).Take(300))
        {
            var it = new ListViewItem(c.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            it.SubItems.Add(c.Barcode ?? "");
            it.SubItems.Add(c.Status ?? "");
            it.SubItems.Add(c.VideoPath ?? "");
            it.Tag = c; // Tag хранит ClipMeta
            _lv.Items.Add(it);
        }

        _lv.EndUpdate();
    }

    private void OpenSelected()
    {
        if (_lv.SelectedItems.Count == 0) return;

        if (_lv.SelectedItems[0].Tag is not ClipMeta c) return; // ВАЖНО: ClipMeta

        if (string.IsNullOrWhiteSpace(c.VideoPath) || !File.Exists(c.VideoPath))
        {
            MessageBox.Show("Файл не найден: " + c.VideoPath);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = c.VideoPath,
            UseShellExecute = true
        });
    }
}

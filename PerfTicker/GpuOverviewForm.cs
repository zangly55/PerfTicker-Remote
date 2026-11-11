namespace PerfTicker;

internal sealed class GpuOverviewForm : Form
{
    private readonly AppForm _app;
    private readonly DataGridView _grid = new();
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    public GpuOverviewForm(AppForm app)
    {
        SuspendLayout();
        _app = app;
        Text = "显卡总览 · 双击查看 K 线";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1100, 460); MinimumSize = new Size(850, 320);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Panel; ForeColor = Theme.TextHi;
        Font = new Font("Microsoft YaHei UI", 9);
        _status.Dock = DockStyle.Top; _status.Height = 46; _status.Padding = new Padding(12, 12, 12, 0);
        _grid.Dock = DockStyle.Fill; _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false; _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.BackgroundColor = Theme.Panel; _grid.BorderStyle = BorderStyle.None;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.HeadTop;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.TextHi;
        _grid.DefaultCellStyle.BackColor = Theme.Panel;
        _grid.DefaultCellStyle.ForeColor = Theme.TextHi;
        _grid.DefaultCellStyle.SelectionBackColor = Theme.RowSel;
        _grid.DefaultCellStyle.SelectionForeColor = Theme.Gold;
        _grid.GridColor = Theme.Line2; _grid.RowTemplate.Height = 34;
        foreach (var (key, label, width) in new[] {
            ("index", "显卡", 65), ("name", "型号", 230), ("load", "负载 %", 85),
            ("memory", "已用 / 总显存 GB", 155), ("temp", "温度 °C", 85),
            ("power", "功耗 W", 85), ("clock", "核心 MHz", 95), ("id", "设备 UUID", 240) })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = key, HeaderText = label, Width = width,
                SortMode = DataGridViewColumnSortMode.NotSortable });
        var open = new Button { Text = "查看所选显卡 K 线", Dock = DockStyle.Bottom, Height = 40,
            BackColor = Theme.ChipBg, ForeColor = Theme.Gold, FlatStyle = FlatStyle.Flat };
        open.Click += (_, _) => OpenSelected();
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSelected(); };
        Controls.Add(_grid); Controls.Add(_status); Controls.Add(open);
        _timer.Tick += (_, _) => RefreshRows();
        RefreshRows(); _timer.Start();
        ResumeLayout(false);
        PerformLayout();
    }

    private void RefreshRows()
    {
        string selected = _grid.CurrentRow?.Tag as string;
        var snap = _app.St.Snap;
        string state = _app.St.Paused ? "已暂停" : _app.RemoteStatus;
        _status.Text = $"{_app.SourceLabel} · {snap.HostName} · {state} · {snap.Gpus.Count} 张 GPU" +
            (snap.Gpus.Count == 0 ? " · 未检测到可枚举显卡；CPU、内存监控仍可使用" : " · — 表示传感器不可用");
        _grid.Rows.Clear();
        string F(double value, string format = "0.0") => double.IsFinite(value) ? value.ToString(format) : "—";
        foreach (var gpu in snap.Gpus)
        {
            int index = _grid.Rows.Add("GPU " + gpu.Index, gpu.Name, F(gpu.UtilPct),
                F(gpu.MemUsedMB / 1024) + " / " + F(gpu.MemTotalMB / 1024), F(gpu.TempC, "0"),
                F(gpu.PowerW), F(gpu.ClockMHz, "0"), gpu.Id);
            _grid.Rows[index].Tag = gpu.Id;
            if (gpu.TempC >= 82) _grid.Rows[index].Cells["temp"].Style.ForeColor = Theme.Warn;
            if (gpu.Id == selected) _grid.CurrentCell = _grid.Rows[index].Cells[0];
        }
    }
    private void OpenSelected()
    {
        if (_grid.CurrentRow?.Tag is not string id) return;
        int index = _app.St.Metrics.FindIndex(m => m.GpuId == id && m.Name.EndsWith("负载"));
        if (index < 0) return;
        _app.St.SelectedIndex = index; _app.OnMetricChanged(); Close();
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop(); _timer.Dispose(); base.OnFormClosed(e);
    }
}

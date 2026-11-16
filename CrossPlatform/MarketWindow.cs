using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Collections.ObjectModel;

namespace PerfTicker;

internal sealed partial class MarketWindow : Window
{
    private IMonitorSampler _sampler;
    private RemoteConfig _config = RemoteConfig.Load();
    private readonly DesktopSettings _settings = DesktopSettings.Load();
    private bool _remote, _paused, _configuring;
    private long _lastTick, _lastUiSecond = -1;
    private string _topology = "";
    private Snapshot _snapshot = new();
    private List<Metric> _metrics = new();
    private readonly ObservableCollection<QuoteRow> _quotes = new();
    private readonly ObservableCollection<ProcessRow> _processRows = new();
    private readonly Dictionary<int, ProcessRow> _knownProcesses = new();
    private readonly HashSet<string> _pinned = new(StringComparer.OrdinalIgnoreCase);
    private readonly ListBox _watch = new() { Background = Brushes.Transparent };
    private readonly ListBox _processes = new() { Background = Brushes.Transparent };
    private readonly StackPanel _cards = new() { Spacing = 8 }, _leaders = new() { Spacing = 4 }, _tape = new() { Spacing = 7 };
    private readonly TextBlock _status = MarketStyle.Label("正在启动…", 11), _warning = MarketStyle.Label("", 11, MarketStyle.Gold),
        _quote = MarketStyle.Label("等待行情", 23, MarketStyle.Gold), _host = MarketStyle.Label("本机", 11, MarketStyle.Muted),
        _cpu = MarketStyle.Label("—", 21), _memory = MarketStyle.Label("—", 21), _summary = MarketStyle.Label("", 11, MarketStyle.Muted),
        _price = MarketStyle.Label("—", 32), _processCount = MarketStyle.Label("", 12, MarketStyle.Muted), _clock = MarketStyle.Label("", 14, MarketStyle.Gold);
    private readonly CandleChart _chart = new();
    private readonly ComboBox _source = new() { ItemsSource = new[] { "本机行情", "SSH 服务器" }, SelectedIndex = 0, MinWidth = 120 };
    private readonly ComboBox _sort = new() { ItemsSource = new[] { "CPU", "GPU 显存", "内存", "线程" }, MinWidth = 105 };
    private readonly ComboBox _group = new() { ItemsSource = new[] { "全部板块", "指数", "图形", "处理器", "内存", "存储网络", "系统" }, SelectedIndex = 0, MinWidth = 110 };
    private readonly ComboBox _interval = new() { ItemsSource = new[] { "0.5 秒", "1 秒", "2 秒", "5 秒", "10 秒" }, MinWidth = 80 };
    private readonly TextBox _search = new() { PlaceholderText = "搜索进程 / PID  ⌘F", Width = 230 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Button _pauseButton = new() { Content = "暂停", MinWidth = 64 }, _colorButton = new() { Content = "红涨" }, _topButton = new() { Content = "置顶" };
    private readonly Dictionary<string, (TextBlock value, TextBlock change, Sparkline spark)> _indices = new();
    private readonly Dictionary<long, Button> _periodButtons = new();
    private readonly Economy _economy = new();
    private readonly Hardware _hardware = new();
    private readonly TextBlock _ecoText = MarketStyle.Label("", 15, MarketStyle.Gold);
    private readonly TextBlock _ecoParts = MarketStyle.Label("", 11, MarketStyle.Muted), _marquee = MarketStyle.Label("", 11, MarketStyle.Gold);
    private readonly Border _ecoPanel;
    private long _lastEconomyAt = Environment.TickCount64, _lastSettingsSave;

    public MarketWindow()
    {
        Title = "性能行情终端 · PerfTicker";
        Width = _settings.Width; Height = _settings.Height; MinWidth = 1000; MinHeight = 700;
        Opened += (_, _) => {
            if (_settings.X is int x && _settings.Y is int y && Screens.All.Any(s => s.WorkingArea.Contains(new PixelPoint(x + 100, y + 50)))) Position = new PixelPoint(x, y);
            if (_settings.Maximized) WindowState = WindowState.Maximized;
            if (_settings.Gadget) ToggleGadget();
        };
        FontFamily = new FontFamily(MarketStyle.Fonts); Background = MarketStyle.Background; Foreground = MarketStyle.Text;
        Topmost = _settings.Topmost; _topButton.Content = Topmost ? "已置顶" : "置顶"; foreach (var p in _settings.Pinned) _pinned.Add(p);
        _economy.Enabled = _settings.EconomyOn; _economy.Restore(_settings.EcoTotal, _settings.EcoPeak, _settings.EcoSeconds);
        _chart.PeriodMs = _settings.PeriodMs;
        ApplyTheme();
        ActualThemeVariantChanged += (_, _) => { if (_settings.ThemeMode == 2) ApplyPalette(); };
        _pauseButton.Click += (_, _) => TogglePause();
        _colorButton.Click += (_, _) => ToggleColor(); _topButton.Click += (_, _) => ToggleTopmost();
        _source.SelectionChanged += async (_, _) => { if (_configuring || (_source.SelectedIndex == 1) == _remote) return; if (_source.SelectedIndex == 0) Switch(false); else await Configure(); };
        _interval.SelectedIndex = Array.IndexOf(DesktopSettings.Intervals, _settings.IntervalMs);
        _interval.SelectionChanged += (_, _) => { if (_interval.SelectedIndex >= 0) SetInterval(DesktopSettings.Intervals[_interval.SelectedIndex]); };
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in new Control[] { MarketStyle.Label("性能行情终端", 18, MarketStyle.Gold), _source,
            MarketStyle.Button("连接设置", async () => await Configure()), MarketStyle.Label("采样", 11, MarketStyle.Muted), _interval,
            MarketStyle.Button("营收", ToggleEconomy), _pauseButton, _topButton, _colorButton,
            MarketStyle.Button("显卡", ShowGpus), MarketStyle.Button("设置", ShowSettings), _clock })
        { control.Margin = new Thickness(0, 0, 8, 6); toolbar.Children.Add(control); }
        var index = new UniformGrid { Columns = 8 };
        foreach (var item in new[] { ("000001", "CPU 综合指数"), ("000002", "CPU 负载"), ("600519", "GPU 核心频率"),
            ("600521", "GPU 负载"), ("000003", "内存占用"), ("600523", "GPU 温度"), ("600036", "网络下行"), ("000301", "进程数") })
        {
            var value = item.Item1 == "000002" ? _cpu : item.Item1 == "000003" ? _memory : MarketStyle.Label("—", 21);
            var change = MarketStyle.Label("—", 10); var spark = new Sparkline { Height = 13 };
            _indices[item.Item1] = (value, change, spark);
            var content = new StackPanel { Spacing = 3, Children = { MarketStyle.Label(item.Item2, 11, MarketStyle.Muted), value, MarketStyle.Row("*,45", change, spark) } };
            var box = MarketStyle.Box(content, new Thickness(10, 8)); box.PointerPressed += (_, _) => SelectMetric(item.Item1); index.Children.Add(box);
        }
        _watch.ItemsSource = _quotes;
        _watch.ItemTemplate = new FuncDataTemplate<QuoteRow>((_, _) =>
        {
            var spark = new Sparkline { Height = 16 }; spark.Bind(Sparkline.ValuesProperty, new Binding("Spark"));
            var price = new StackPanel { Spacing = 2, Children = { Bound("Price", 11, color: true, right: true), Bound("Change", 9, color: true, right: true) } };
            return MarketStyle.Row("48,*,38,75", Bound("Code", 10, true), Bound("Name", 11), spark, price);
        });
        _watch.SelectionChanged += (_, _) => { _chart.Metric = (_watch.SelectedItem as QuoteRow)?.Metric; UpdateQuote(); RenderProcesses(); };
        _group.SelectionChanged += (_, _) => RebuildQuotes();
        var left = DockHeader(MarketStyle.Row("*,Auto", MarketStyle.Label("自选行情", 16, MarketStyle.Gold), _group), _watch);
        var periods = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (long p in DesktopSettings.Periods)
        {
            var button = MarketStyle.Button(p == 60000 ? "1分K" : $"{p / 1000}秒K", () => SetPeriod(p));
            button.FontSize = 11; button.FontWeight = FontWeight.Normal;
            button.Height = button.MinHeight = 22; button.MinWidth = 38;
            button.Padding = new Thickness(6, 1); button.CornerRadius = new CornerRadius(2);
            button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0, 0, 0, 1);
            ToolTip.SetTip(button, $"{p / 1000} 秒 K 线 · 快捷键 {Array.IndexOf(DesktopSettings.Periods, p) + 1}");
            _periodButtons[p] = button; periods.Children.Add(button);
        }
        var quoteHead = new StackPanel { Spacing = 5, Margin = new Thickness(0, 0, 0, 6), Children = { _quote, _price, _summary, periods } };
        _ecoText.TextWrapping = _ecoParts.TextWrapping = TextWrapping.Wrap;
        _ecoPanel = MarketStyle.Box(new StackPanel { Spacing = 5, Children = { MarketStyle.Row("*,Auto", _ecoText, MarketStyle.Button("归零", ResetEconomy)), _ecoParts } }, new Thickness(10, 8));
        _ecoPanel.IsVisible = _economy.Enabled;
        var center = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        center.Children.Add(quoteHead); Grid.SetRow(_chart, 1); center.Children.Add(_chart); Grid.SetRow(_ecoPanel, 2); center.Children.Add(_ecoPanel);
        var right = new Grid { RowDefinitions = new RowDefinitions("*,*") };
        right.Children.Add(DockHeader(MarketStyle.Label("主力动向 · CPU 持仓", 14, MarketStyle.Gold), new ScrollViewer { Content = _leaders }));
        var tape = DockHeader(MarketStyle.Label("逐笔动态 · LIVE TAPE", 14, MarketStyle.Gold), new ScrollViewer { Content = _tape }); Grid.SetRow(tape, 1); right.Children.Add(tape);
        var body = MarketStyle.Row("270,*,225", MarketStyle.Box(left, new Thickness(4, 10)), MarketStyle.Box(center), MarketStyle.Box(right));
        _processes.ItemsSource = _processRows;
        _processes.ItemTemplate = new FuncDataTemplate<ProcessRow>((row, _) =>
        {
            var spark = new Sparkline { Height = 18, Margin = new Thickness(8, 0) }; spark.Bind(Sparkline.ValuesProperty, new Binding("Spark"));
            var line = MarketStyle.Row("65,*,68,68,85,85,65,65,150", Bound("Pid", 11, true), Bound("Name", 12), Bound("Cpu", 12, right: true),
                Bound("Delta", 11, color: true, right: true), Bound("Gpu", 12, right: true), Bound("Memory", 12, right: true), Bound("Threads", 11, right: true), Bound("Handles", 11, right: true), spark);
            var pin = new MenuItem { Header = "置顶 / 取消置顶进程" }; pin.Click += (_, _) => { if (line.DataContext is ProcessRow p) { if (!_pinned.Add(p.Sample.Name)) _pinned.Remove(p.Sample.Name); RenderProcesses(); } };
            var copy = new MenuItem { Header = "复制进程名" }; copy.Click += async (_, _) => { if (line.DataContext is ProcessRow p && Clipboard != null) await Clipboard.SetValueAsync(DataFormat.Text, p.Sample.Name); };
            line.ContextMenu = new ContextMenu { ItemsSource = new[] { pin, copy } }; return line;
        });
        _search.TextChanged += (_, _) => RenderProcesses();
        _sort.SelectedIndex = _settings.ProcSort; _sort.SelectionChanged += (_, _) => RenderProcesses();
        var procHead = new StackPanel { Spacing = 5, Children = {
            MarketStyle.Row("*,240,110,65", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { MarketStyle.Label("个股列表 · 进程", 15, MarketStyle.Gold), _processCount } }, _search, _sort,
                MarketStyle.Button("升/降", () => { _settings.SortDescending = !_settings.SortDescending; RenderProcesses(); })),
            MarketStyle.Row("65,*,68,68,85,85,65,65,150", MarketStyle.Label("PID", 10, MarketStyle.Muted), MarketStyle.Label("名称 / 进程", 10, MarketStyle.Muted),
                MarketStyle.Label("CPU %", 10, MarketStyle.Muted), MarketStyle.Label("涨跌", 10, MarketStyle.Muted), MarketStyle.Label("显存 MB", 10, MarketStyle.Muted),
                MarketStyle.Label("内存 MB", 10, MarketStyle.Muted), MarketStyle.Label("线程", 10, MarketStyle.Muted), MarketStyle.Label("句柄", 10, MarketStyle.Muted), MarketStyle.Label("K 线走势", 10, MarketStyle.Muted)) } };
        var procPanel = MarketStyle.Box(DockHeader(procHead, _processes), new Thickness(8));
        _warning.TextWrapping = TextWrapping.Wrap; _warning.TextTrimming = TextTrimming.None;
        _marquee.IsVisible = _settings.Marquee;
        var footer = new StackPanel { Spacing = 2, Children = { _marquee, MarketStyle.Row("*,Auto", _status, MarketStyle.Button("关于", ShowAbout)), _host, _warning } };
        var root = new Grid { Margin = new Thickness(8), RowDefinitions = new RowDefinitions("Auto,Auto,*,5,210,Auto") };
        root.Children.Add(toolbar); Grid.SetRow(index, 1); root.Children.Add(index); Grid.SetRow(body, 2); root.Children.Add(body);
        var split = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch, Background = MarketStyle.Line };
        Grid.SetRow(split, 3); root.Children.Add(split); Grid.SetRow(procPanel, 4); root.Children.Add(procPanel); Grid.SetRow(footer, 5); root.Children.Add(footer); Content = root;
        KeyDown += HandleKey;
        _timer.Tick += (_, _) => Update();
        Closed += (_, _) => { _timer.Stop(); SaveSettings(); _sampler?.Dispose(); _gadget?.Close(); _tray?.Dispose(); };
        Switch(false); UpdatePeriodButtons(); _timer.Start(); SetupTray();
    }
    private static TextBlock Bound(string path, double size = 12, bool muted = false, bool color = false, bool right = false)
    {
        var t = MarketStyle.Label("", size, muted ? MarketStyle.Muted : MarketStyle.Text); t.Bind(TextBlock.TextProperty, new Binding(path));
        if (color) t.Bind(TextBlock.ForegroundProperty, new Binding("Color"));
        if (right) t.HorizontalAlignment = HorizontalAlignment.Right;
        return t;
    }
    private static DockPanel DockHeader(Control header, Control content)
    {
        header.Margin = new Thickness(4, 0, 4, 8); var panel = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header); panel.Children.Add(content); return panel;
    }
    private async Task Configure()
    {
        if (_configuring) return;
        _configuring = true;
        try { var config = await new ConnectionWindow(_config).ShowDialog<RemoteConfig>(this); if (config != null) { _config = config; Switch(true); } else _source.SelectedIndex = _remote ? 1 : 0; }
        finally { _configuring = false; }
    }
    private void Switch(bool remote)
    {
        _sampler?.Dispose(); _remote = remote; _lastTick = 0; _topology = ""; _snapshot = new();
        _metrics.Clear(); _quotes.Clear(); _cards.Children.Clear(); _processRows.Clear(); _knownProcesses.Clear(); _leaders.Children.Clear(); _tape.Children.Clear();
        _chart.Metric = null; _chart.InvalidateVisual(); _quote.Text = "等待行情"; _price.Text = "—"; _summary.Text = "";
        foreach (var item in _indices.Values) { item.value.Text = "—"; item.change.Text = "—"; item.spark.Values = Array.Empty<double>(); }
        _sampler = remote ? new RemoteSampler(_settings.IntervalMs, () => _config.Copy()) : new LocalSampler { IntervalMs = _settings.IntervalMs };
        _source.SelectedIndex = remote ? 1 : 0; _paused = false; _pauseButton.Content = "暂停"; _lastEconomyAt = Environment.TickCount64;
        AddTape(remote ? "正在连接 SSH 服务器" : "正在读取本机行情"); Update();
    }
    private void RebuildQuotes()
    {
        string selected = (_watch.SelectedItem as QuoteRow)?.Code ?? _settings.SelectedCode;
        string group = _group.SelectedIndex <= 0 ? "" : _group.SelectedItem?.ToString(); _quotes.Clear();
        foreach (var m in _metrics.Where(m => group == "" || m.Group == group)) _quotes.Add(new QuoteRow(m));
        _watch.SelectedItem = _quotes.FirstOrDefault(r => r.Code == selected) ?? _quotes.FirstOrDefault();
    }
    internal void SelectMetric(string code) { _group.SelectedIndex = 0; _watch.SelectedItem = _quotes.FirstOrDefault(r => r.Code == code); }
    private void UpdateQuote()
    {
        var m = _chart.Metric;
        if (m != null)
        {
            _settings.SelectedCode = m.Code; _quote.Text = $"{m.Name}  {m.Code}"; _price.Text = $"{m.Display} {m.Unit}    {m.ChgText}";
            _price.Foreground = !m.Available ? MarketStyle.Muted : m.Chg >= 0 ? MarketStyle.Up : MarketStyle.Down;
            _summary.Text = m.Count == 0 ? "暂无可用样本" : $"今开  {m.Fmt(m.Open)}    最高  {m.Fmt(m.High)}    最低  {m.Fmt(m.Low)}    均价  {m.Fmt(m.Sum / m.Count)}";
        }
        _chart.InvalidateVisual();
    }
    private void Update()
    {
        long now = Environment.TickCount64;
        double dt = (now - _lastEconomyAt) / 1000.0; _lastEconomyAt = now;
        if (!_paused && _sampler.IsLive) _economy.Accrue(dt, _snapshot);
        if (now / 1000 != _lastUiSecond)
        {
            _lastUiSecond = now / 1000; _clock.Text = DateTime.Now.ToString("HH:mm:ss");
            _status.Text = $"● {(_remote ? "SSH 服务器" : "本机")} / {_sampler.Status}" + (_paused ? " · 显示已暂停" : "") + $" · {_settings.IntervalMs / 1000.0:0.#}秒采样";
            _status.Foreground = _sampler.IsLive ? MarketStyle.Down : MarketStyle.Gold; _warning.Text = _sampler.Error;
            bool stale = !_sampler.IsLive; if (_chart.Stale != stale) { _chart.Stale = stale; _chart.InvalidateVisual(); }
            if (_economy.Enabled)
            {
                bool available = Economy.CanAccrue(_snapshot);
                _ecoText.Text = $"营收中心（模拟）  ¥{_economy.Total:N2}   " + (available ? $"{_economy.Rate:+0.00;-0.00;0.00} 元/秒" : "缺少传感器数据，结算暂停");
                _ecoParts.Text = available ? string.Join("  ·  ", _economy.Parts.Select(p => $"{p.Name} {p.Net:+0.00;-0.00;0.00}")) + $"\n累计 {_economy.Seconds:0} 秒 · 峰值 {_economy.PeakRate:0.00} 元/秒 · 温度系数 {_economy.TempPenalty:0.00}" : "需 CPU、GPU、内存、磁盘及网络读数完整；不将未知数据按零结算。";
            }
            if (_settings.Marquee && _metrics.Count > 0)
            {
                int start = (int)((now / 5000) % _metrics.Count);
                _marquee.Text = "行情播报  ·  " + string.Join("     |     ", Enumerable.Range(0, Math.Min(5, _metrics.Count)).Select(i => _metrics[(start + i) % _metrics.Count]).Select(m => $"{m.Name}  {m.Display} {m.Unit}  {m.ChgText}"));
            }
            if (now - _lastSettingsSave > 15000) { _lastSettingsSave = now; SaveSettings(); }
        }
        var s = _sampler.Latest;
        if (_paused || !_sampler.IsLive || s.Tick == _lastTick) return;
        _lastTick = s.Tick;
        string topology = s.CoreMHz.Length + ":" + string.Join("|", s.Gpus.OrderBy(g => g.Index).Select(g => g.Index + ":" + g.Id));
        bool changed = topology != _topology;
        if (changed)
        {
            _topology = topology; _metrics = Catalog.Reconcile(_metrics, s);
            var handles = _metrics.FirstOrDefault(m => m.Code == "000303"); if (handles != null) handles.Read = v => v.Handles < 0 ? double.NaN : v.Handles;
            _hardware.Apply(s); _economy.ApplyWeights(_hardware);
        }
        foreach (var m in _metrics) m.Push(m.Read(s), (long)(s.WallSeconds * 1000), double.IsFinite(s.CtxSwitchPerSec) ? s.CtxSwitchPerSec : double.NaN);
        if (changed) RebuildQuotes(); foreach (var row in _quotes) row.Refresh();
        foreach (var pair in _indices)
        {
            var m = _metrics.FirstOrDefault(m => m.Code == pair.Key); if (m == null) continue;
            var v = pair.Value; v.value.Text = m.Display + " " + m.Unit; v.value.FontSize = Bounds.Width < 1150 ? 18 : 21;
            ToolTip.SetTip(v.value, v.value.Text); v.change.Text = m.ChgText; v.spark.Values = m.Spark(24);
            v.value.Foreground = v.change.Foreground = !m.Available ? MarketStyle.Muted : m.Chg >= 0 ? MarketStyle.Up : MarketStyle.Down;
        }
        foreach (var p in s.Procs)
        {
            if (!_knownProcesses.TryGetValue(p.Pid, out var row) || row.Sample.Name != p.Name)
            {
                _knownProcesses[p.Pid] = row = new(); if (_snapshot.Tick > 0) AddTape($"新进 {p.Name} · {p.Pid}");
            }
            row.Push(p);
        }
        var livePids = s.Procs.Select(p => p.Pid).ToHashSet();
        foreach (int pid in _knownProcesses.Keys.Where(pid => !livePids.Contains(pid)).ToArray()) { AddTape($"退出 {_knownProcesses[pid].Sample.Name} · {pid}"); _knownProcesses.Remove(pid); }
        foreach (var p in s.Procs.Where(p => Math.Abs(p.CpuDelta) >= 1.2).OrderByDescending(p => Math.Abs(p.CpuDelta)).Take(2)) AddTape($"{p.Name}  {p.CpuPct:0.0}%  {p.CpuDelta:+0.0;-0.0}");
        if (s.CpuPct >= 88 && _snapshot.CpuPct < 88) AddTape($"CPU 负载预警 {s.CpuPct:0}%");
        if (s.MemPct >= 90 && _snapshot.MemPct < 90) AddTape($"内存水位预警 {s.MemPct:0}%");
        _snapshot = s; _host.Text = $"{s.HostName} · {s.OsName} · {s.CpuName} · {s.CoreMHz.Length} 逻辑核心 · {s.Gpus.Count} 张 GPU";
        UpdateQuote(); RenderProcesses(); RenderLeaders(); RenderCards(); UpdateGadget();
    }
    private void RenderProcesses()
    {
        foreach (var row in _knownProcesses.Values) { row.Pinned = _pinned.Contains(row.Sample.Name); row.GpuId = _chart.Metric?.GpuId ?? ""; row.Refresh(); }
        var desired = DesktopQueries.Processes(_knownProcesses.Values, _search.Text, Math.Max(0, _sort.SelectedIndex), _settings.SortDescending, _chart.Metric?.GpuId ?? "").ToList();
        // Keep row instances and the virtualized ListBox; do not recreate all controls per sample.
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < _processRows.Count && ReferenceEquals(_processRows[i], desired[i])) continue;
            int old = _processRows.IndexOf(desired[i]); if (old >= 0) _processRows.Move(old, i); else _processRows.Insert(i, desired[i]);
        }
        while (_processRows.Count > desired.Count) _processRows.RemoveAt(_processRows.Count - 1);
        _processCount.Text = $"{desired.Count} / {_knownProcesses.Count} 只 · CPU 为单核口径";
    }
    private void RenderLeaders()
    {
        _leaders.Children.Clear(); int rank = 0;
        foreach (var p in _snapshot.Procs.OrderByDescending(p => p.CpuPct).Take(10))
            _leaders.Children.Add(MarketStyle.Row("24,*,55", MarketStyle.Label((++rank).ToString(), 11, MarketStyle.Gold), MarketStyle.Label(p.Name, 11), MarketStyle.Label($"{p.CpuPct:0.0}%", 11, MarketStyle.Up)));
    }
    private void AddTape(string text)
    {
        _tape.Children.Insert(0, MarketStyle.Label(DateTime.Now.ToString("HH:mm:ss") + "  " + text, 11, MarketStyle.Muted));
        while (_tape.Children.Count > 60) _tape.Children.RemoveAt(_tape.Children.Count - 1);
    }
    private void RenderCards()
    {
        if (_gpuWindow?.IsVisible != true) return;
        _cards.Children.Clear();
        foreach (var g in _snapshot.Gpus)
        {
            var b = MarketStyle.Button($"GPU {g.Index} · {g.Name}\n负载 {MarketStyle.N(g.UtilPct)}% · 显存 {MarketStyle.N(g.MemUsedMB)} / {MarketStyle.N(g.MemTotalMB)} MB\n温度 {MarketStyle.N(g.TempC)} °C · 功耗 {MarketStyle.N(g.PowerW)} W · 频率 {MarketStyle.N(g.ClockMHz)} MHz", () => { var m = _metrics.FirstOrDefault(m => m.GpuId == g.Id && m.Name.EndsWith("负载")); if (m != null) SelectMetric(m.Code); });
            b.HorizontalAlignment = HorizontalAlignment.Stretch; _cards.Children.Add(b);
        }
        if (_cards.Children.Count == 0) _cards.Children.Add(MarketStyle.Label("未取得显卡数据"));
    }
    internal void SetInterval(int ms)
    {
        _settings.IntervalMs = ms; _settings.Normalize();
        int selection = Array.IndexOf(DesktopSettings.Intervals, _settings.IntervalMs);
        if (_interval.SelectedIndex != selection) _interval.SelectedIndex = selection;
        _sampler?.IntervalMs = _settings.IntervalMs;
        _chart.PeriodMs = _settings.PeriodMs; UpdatePeriodButtons(); _chart.InvalidateVisual();
    }
    internal void SetPeriod(long period) { if (period < 2L * _settings.IntervalMs) return; _settings.PeriodMs = period; _chart.PeriodMs = period; UpdatePeriodButtons(); _chart.InvalidateVisual(); }
    private void UpdatePeriodButtons()
    {
        foreach (var (period, b) in _periodButtons)
        {
            b.IsEnabled = period >= 2L * _settings.IntervalMs;
            b.Foreground = period == _chart.PeriodMs ? MarketStyle.Gold : MarketStyle.Muted;
            b.BorderBrush = period == _chart.PeriodMs ? MarketStyle.Gold : Brushes.Transparent;
        }
    }
    internal void TogglePause() { _paused = !_paused; _pauseButton.Content = _paused ? "继续" : "暂停"; AddTape(_paused ? "行情已暂停" : "行情恢复推送"); _lastUiSecond = -1; Update(); }
    internal void ToggleTopmost() { Topmost = !Topmost; _topButton.Content = Topmost ? "已置顶" : "置顶"; }
    internal void ToggleColor() { _settings.RedUp = !_settings.RedUp; ApplyPalette(); UpdateQuote(); foreach (var q in _quotes) q.Refresh(); }
    internal void ToggleEconomy() { _economy.Enabled = !_economy.Enabled; _ecoPanel.IsVisible = _economy.Enabled; _lastUiSecond = -1; }
    private void ResetEconomy() { _economy.Reset(); _lastUiSecond = -1; }
    internal void SetTheme(int theme) { _settings.ThemeMode = Math.Clamp(theme, 0, 2); ApplyTheme(); }
    private void ApplyTheme()
    {
        RequestedThemeVariant = _settings.ThemeMode == 2 ? ThemeVariant.Default : _settings.ThemeMode == 1 ? ThemeVariant.Light : ThemeVariant.Dark;
        foreach (var window in _auxiliaries) window.RequestedThemeVariant = RequestedThemeVariant;
        ApplyPalette();
    }
    private void ApplyPalette() { MarketStyle.Apply(_settings.ThemeMode == 0 || (_settings.ThemeMode == 2 && ActualThemeVariant != ThemeVariant.Light), _settings.RedUp); _colorButton.Content = _settings.RedUp ? "红涨" : "绿涨"; _chart.InvalidateVisual(); }
    private void HandleKey(object sender, KeyEventArgs e)
    {
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && e.Key == Key.F) { _search.Focus(); e.Handled = true; return; }
        if (e.Source is TextBox || (e.Source is Visual visual && visual.GetVisualAncestors().OfType<TextBox>().Any()))
        { if (e.Key == Key.Escape) { _search.Text = ""; Focus(); e.Handled = true; } return; }
        switch (e.Key)
        {
            case Key.Space: TogglePause(); break;
            case Key.D1: SetPeriod(2000); break; case Key.D2: SetPeriod(5000); break; case Key.D3: SetPeriod(10000); break; case Key.D4: SetPeriod(30000); break; case Key.D5: SetPeriod(60000); break;
            case Key.T: ToggleTopmost(); break; case Key.C: ToggleColor(); break; case Key.E: ToggleEconomy(); break;
            case Key.F5: _lastTick = 0; Update(); break;
            case Key.F9: ToggleGadget(); break;
            case Key.F11: WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen; break;
            case Key.Escape: WindowState = WindowState.Normal; break;
            default: return;
        }
        e.Handled = true;
    }
    private void SaveSettings()
    {
        if (WindowState == WindowState.Normal) { _settings.Width = Width; _settings.Height = Height; _settings.X = Position.X; _settings.Y = Position.Y; }
        _settings.Maximized = WindowState == WindowState.Maximized; _settings.Gadget = _gadget?.IsVisible == true;
        _settings.Topmost = Topmost; _settings.ProcSort = Math.Max(0, _sort.SelectedIndex);
        _settings.EconomyOn = _economy.Enabled; _settings.EcoTotal = _economy.Total; _settings.EcoPeak = _economy.PeakRate; _settings.EcoSeconds = _economy.Seconds;
        _settings.Pinned = _pinned.ToArray(); _settings.Save();
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

namespace PerfTicker;

internal static class MarketStyle
{
    public static readonly IBrush Background = Brush.Parse("#0B1018"), Panel = Brush.Parse("#111923"), Line = Brush.Parse("#253244"),
        Text = Brush.Parse("#D9E3EE"), Muted = Brush.Parse("#8192A9"), Gold = Brush.Parse("#E3BD70"), Up = Brush.Parse("#FF596E"), Down = Brush.Parse("#2DD5A3");
    public static TextBlock Label(string text, double size = 13, IBrush color = null) => new() { Text = text, FontSize = size, Foreground = color ?? Text, VerticalAlignment = VerticalAlignment.Center };
    public static string N(double value, string format = "F1") => double.IsFinite(value) ? value.ToString(format, CultureInfo.InvariantCulture) : "—";
    public static Border Box(Control control, Thickness? padding = null) => new() { Background = Panel, BorderBrush = Line, BorderThickness = new Thickness(1), Padding = padding ?? new Thickness(14), Child = control };
    public static Grid Row(string columns, params Control[] children)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns) };
        for (int i = 0; i < children.Length; i++) { Grid.SetColumn(children[i], i); grid.Children.Add(children[i]); }
        return grid;
    }
}

internal sealed class QuoteRow : INotifyPropertyChanged
{
    public Metric Metric { get; }
    public string Code => Metric.Code;
    public string Name => Metric.Name;
    public string Price => Metric.Display + " " + Metric.Unit;
    public string Change => Metric.ChgText;
    public IBrush Color => !Metric.Available ? MarketStyle.Muted : Metric.Chg >= 0 ? MarketStyle.Up : MarketStyle.Down;
    public QuoteRow(Metric metric) => Metric = metric;
    public event PropertyChangedEventHandler PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

internal sealed class MarketWindow : Window
{
    private IMonitorSampler _sampler;
    private RemoteConfig _config = RemoteConfig.Load();
    private bool _remote, _paused;
    private long _lastTick;
    private string _topology = "";
    private List<Metric> _metrics = new();
    private readonly ObservableCollection<QuoteRow> _quotes = new();
    private readonly ListBox _watch = new() { Background = Brushes.Transparent };
    private readonly StackPanel _cards = new() { Spacing = 10 };
    private readonly StackPanel _processes = new() { Spacing = 4 };
    private readonly TextBlock _status = MarketStyle.Label("正在启动…", 12), _warning = MarketStyle.Label("", 12, MarketStyle.Gold),
        _quote = MarketStyle.Label("等待行情", 23, MarketStyle.Gold), _host = MarketStyle.Label("本机", 12, MarketStyle.Muted),
        _cpu = MarketStyle.Label("—", 24), _memory = MarketStyle.Label("—", 24), _gpu = MarketStyle.Label("—", 24), _net = MarketStyle.Label("—", 24);
    private readonly CandleChart _chart = new();
    private readonly ComboBox _source = new() { ItemsSource = new[] { "本机行情", "SSH 服务器" }, SelectedIndex = 0, MinWidth = 145 };
    private readonly ComboBox _sort = new() { ItemsSource = new[] { "按 CPU", "按内存", "按显存" }, SelectedIndex = 0, MinWidth = 110 };
    private readonly ComboBox _group = new() { ItemsSource = new[] { "全部板块", "指数", "图形", "处理器", "内存", "存储网络", "系统" }, SelectedIndex = 0, MinWidth = 120 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Button _pauseButton = new() { Content = "暂停显示", MinWidth = 100 };
    public MarketWindow()
    {
        Title = "PerfTicker · 性能行情终端 · macOS / Linux";
        Width = 1400; Height = 900; MinWidth = 980; MinHeight = 680;
        Background = MarketStyle.Background; Foreground = MarketStyle.Text;
        FontFamily = new FontFamily("Microsoft YaHei, PingFang SC, Noto Sans CJK SC, WenQuanYi Micro Hei, sans-serif");
        var connect = new Button { Content = "连接设置", MinWidth = 100 };
        connect.Click += async (_, _) => await Configure();
        _source.SelectionChanged += async (_, _) => {
            if ((_source.SelectedIndex == 1) == _remote) return;
            if (_source.SelectedIndex == 0) Switch(false);
            else await Configure();
        };
        var pause = _pauseButton;
        pause.Click += (_, _) => { _paused = !_paused; pause.Content = _paused ? "继续显示" : "暂停显示"; Update(); };
        var interval = new ComboBox { ItemsSource = new[] { "1 秒采样", "2 秒采样", "5 秒采样" }, SelectedIndex = 0, MinWidth = 110 };
        interval.SelectionChanged += (_, _) => { if (_sampler != null) _sampler.IntervalMs = new[] { 1000, 2000, 5000 }[Math.Max(0, interval.SelectedIndex)]; };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = {
            MarketStyle.Label("PERFTICKER", 23, MarketStyle.Gold), MarketStyle.Label("性能行情终端", 15), _source, connect, interval, pause } };
        var index = MarketStyle.Row("*,*,*,*", Index("CPU 负载指数", _cpu), Index("内存占用指数", _memory), Index("GPU 平均负载", _gpu), Index("网络流入 · MB/s", _net));
        _watch.ItemsSource = _quotes;
        _watch.ItemTemplate = new FuncDataTemplate<QuoteRow>((_, _) => {
            TextBlock Bound(string path, double size = 12) { var t = MarketStyle.Label("", size); t.Bind(TextBlock.TextProperty, new Binding(path)); return t; }
            var code = Bound("Code"); code.Foreground = MarketStyle.Muted;
            var price = Bound("Price"); price.HorizontalAlignment = HorizontalAlignment.Right; price.Bind(TextBlock.ForegroundProperty, new Binding("Color"));
            return MarketStyle.Row("62,*,92", code, Bound("Name"), price);
        });
        _watch.SelectionChanged += (_, _) => SelectQuote();
        _group.SelectionChanged += (_, _) => RebuildQuotes();
        var left = new DockPanel { LastChildFill = true };
        var leftTitle = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 10), Children = { MarketStyle.Label("自选行情", 16, MarketStyle.Gold), _group,
            MarketStyle.Row("62,*,92", MarketStyle.Label("代码", 11, MarketStyle.Muted), MarketStyle.Label("品种", 11, MarketStyle.Muted), MarketStyle.Label("最新", 11, MarketStyle.Muted)) } };
        DockPanel.SetDock(leftTitle, Dock.Top); left.Children.Add(leftTitle); left.Children.Add(_watch);
        var period = new ComboBox { ItemsSource = new[] { "5 秒 K 线", "15 秒 K 线", "1 分钟 K 线" }, SelectedIndex = 0, MinWidth = 125 };
        period.SelectionChanged += (_, _) => { _chart.PeriodMs = new long[] { 5000, 15000, 60000 }[Math.Max(0, period.SelectedIndex)]; _chart.InvalidateVisual(); };
        var center = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,190") };
        var chartHead = new StackPanel { Spacing = 8, Children = { _quote, MarketStyle.Row("*,Auto", MarketStyle.Label("红涨绿跌  ·  涨跌以本次会话开盘值为基准", 11, MarketStyle.Muted), period) } };
        center.Children.Add(chartHead); Grid.SetRow(_chart, 1); center.Children.Add(_chart);
        var processHead = MarketStyle.Row("*,Auto", MarketStyle.Label("持仓透视 · 进程排行", 15, MarketStyle.Gold), _sort);
        processHead.Margin = new Thickness(0, 10, 0, 8); Grid.SetRow(processHead, 2); center.Children.Add(processHead);
        var processScroll = new ScrollViewer { Content = _processes }; Grid.SetRow(processScroll, 3); center.Children.Add(processScroll);
        var right = new DockPanel();
        var gpuHeader = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 12), Children = { MarketStyle.Label("显卡板块", 16, MarketStyle.Gold), MarketStyle.Label("点击显卡查看独立负载 K 线", 11, MarketStyle.Muted) } };
        DockPanel.SetDock(gpuHeader, Dock.Top); right.Children.Add(gpuHeader); right.Children.Add(new ScrollViewer { Content = _cards });
        var body = MarketStyle.Row("305,*,255", MarketStyle.Box(left), MarketStyle.Box(center), MarketStyle.Box(right));
        var root = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        header.Margin = new Thickness(0, 0, 0, 14); root.Children.Add(header);
        index.Margin = new Thickness(0, 0, 0, 12); Grid.SetRow(index, 1); root.Children.Add(index);
        Grid.SetRow(body, 2); root.Children.Add(body);
        _warning.TextWrapping = TextWrapping.Wrap;
        var footer = new StackPanel { Spacing = 5, Margin = new Thickness(0, 10, 0, 0), Children = { _status, _host, _warning } };
        Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        _timer.Tick += (_, _) => Update();
        Closed += (_, _) => { _timer.Stop(); _sampler?.Dispose(); };
        Switch(false); _timer.Start();
    }
    private static Border Index(string name, TextBlock value) => MarketStyle.Box(new StackPanel { Spacing = 7, Children = { MarketStyle.Label(name, 12, MarketStyle.Muted), value } });
    private async Task Configure()
    {
        var config = await new ConnectionWindow(_config).ShowDialog<RemoteConfig>(this);
        if (config != null) { _config = config; Switch(true); }
        else _source.SelectedIndex = _remote ? 1 : 0;
    }
    private void Switch(bool remote)
    {
        int interval = _sampler?.IntervalMs ?? 1000;
        _sampler?.Dispose(); _remote = remote; _lastTick = 0; _topology = ""; _metrics.Clear(); _quotes.Clear(); _cards.Children.Clear(); _processes.Children.Clear();
        _chart.Metric = null; _chart.InvalidateVisual(); _quote.Text = "等待行情";
        foreach (var label in new[] { _cpu, _memory, _gpu, _net }) label.Text = "—";
        _sampler = remote ? new RemoteSampler(interval, () => _config.Copy()) : new LocalSampler { IntervalMs = interval };
        _source.SelectedIndex = remote ? 1 : 0; _paused = false; _pauseButton.Content = "暂停显示"; Update();
    }
    private void RebuildQuotes()
    {
        string selected = (_watch.SelectedItem as QuoteRow)?.Code ?? "000002";
        string group = _group.SelectedIndex <= 0 ? "" : _group.SelectedItem?.ToString();
        _quotes.Clear();
        foreach (var m in _metrics.Where(m => group == "" || m.Group == group)) _quotes.Add(new QuoteRow(m));
        _watch.SelectedItem = _quotes.FirstOrDefault(r => r.Code == selected) ?? _quotes.FirstOrDefault();
    }
    private void SelectQuote()
    {
        _chart.Metric = (_watch.SelectedItem as QuoteRow)?.Metric;
        UpdateQuote();
    }
    private void UpdateQuote()
    {
        var m = _chart.Metric;
        if (m != null) { _quote.Text = $"{m.Name}  {m.Display} {m.Unit}  {m.ChgText}"; _quote.Foreground = !m.Available ? MarketStyle.Muted : m.Chg >= 0 ? MarketStyle.Up : MarketStyle.Down; }
        _chart.InvalidateVisual();
    }
    private void Update()
    {
        var s = _sampler.Latest;
        _status.Text = $"●  {(_remote ? "SSH 服务器" : "本机")}  /  {_sampler.Status}" + (_paused ? "  ·  显示已暂停" : "") + $"  /  {DateTime.Now:HH:mm:ss}";
        _status.Foreground = _sampler.IsLive ? MarketStyle.Down : MarketStyle.Gold;
        _warning.Text = _sampler.Error;
        _chart.Stale = !_sampler.IsLive; _chart.InvalidateVisual();
        if (_paused || !_sampler.IsLive || s.Tick == _lastTick) return;
        _lastTick = s.Tick;
        _host.Text = $"{s.HostName}  ·  {s.OsName}  ·  {s.CpuName}  ·  {s.CoreMHz.Length} 逻辑核心  ·  {s.Gpus.Count} 张 GPU";
        _cpu.Text = MarketStyle.N(s.CpuPct) + "%"; _memory.Text = MarketStyle.N(s.MemPct) + "%";
        _gpu.Text = MarketStyle.N(s.Gpu.UtilPct) + "%"; _net.Text = MarketStyle.N(s.NetRxMBps, "F2");
        string topology = s.CoreMHz.Length + ":" + string.Join("|", s.Gpus.OrderBy(g => g.Index).Select(g => g.Index + ":" + g.Id));
        bool changed = topology != _topology;
        if (changed)
        {
            _topology = topology;
            _metrics = Catalog.Reconcile(_metrics, s);
            var handles = _metrics.FirstOrDefault(m => m.Code == "000303");
            if (handles != null) handles.Read = v => v.Handles < 0 ? double.NaN : v.Handles;
        }
        foreach (var m in _metrics) m.Push(m.Read(s), (long)(s.WallSeconds * 1000), s.CpuPct);
        if (changed) RebuildQuotes();
        foreach (var row in _quotes) row.Refresh();
        UpdateQuote(); RenderCards(s); RenderProcesses(s);
    }
    private void RenderCards(Snapshot s)
    {
        _cards.Children.Clear();
        if (s.Gpus.Count == 0) { _cards.Children.Add(MarketStyle.Label("未取得显卡数据", 13, MarketStyle.Muted)); return; }
        foreach (var g in s.Gpus)
        {
            var name = MarketStyle.Label($"GPU {g.Index} · {g.Name}", 13, MarketStyle.Gold); name.TextWrapping = TextWrapping.Wrap;
            var fields = MarketStyle.Label($"负载  {MarketStyle.N(g.UtilPct)}%\n显存  {MarketStyle.N(g.MemUsedMB / 1024)} / {MarketStyle.N(g.MemTotalMB / 1024)} GB\n温度  {MarketStyle.N(g.TempC)} °C    功耗  {MarketStyle.N(g.PowerW)} W\n频率  {MarketStyle.N(g.ClockMHz, "F0")} MHz", 12);
            var button = new Button { HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel { Spacing = 9, Children = { name, fields } } };
            ToolTip.SetTip(button, g.Id);
            button.Click += (_, _) => { _group.SelectedIndex = 0; _watch.SelectedItem = _quotes.FirstOrDefault(r => r.Metric.GpuId == g.Id && r.Metric.Name.EndsWith("负载")); };
            _cards.Children.Add(button);
        }
    }
    private void RenderProcesses(Snapshot s)
    {
        _processes.Children.Clear();
        string gpuId = _chart.Metric?.GpuId ?? "";
        _processes.Children.Add(MarketStyle.Row("*,65,75,85", MarketStyle.Label("进程 / PID", 11, MarketStyle.Muted), MarketStyle.Label("CPU %", 11, MarketStyle.Muted), MarketStyle.Label("内存 MB", 11, MarketStyle.Muted), MarketStyle.Label("显存 MB", 11, MarketStyle.Muted)));
        double Read(ProcSample p) => _sort.SelectedIndex == 1 ? p.MemMB : _sort.SelectedIndex == 2 ? p.MemoryOnGpu(gpuId) : p.CpuNorm;
        foreach (var p in s.Procs.OrderByDescending(Read).Take(40))
        {
            var name = MarketStyle.Label($"{p.Name} · {p.Pid}", 12); name.TextTrimming = TextTrimming.CharacterEllipsis;
            _processes.Children.Add(MarketStyle.Row("*,65,75,85", name, MarketStyle.Label(MarketStyle.N(p.CpuNorm), 12), MarketStyle.Label(MarketStyle.N(p.MemMB, "F0"), 12), MarketStyle.Label(MarketStyle.N(p.MemoryOnGpu(gpuId), "F0"), 12)));
        }
    }
}

internal sealed class CandleChart : Control
{
    public Metric Metric;
    public long PeriodMs = 5000;
    public bool Stale;
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var area = new Rect(6, 35, Math.Max(1, Bounds.Width - 74), Math.Max(1, Bounds.Height - 75));
        void Text(string value, double x, double y, IBrush brush = null) => context.DrawText(new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei, PingFang SC, Noto Sans CJK SC, sans-serif"), 11, brush ?? MarketStyle.Muted), new Point(x, y));
        if (Metric == null || Metric.TickCount == 0) { Text("等待实时数据…", 20, 65); return; }
        Metric.RebuildCandles(PeriodMs);
        var candles = Metric.Candles.TakeLast(Math.Max(12, (int)area.Width / 9)).ToArray();
        if (candles.Length == 0) return;
        double low = candles.Min(c => c.L), high = candles.Max(c => c.H), pad = Math.Max((high - low) * .12, Math.Max(Math.Abs(high) * .005, .1));
        low -= pad; high += pad;
        double Y(double v) => area.Bottom - (v - low) / (high - low) * area.Height;
        for (int i = 0; i <= 4; i++)
        {
            double y = area.Top + area.Height * i / 4;
            context.DrawLine(new Pen(MarketStyle.Line, 1), new Point(area.Left, y), new Point(area.Right, y));
            Text(Metric.Fmt(high - (high - low) * i / 4), area.Right + 8, y - 7);
        }
        double step = area.Width / Math.Max(30, candles.Length);
        for (int i = 0; i < candles.Length; i++)
        {
            var c = candles[i]; double x = area.Left + step * (i + .5);
            var color = c.C >= c.O ? MarketStyle.Up : MarketStyle.Down;
            context.DrawLine(new Pen(color, 1), new Point(x, Y(c.H)), new Point(x, Y(c.L)));
            var body = new Rect(x - step * .32, Math.Min(Y(c.O), Y(c.C)), step * .64, Math.Max(1.5, Math.Abs(Y(c.O) - Y(c.C))));
            context.DrawRectangle(c.C >= c.O ? null : color, new Pen(color, 1), body);
        }
        Text($"{Metric.Code}  ·  OHLC  ·  {PeriodMs / 1000}s", area.Left, 12, MarketStyle.Gold);
        Text($"高 {Metric.Fmt(candles.Max(c => c.H))}     低 {Metric.Fmt(candles.Min(c => c.L))}     {candles.Length} 根", area.Left, area.Bottom + 14);
        if (Stale || !Metric.Available) Text(Stale ? "数据中断 · 保留历史" : "当前传感器不可用", area.Left + 160, 12, MarketStyle.Gold);
    }
}

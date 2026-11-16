using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using System.ComponentModel;
using System.Globalization;

namespace PerfTicker;

internal static class MarketStyle
{
    public static string Fonts => OperatingSystem.IsMacOS() ? "PingFang SC" : OperatingSystem.IsWindows() ? "Microsoft YaHei UI" : "Noto Sans CJK SC";
    public static readonly SolidColorBrush Background = new(), Panel = new(), Line = new(), Text = new(), Muted = new(), Gold = new(), Up = new(), Down = new(), Purple = new(), Cyan = new();
    public static bool Dark = true, RedUp = true;
    static MarketStyle() => Apply(true, true);
    public static void Apply(bool dark, bool redUp)
    {
        Dark = dark; RedUp = redUp;
        Background.Color = Color.Parse(dark ? "#0A0D13" : "#F7F9FC");
        Panel.Color = Color.Parse(dark ? "#0E121A" : "#FFFFFF");
        Line.Color = Color.Parse(dark ? "#232C3B" : "#D5DCE7");
        Text.Color = Color.Parse(dark ? "#E9EFF8" : "#16202E");
        Muted.Color = Color.Parse(dark ? "#8B98AA" : "#5D6B7C");
        Gold.Color = Color.Parse(dark ? "#F2C14E" : "#B4841A");
        var red = Color.Parse(dark ? "#FF4D4F" : "#DD3538");
        var green = Color.Parse(dark ? "#00C08B" : "#087E58");
        Up.Color = redUp ? red : green; Down.Color = redUp ? green : red;
        Purple.Color = Color.Parse(dark ? "#A889EE" : "#7051BA"); Cyan.Color = Color.Parse(dark ? "#54B9CB" : "#217E8F");
    }
    public static void Install(Styles styles)
    {
        styles.Add(new Style(x => x.OfType<Button>()) { Setters = {
            new Setter(Avalonia.Controls.Button.BackgroundProperty, Panel), new Setter(Avalonia.Controls.Button.ForegroundProperty, Text),
            new Setter(Avalonia.Controls.Button.BorderBrushProperty, Line), new Setter(Avalonia.Controls.Button.BorderThicknessProperty, new Thickness(1)),
            new Setter(Avalonia.Controls.Button.CornerRadiusProperty, new CornerRadius(3)), new Setter(Avalonia.Controls.Button.FontSizeProperty, 12d),
            new Setter(Avalonia.Controls.Button.MinHeightProperty, 28d), new Setter(Avalonia.Controls.Button.PaddingProperty, new Thickness(10, 4)) } });
        styles.Add(new Style(x => x.OfType<ComboBox>()) { Setters = {
            new Setter(ComboBox.FontSizeProperty, 12d), new Setter(ComboBox.MinHeightProperty, 28d),
            new Setter(ComboBox.PaddingProperty, new Thickness(9, 4)), new Setter(ComboBox.BorderBrushProperty, Line),
            new Setter(ComboBox.BackgroundProperty, Background) } });
        styles.Add(new Style(x => x.OfType<TextBox>()) { Setters = {
            new Setter(TextBox.FontSizeProperty, 12d), new Setter(TextBox.MinHeightProperty, 28d),
            new Setter(TextBox.PaddingProperty, new Thickness(9, 4)), new Setter(TextBox.BorderBrushProperty, Line),
            new Setter(TextBox.BackgroundProperty, Background) } });
        styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = {
            new Setter(ListBoxItem.PaddingProperty, new Thickness(7, 4)), new Setter(ListBoxItem.MinHeightProperty, 29d),
            new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        foreach (string name in new[] { "ButtonBackgroundPointerOver", "ButtonBackgroundPressed" })
            Application.Current.Resources[name] = Line;
    }
    public static TextBlock Label(string text, double size = 12, IBrush color = null) => new()
    { Text = text, FontSize = size, Foreground = color ?? Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    public static string N(double value, string format = "F1") => double.IsFinite(value) ? value.ToString(format, CultureInfo.InvariantCulture) : "—";
    public static Border Box(Control child, Thickness? padding = null) => new()
    { Background = Panel, BorderBrush = Line, BorderThickness = new Thickness(1), Padding = padding ?? new Thickness(12), Child = child };
    public static Grid Row(string columns, params Control[] children)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns) };
        for (int i = 0; i < children.Length; i++) { Grid.SetColumn(children[i], i); grid.Children.Add(children[i]); }
        return grid;
    }
    public static Button Button(string caption, Action action)
    {
        var b = new Button { Content = caption };
        b.Click += (_, _) => action(); return b;
    }
}

internal sealed class QuoteRow : INotifyPropertyChanged
{
    public Metric Metric { get; }
    public string Code => Metric.Code;
    public string Name => Metric.Name;
    public string Price => Metric.Display + " " + Metric.Unit;
    public string Change => Metric.ChgText;
    public double[] Spark => Metric.Spark(24);
    public IBrush Color => !Metric.Available ? MarketStyle.Muted : Metric.Chg >= 0 ? MarketStyle.Up : MarketStyle.Down;
    public QuoteRow(Metric metric) => Metric = metric;
    public event PropertyChangedEventHandler PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

internal sealed class ProcessRow : INotifyPropertyChanged
{
    public ProcSample Sample = new();
    public string GpuId = "";
    public bool Pinned;
    private readonly Queue<double> _history = new();
    public string Pid => Sample.Pid.ToString();
    public string Name => (Pinned ? "★ " : "") + Sample.Name;
    public string Cpu => MarketStyle.N(Sample.CpuPct);
    public string Delta => MarketStyle.N(Sample.CpuDelta, "+0.0;-0.0;0.0");
    public string Memory => MarketStyle.N(Sample.MemMB, "F0");
    public string Gpu => MarketStyle.N(Sample.MemoryOnGpu(GpuId), "F0");
    public string Threads => Sample.Threads.ToString();
    public string Handles => Sample.Handles < 0 ? "—" : Sample.Handles.ToString();
    public double[] Spark => _history.ToArray();
    public IBrush Color => Sample.CpuDelta >= 0 ? MarketStyle.Up : MarketStyle.Down;
    public event PropertyChangedEventHandler PropertyChanged;
    public void Push(ProcSample sample)
    {
        sample.CpuDelta = _history.Count == 0 ? 0 : sample.CpuPct - Sample.CpuPct;
        Sample = sample; _history.Enqueue(sample.CpuPct); if (_history.Count > 36) _history.Dequeue();
    }
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

internal static class DesktopQueries
{
    public static IEnumerable<ProcessRow> Processes(IEnumerable<ProcessRow> rows, string filter, int sort, bool descending, string gpuId)
    {
        rows = rows.Where(p => string.IsNullOrWhiteSpace(filter) || p.Sample.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || p.Pid.Contains(filter));
        double Value(ProcessRow p) => sort switch { 1 => p.Sample.MemoryOnGpu(gpuId), 2 => p.Sample.MemMB, 3 => p.Sample.Threads, _ => p.Sample.CpuPct };
        // Unknown readings stay at the end in either direction.
        return rows.OrderByDescending(p => p.Pinned).ThenBy(p => !double.IsFinite(Value(p)))
            .ThenBy(p => descending ? -Value(p) : Value(p)).ThenBy(p => p.Sample.Pid);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System.Globalization;

namespace PerfTicker;

internal sealed class Sparkline : Control
{
    public static readonly StyledProperty<double[]> ValuesProperty = AvaloniaProperty.Register<Sparkline, double[]>(nameof(Values), Array.Empty<double>());
    public double[] Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    static Sparkline() => AffectsRender<Sparkline>(ValuesProperty);
    public override void Render(DrawingContext c)
    {
        var v = Values.Where(double.IsFinite).ToArray(); if (v.Length < 2 || Bounds.Width < 1) return;
        double lo = v.Min(), range = Math.Max(.01, v.Max() - lo), step = Bounds.Width / (v.Length - 1);
        for (int i = 1; i < v.Length; i++)
            c.DrawLine(new Pen(v[i] >= v[i - 1] ? MarketStyle.Up : MarketStyle.Down, 1.3),
                new Point((i - 1) * step, Bounds.Height - 2 - (v[i - 1] - lo) / range * (Bounds.Height - 4)),
                new Point(i * step, Bounds.Height - 2 - (v[i] - lo) / range * (Bounds.Height - 4)));
    }
}

internal sealed class CandleChart : Control
{
    public Metric Metric;
    public long PeriodMs = 2000;
    public bool Stale;
    private Point? _pointer;
    private Metric _cachedMetric;
    private long _lastMs = -1, _cachedPeriod;
    public int RenderCount { get; private set; }
    public CandleChart()
    {
        ClipToBounds = true;
        PointerMoved += (_, e) => { _pointer = e.GetPosition(this); InvalidateVisual(); };
        PointerExited += (_, _) => { _pointer = null; InvalidateVisual(); };
    }
    private static void Text(DrawingContext c, string value, double x, double y, IBrush brush = null, double size = 11) =>
        c.DrawText(new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(MarketStyle.Fonts), size, brush ?? MarketStyle.Muted), new Point(x, y));
    public override void Render(DrawingContext c)
    {
        base.Render(c); RenderCount++;
        c.FillRectangle(MarketStyle.Background, new Rect(Bounds.Size));
        if (Metric == null || Metric.TickCount == 0) { Text(c, Stale ? "等待实时数据…" : "该指标暂不可用", 20, 45); return; }
        long lastMs = Metric.TickAt(Metric.TickCount - 1).Ms;
        if (_cachedMetric != Metric || _lastMs != lastMs || _cachedPeriod != PeriodMs)
        {
            Metric.RebuildCandles(PeriodMs); _cachedMetric = Metric; _lastMs = lastMs; _cachedPeriod = PeriodMs;
        }
        int slots = Math.Clamp((int)((Bounds.Width - 80) / 10), 12, 100);
        var bars = Metric.Candles.TakeLast(slots).ToArray(); if (bars.Length == 0) return;
        bool hasVolume = bars.Any(b => double.IsFinite(b.Vol)) && Bounds.Height >= 230;
        var plot = new Rect(12, 32, Math.Max(1, Bounds.Width - 78), Math.Max(30, hasVolume ? (Bounds.Height - 90) * .75 : Bounds.Height - 60));
        var volume = new Rect(plot.Left, plot.Bottom + 20, plot.Width, Math.Max(16, Bounds.Height - plot.Bottom - 48));
        double lo = bars.Min(x => x.L), hi = bars.Max(x => x.H), pad = Math.Max(.1, (hi - lo) * .12);
        lo -= pad; hi += pad;
        double Y(double n) => plot.Bottom - (n - lo) / (hi - lo) * plot.Height;
        double step = plot.Width / slots, start = plot.Right - bars.Length * step;
        for (int i = 0; i <= 4; i++)
        {
            double y = plot.Top + i * plot.Height / 4;
            c.DrawLine(new Pen(MarketStyle.Line, 1), new Point(plot.Left, y), new Point(plot.Right, y));
            Text(c, Metric.Fmt(hi - (hi - lo) * i / 4), plot.Right + 6, y - 6);
        }
        double maxVol = Math.Max(1, bars.Where(b => double.IsFinite(b.Vol)).Select(b => b.Vol).DefaultIfEmpty(1).Max());
        for (int i = 0; i < bars.Length; i++)
        {
            var b = bars[i]; double x = start + (i + .5) * step;
            var brush = b.C >= b.O ? MarketStyle.Up : MarketStyle.Down;
            c.DrawLine(new Pen(brush, 1), new Point(x, Y(b.H)), new Point(x, Y(b.L)));
            c.DrawRectangle(b.C >= b.O ? null : brush, new Pen(brush, 1), new Rect(x - step * .32, Math.Min(Y(b.O), Y(b.C)), step * .64, Math.Max(1.5, Math.Abs(Y(b.O) - Y(b.C)))));
            if (hasVolume && double.IsFinite(b.Vol) && b.Vol > 0)
            {
                double h = b.Vol / maxVol * volume.Height;
                c.FillRectangle(brush, new Rect(x - step * .32, volume.Bottom - h, step * .64, h));
            }
        }
        var averages = new[] { (5, MarketStyle.Gold), (10, MarketStyle.Purple), (20, MarketStyle.Cyan) };
        int offset = Metric.Candles.Count - bars.Length;
        int label = 0;
        foreach (var (length, brush) in averages)
        {
            Point? previous = null; double value = double.NaN;
            for (int i = 0; i < bars.Length; i++)
            {
                int end = offset + i; if (end + 1 < length) continue;
                value = Enumerable.Range(end - length + 1, length).Average(n => Metric.Candles[n].C);
                var point = new Point(start + (i + .5) * step, Y(value));
                if (previous is Point p) c.DrawLine(new Pen(brush, 1.2), p, point);
                previous = point;
            }
            Text(c, $"MA{length} {Metric.Fmt(value)}", plot.Left + label++ * 112, 10, brush);
        }
        if (hasVolume) Text(c, "采样活跃量", volume.Left, volume.Top - 16);
        double footerY = Bounds.Height - 17;
        Text(c, $"{PeriodMs / 1000}秒 K  ·  {bars.Length} 根", volume.Left, footerY);
        if (_pointer is Point mouse && plot.Contains(mouse))
        {
            int index = Math.Clamp((int)((mouse.X - start) / step), 0, bars.Length - 1);
            var b = bars[index]; double x = start + (index + .5) * step;
            c.DrawLine(new Pen(MarketStyle.Muted, .8), new Point(x, plot.Top), new Point(x, hasVolume ? volume.Bottom : plot.Bottom));
            c.DrawLine(new Pen(MarketStyle.Muted, .8), new Point(plot.Left, mouse.Y), new Point(plot.Right, mouse.Y));
            c.FillRectangle(MarketStyle.Background, new Rect(0, footerY - 2, Bounds.Width, 20));
            Text(c, $"开 {Metric.Fmt(b.O)}  高 {Metric.Fmt(b.H)}  低 {Metric.Fmt(b.L)}  收 {Metric.Fmt(b.C)}", plot.Left, footerY, MarketStyle.Gold);
        }
        if (Stale || !Metric.Available) Text(c, Stale ? "数据中断 · 保留历史" : "当前传感器不可用", plot.Left + 10, plot.Top + 12, MarketStyle.Gold);
    }
}

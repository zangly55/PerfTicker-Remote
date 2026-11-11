using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using System.Text.Json;

namespace PerfTicker;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--probe-local"))
        {
            using var sampler = new LocalSampler();
            long tick = 0;
            for (int i = 0; i < 3; i++)
            {
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (sampler.Latest.Tick <= tick && DateTime.UtcNow < deadline) Thread.Sleep(100);
                if (sampler.Latest.Tick <= tick) { Console.Error.WriteLine(sampler.Error); return 1; }
                var s = sampler.Latest;
                if (!double.IsFinite(s.CpuPct) || s.MemTotalGB <= 0) return 2;
                tick = s.Tick;
                Console.WriteLine(JsonSerializer.Serialize(s, RemoteSampler.JsonOptions));
            }
            return 0;
        }
        Diag.Init();
        AppBuilder.Configure<DesktopApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
        return 0;
    }
}

public sealed class DesktopApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = new MarketWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

internal interface IMonitorSampler : IDisposable
{
    Snapshot Latest { get; }
    int Cores { get; }
    string CpuName { get; }
    string GpuDriverVersion { get; }
    int IntervalMs { get; set; }
    bool IsLive { get; }
    string Status { get; }
    string Error { get; }
}

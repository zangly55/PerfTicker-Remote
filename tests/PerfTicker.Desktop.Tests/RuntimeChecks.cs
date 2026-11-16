using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PerfTicker;
using System.Reflection;
using System.Text.Json;

// Explicit opt-in integration checks. The default test command remains fixture-only.
internal static class RuntimeChecks
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

    private static async Task Until(Func<bool> ready, string name, int seconds = 45)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(100);
        Check(ready(), name);
    }

    public static int Gui()
    {
        string previous = File.Exists(DesktopSettings.FilePath) ? File.ReadAllText(DesktopSettings.FilePath) : null;
        try
        {
            new DesktopSettings().Save();
            if (!OperatingSystem.IsWindows()) return GuiCore();
            int result = 1;
            var thread = new Thread(() => result = GuiCore());
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return result;
        }
        finally
        {
            if (previous != null) File.WriteAllText(DesktopSettings.FilePath, previous);
            else if (File.Exists(DesktopSettings.FilePath)) File.Delete(DesktopSettings.FilePath);
        }
    }
    private static int GuiCore() => AppBuilder.Configure<DesktopApp>().UsePlatformDetect().LogToTrace()
        .AfterSetup(builder => Dispatcher.UIThread.Post(async () =>
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)builder.Instance.ApplicationLifetime;
            int exit = 1;
            try
            {
                var window = (MarketWindow)desktop.MainWindow;
                await Until(() => window.IsVisible && Field<long>(window, "_lastTick") >= 3, "native window receives three live samples");
                Check(window.Bounds.Width > 900 && window.Bounds.Height > 600, "native window has usable dimensions");
                Check(Field<TextBlock>(window, "_cpu").Text != "—" && Field<TextBlock>(window, "_memory").Text != "—", "CPU and memory reach UI");
                var processes = Field<ListBox>(window, "_processes");
                Check(processes.ItemCount > 1, "virtualized process rows reach UI");
                var chart = Field<CandleChart>(window, "_chart");
                await Until(() => chart.Metric?.Candles.Count > 0, "native renderer draws live candles");
                var pause = Field<Button>(window, "_pauseButton");
                pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                long tick = Field<long>(window, "_lastTick");
                await Task.Delay(1600);
                Check(Field<long>(window, "_lastTick") == tick && Field<bool>(window, "_paused"), "pause freezes displayed samples");
                var search = Field<TextBox>(window, "_search");
                search.Text = "this-process-cannot-exist-92a4";
                await Until(() => processes.ItemCount == 0, "process search filters live table");
                search.Text = "";
                await Until(() => processes.ItemCount > 1, "clearing search restores all processes");
                window.SetInterval(10000); window.SetPeriod(2000);
                Check(chart.PeriodMs == 30000, "sampling interval prevents misleading short candles");
                window.SetInterval(500); window.SetPeriod(2000);
                Check(chart.PeriodMs == 2000, "Windows 0.5s sampling and 2s candles supported");
                window.ToggleTopmost(); Check(window.Topmost, "topmost toggle"); window.ToggleTopmost();
                var up = MarketStyle.Up.Color; window.ToggleColor(); Check(MarketStyle.Up.Color != up, "red/green color preference"); window.ToggleColor();
                window.ToggleEconomy(); Check(Field<Border>(window, "_ecoPanel").IsVisible, "shared simulation panel opens"); window.ToggleEconomy();
                window.ToggleGadget(); Check(Field<Window>(window, "_gadget").IsVisible, "native floating gadget opens"); window.ToggleGadget();
                window.ShowGpus(); Check(Field<Window>(window, "_gpuWindow").IsVisible, "GPU overview window opens"); Field<Window>(window, "_gpuWindow").Close();
                var uiSettings = Field<DesktopSettings>(window, "_settings");
                Check(Field<Dictionary<string, (TextBlock, TextBlock, Sparkline)>>(window, "_indices").Count == 8, "eight Windows-style index tiles");
                pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => Field<long>(window, "_lastTick") > tick, "resume refreshes samples");
                Field<ComboBox>(window, "_source").SelectedIndex = 1;
                await Until(() => desktop.Windows.OfType<ConnectionWindow>().Any(w => w.IsVisible), "SSH settings dialog opens");
                desktop.Windows.OfType<ConnectionWindow>().Single().Close();
                await Until(() => Field<ComboBox>(window, "_source").SelectedIndex == 0, "cancel restores local source");
                window.Width = 1440; window.Height = 960;
                await Task.Delay(1000);
                string output = Environment.GetEnvironmentVariable("PERFTICKER_TEST_OUTPUT") ?? "artifacts/runtime";
                Directory.CreateDirectory(output);
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                bitmap.Render(window); bitmap.Save(Path.Combine(output, "macos-window.png"), PngBitmapEncoderOptions.Default);
                Check(new FileInfo(Path.Combine(output, "macos-window.png")).Length > 10000, "window screenshot rendered after resize");
                window.Width = 1000; window.Height = 700;
                await Task.Delay(1000);
                using var small = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                small.Render(window); small.Save(Path.Combine(output, "macos-window-small.png"), PngBitmapEncoderOptions.Default);
                window.SetTheme(1);
                await Task.Delay(500);
                Check(!MarketStyle.Dark, "light palette follows appearance setting");
                using var light = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                light.Render(window); light.Save(Path.Combine(output, "macos-window-light.png"), PngBitmapEncoderOptions.Default);
                window.SetTheme(0);
                window.Close();
                var saved = DesktopSettings.Load();
                Check(saved.IntervalMs == 500 && saved.Width == 1000 && saved.PeriodMs == 2000, "preferences survive closing and reloading");
                exit = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { desktop.Shutdown(exit); }
        })).StartWithClassicDesktopLifetime(Array.Empty<string>());

    public static async Task<int> Ssh()
    {
        try
        {
            var c = JsonSerializer.Deserialize<RemoteConfig>(File.ReadAllText(Environment.GetEnvironmentVariable("PERFTICKER_SSH_FIXTURE")));
            c.SetPassword("integration-only");
            Check(await SshConnection.InspectHostAsync(c, CancellationToken.None) == c.Fingerprint, "SSH fingerprint discovery");
            var wrong = c.Copy(); wrong.Fingerprint = "SHA256:" + new string('a', 43);
            using (var rejected = new SshConnection(wrong))
            {
                bool denied = false;
                try { rejected.Connect(); } catch (InvalidOperationException) { denied = rejected.KeyMismatch; }
                Check(denied, "SSH rejects wrong host key");
            }
            foreach (string auth in new[] { "password", "key" })
            {
                c.AuthMethod = auth;
                using var sampler = new RemoteSampler(500, () => c.Copy());
                await Until(() => sampler.IsLive && sampler.Latest.Tick >= 3, auth + " authentication streams snapshots");
                Check(sampler.Latest.Gpus.Count == 4 && sampler.Latest.IsRemote, auth + " four GPU payload survives transport");
                long tick = sampler.Latest.Tick;
                sampler.IntervalMs = 1000;
                await Until(() => sampler.IsLive && sampler.Latest.Tick > tick + 2, auth + " reconnect after interval change");
                // The fixture deliberately closes every channel after six samples.
                tick = sampler.Latest.Tick;
                await Until(() => sampler.IsLive && sampler.Latest.Tick > tick + 8, auth + " reconnect after server EOF");
                sampler.Dispose();
                Check(!sampler.IsLive, auth + " dispose marks source offline");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

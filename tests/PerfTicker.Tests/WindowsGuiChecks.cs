using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using PerfTicker;

internal static class WindowsGuiChecks
{
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    public static int Run()
    {
        int exit = 1;
        var thread = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            var settings = new Settings { MonitorMode = MonitorSource.Remote };
            using var form = new AppForm(settings, new FixtureSampler());
            form.Shown += async (_, _) =>
            {
                try
                {
                    await Task.Delay(1600);
                    Check(form.St.Snap.Tick > 0, "Windows native window ingests isolated fixture");
                    foreach (var metric in form.St.Metrics)
                        for (int i = 0; i < 120; i++) metric.Push(30 + Math.Sin(i / 5.0) * 8, (Environment.TickCount64 / 1000 - 120 + i) * 1000, 100 + i);
                    form.St.Selected.RebuildCandles(form.St.PeriodMs);
                    await Task.Delay(1400); // Let the production chart's scale animation settle.
                    string output = Environment.GetEnvironmentVariable("PERFTICKER_TEST_OUTPUT") ?? "artifacts/windows-ui";
                    Directory.CreateDirectory(output);
                    void Capture(string name, int width, int height)
                    {
                        form.Size = new Size(Theme.Px(width), Theme.Px(height)); form.Refresh();
                        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
                        form.DrawToBitmap(bitmap, form.ClientRectangle); bitmap.Save(Path.Combine(output, name));
                    }
                    Capture("windows-dark.png", 1440, 900);
                    var instrument = Field<InstrumentPanel>(form, "_inst");
                    Check(instrument.Tabs.All(r => r.Left > 0 && r.Right <= instrument.Bounds.Width), "compact Windows periods fit the panel");
                    var tab = instrument.Tabs[1];
                    instrument.Click(new Point(instrument.Bounds.X + tab.Left + tab.Width / 2, instrument.Bounds.Y + tab.Top + tab.Height / 2), MouseButtons.Left);
                    Check(form.St.PeriodMs == 5000, "Windows period selection uses visible hit target");
                    form.SetInterval(10000); instrument.Click(new Point(instrument.Bounds.X + tab.Left + 2, instrument.Bounds.Y + tab.Top + 2), MouseButtons.Left);
                    Check(form.St.PeriodMs == 30000, "Windows disabled short periods remain blocked");
                    form.SetInterval(1000); form.SetChartMode(2000);
                    form.TogglePause(); Check(form.St.Paused, "Windows pause works after visual changes"); form.TogglePause();
                    Capture("windows-small.png", 1180, 720);
                    var proc = Field<ProcPanel>(form, "_proc");
                    var columns = ((float, float, float, float, float, float, float))typeof(ProcPanel).GetMethod("Cols", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(proc, null);
                    Check(columns.Item7 > proc.Bounds.Width * .7 && columns.Item1 > Theme.Px(350), "process columns occupy correct physical positions at display DPI");
                    Check(instrument.Tabs[0].Left > Theme.Px(200), "small Windows layout leaves room for the quote title");
                    settings.ThemeMode = 1;
                    typeof(AppForm).GetMethod("ApplyTheme", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null);
                    Capture("windows-light.png", 1180, 720);
                    Check(!Theme.Dark, "Windows light theme renders");
                    exit = 0;
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                finally { form.Close(); }
            };
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return exit;
    }

    private sealed class FixtureSampler : IMonitorSampler
    {
        public Snapshot Latest => new() {
            Protocol = 2, Tick = Environment.TickCount64 / 1000, WallSeconds = Environment.TickCount64 / 1000.0,
            IsRemote = true, HostName = "UI validation fixture", OsName = "Linux fixture", CpuName = "Fixture CPU",
            CpuPct = 42, CpuAvgMHz = 2600, CpuMaxMHz = 2900, CoreMHz = Enumerable.Repeat(2600d, 8).ToArray(),
            MemTotalGB = 32, MemUsedGB = 14, MemPct = 43.75, NetRxMBps = 2.4, NetTxMBps = .2,
            DiskReadMBps = 12, DiskWriteMBps = 4, CtxSwitchPerSec = 120, ProcCount = 20, Threads = 140,
            Procs = Enumerable.Range(1, 20).Select(i => new ProcSample { Pid = 400 + i, Name = i == 1 ? "python-training-worker" : "worker-" + i, CpuPct = 75.0 / i, MemMB = 80 + i * 12, Threads = 7 }).ToList(),
            Gpus = new() { new() { Index = 0, Id = "GPU-fixture", Name = "NVIDIA UI fixture", Available = true, UtilPct = 52, ClockMHz = 1800, MemUsedMB = 2048, MemTotalMB = 8192, TempC = 55, PowerW = 120 } }
        };
        public int Cores => 8;
        public string CpuName => "Fixture CPU";
        public string GpuDriverVersion => "fixture";
        public int IntervalMs { get; set; } = 1000;
        public bool IsLive => true;
        public string Status => "界面测试样例";
        public string Error => "";
        public void Dispose() { }
    }
}

using System.Text.Json;

namespace PerfTicker;

internal sealed class DesktopSettings
{
    public int IntervalMs { get; set; } = 1000;
    public long PeriodMs { get; set; } = 2000;
    public bool RedUp { get; set; } = true;
    public bool Topmost { get; set; }
    public int ThemeMode { get; set; }
    public bool EconomyOn { get; set; }
    public bool Marquee { get; set; } = true;
    public double EcoTotal { get; set; }
    public double EcoPeak { get; set; }
    public double EcoSeconds { get; set; }
    public string SelectedCode { get; set; } = "000002";
    public int ProcSort { get; set; }
    public bool SortDescending { get; set; } = true;
    public double Width { get; set; } = 1440;
    public double Height { get; set; } = 960;
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool Maximized { get; set; }
    public bool Gadget { get; set; }
    public string[] Pinned { get; set; } = Array.Empty<string>();
    internal static readonly int[] Intervals = { 500, 1000, 2000, 5000, 10000 };
    internal static readonly long[] Periods = { 2000, 5000, 10000, 30000, 60000 };
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PerfTickerDesktop", "settings.json");

    internal void Normalize()
    {
        if (!Intervals.Contains(IntervalMs)) IntervalMs = 1000;
        if (!Periods.Contains(PeriodMs) || PeriodMs < 2L * IntervalMs) PeriodMs = Periods.First(p => p >= 2L * IntervalMs);
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 1000, 3840) : 1440;
        Height = double.IsFinite(Height) ? Math.Clamp(Height, 700, 2160) : 960;
        ThemeMode = Math.Clamp(ThemeMode, 0, 2); ProcSort = Math.Clamp(ProcSort, 0, 3);
        if (!double.IsFinite(EcoTotal)) EcoTotal = 0;
        if (!double.IsFinite(EcoPeak)) EcoPeak = 0;
        if (!double.IsFinite(EcoSeconds) || EcoSeconds < 0) EcoSeconds = 0;
        Pinned = (Pinned ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToArray();
    }
    public static DesktopSettings Load()
    {
        try
        {
            var s = File.Exists(FilePath) ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath)) ?? new() : new();
            s.Normalize(); return s;
        }
        catch (Exception ex)
        {
            Diag.Error("DESKTOP-SETTINGS", ex);
            try { File.Copy(FilePath, FilePath + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
            return new();
        }
    }
    public void Save()
    {
        try
        {
            Normalize(); Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch (Exception ex) { Diag.Error("DESKTOP-SETTINGS", ex); }
    }
}

// Same snapshot-based weights as the Windows client's Hardware.ProbeRemote.
// No Windows registry/native APIs are needed by the shared simulation engine.
internal sealed class Hardware
{
    public double Wcpu = 1, Wgpu = 1, Wmem = 1, Wdisk = 1, Wnet = 1;
    public void Apply(Snapshot s)
    {
        Wcpu = Math.Clamp(s.CoreMHz.Length / 16.0, .30, 4);
        double memory = double.IsFinite(s.Gpu.MemTotalMB) ? s.Gpu.MemTotalMB / 1024 : 0;
        Wgpu = s.Gpu.Available ? Math.Clamp(Math.Max(memory, 2) / 8.0, .30, 4) : .25;
        Wmem = Math.Clamp(s.MemTotalGB / 32, .30, 4);
        Wdisk = Math.Clamp(Math.Max(1, s.FixedDisks) / 2.0, .50, 2);
        Wnet = 1;
    }
}

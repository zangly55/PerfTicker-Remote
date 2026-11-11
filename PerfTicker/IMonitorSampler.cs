namespace PerfTicker;

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

internal static class MonitorSource
{
    public const string Local = "local";
    public const string Remote = "remote";
    public static IMonitorSampler Create(string mode, int interval) => mode == Local
        ? new Sampler { IntervalMs = interval } : new RemoteSampler(interval);
}

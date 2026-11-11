using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PerfTicker;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}
async Task Until(Func<bool> condition, int seconds = 15)
{
    var until = DateTime.UtcNow.AddSeconds(seconds);
    while (!condition() && DateTime.UtcNow < until) await Task.Delay(100);
    if (!condition()) throw new Exception("Timed out waiting for test condition");
}

Snapshot Fixture(int count) => new() {
    Protocol = 2, IsRemote = true, HostName = "fixture-server", CpuPct = 25, MemTotalGB = 64,
    CoreMHz = new double[] { 2400, 2500, 2600, 2700 }, CorePct = new double[] { 20, 30, 25, 25 },
    Gpus = Enumerable.Range(0, count).Select(i => new GpuInfo {
        Index = i, Id = "GPU-fixture-" + i, Name = "NVIDIA test GPU", Available = true,
        UtilPct = 10 + i, MemUsedMB = 100 + i, MemTotalMB = 4096, PowerW = 50 + i,
        ClockMHz = 1000 + i, MemClockMHz = 2000, MaxClockMHz = 2500, TempC = 60 + i,
        PowerLimitW = 300, FanPct = 35, MemUtilPct = 10
    }).ToList(),
    Procs = new() { new() { Pid = 42, Name = "test-worker", GpuPct = double.NaN,
        GpuMemMB = 300, GpuMemory = new() { ["GPU-fixture-0"] = 100, ["GPU-fixture-1"] = 200 } } }
};
Snapshot RoundTrip(Snapshot value) => RemoteSampler.Parse(JsonSerializer.Serialize(value, RemoteSampler.JsonOptions));
var samples = JsonDocument.Parse(args.Length > 0 && !args[0].StartsWith("--") ? File.ReadAllText(args[0])
    : JsonSerializer.Serialize(new[] { Fixture(1) }, RemoteSampler.JsonOptions)).RootElement;
var sample = RemoteSampler.Parse(samples[0].GetRawText());
Check(sample.IsRemote && sample.CoreMHz.Length > 0 && sample.Gpus != null, "Linux sample schema and remote hardware");
Check(double.IsNaN(sample.Procs[0].GpuPct), "unavailable process GPU utilization is NaN");
try { RemoteSampler.Parse("{\"Protocol\":1}"); throw new Exception("Parser accepted invalid sample"); }
catch (InvalidDataException) { Check(true, "reject incomplete snapshot"); }
var metric = new Metric();
metric.Push(double.NaN, 1, 1);
Check(metric.Count == 0 && metric.ShownText == "—", "missing sensor produces no fabricated candle");
metric.Push(10, 1000, 1);
metric.Push(20, 1500, 1);
metric.Push(double.NaN, 2000, 1);
metric.RebuildCandles(2000);
Check(metric.Count == 2 && metric.Candles.Count == 1 && metric.Candles[0].O == 10 && metric.Candles[0].C == 20 && !metric.Available, "OHLC ignores missing values and retains history");

foreach (int count in new[] { 4, 8, 32 })
{
    var multi = RoundTrip(Fixture(count));
    var catalog = Catalog.Build(multi.CoreMHz.Length, multi.Gpus);
    Check(multi.Gpus.Count == count && multi.Gpu.MemTotalMB == 4096 * count
        && multi.Gpu.UtilPct == 10 + (count - 1) / 2.0 && multi.Gpu.TempC == 60 + count - 1,
        $"{count} GPU inventory and aggregate sum/mean/max semantics");
    Check(catalog.Where(m => m.Name.EndsWith("负载") && m.GpuId.Length > 0).Count() == count
        && catalog.Select(m => m.Code).Distinct().Count() == catalog.Count,
        $"{count} GPUs have independent, unique market instruments");
    var last = catalog.Single(m => m.GpuId == multi.Gpus[^1].Id && m.Name.EndsWith("负载"));
    multi.Gpus.Reverse();
    Check(last.Read(multi) == 10 + count - 1, $"{count} GPU metrics follow UUID after enumeration reorder");
}
var four = RoundTrip(Fixture(4));
Check(four.Procs[0].MemoryOnGpu("GPU-fixture-0") == 100 && four.Procs[0].MemoryOnGpu("GPU-fixture-1") == 200
    && four.Procs[0].MemoryOnGpu("") == 300 && double.IsNaN(four.Procs[0].MemoryOnGpu("missing")),
    "process holdings use the selected GPU UUID and total is separate");
var missing = Fixture(4); missing.Gpus[1].PowerW = double.NaN;
Check(double.IsNaN(RoundTrip(missing).Gpu.PowerW), "missing card sensor does not fabricate a complete total");
var catalog4 = Catalog.Build(4, four.Gpus);
var cpuMetric = catalog4.Single(m => m.Code == "000002"); cpuMetric.Push(25, 1000, 1);
var cardMetric = catalog4.Single(m => m.GpuId == "GPU-fixture-0" && m.Name.EndsWith("负载")); cardMetric.Push(10, 1000, 1);
var catalog8 = Catalog.Reconcile(catalog4, RoundTrip(Fixture(8)));
Check(catalog8.Contains(cpuMetric) && catalog8.Contains(cardMetric) && cardMetric.Count == 1,
    "adding GPUs preserves existing CPU and card histories");
var replacement = Fixture(4); replacement.Gpus[0].Id = "GPU-replacement";
var replaced = Catalog.Reconcile(catalog4, RoundTrip(replacement));
Check(!replaced.Contains(cardMetric) && replaced.Contains(cpuMetric), "replacing a GPU cannot inherit a different device's candles");
var legacy = Fixture(0); legacy.Protocol = 1; legacy.Gpu = Fixture(1).Gpus[0];
Check(RoundTrip(legacy).Gpus.Count == 1, "legacy single-GPU protocol migrates to inventory");
Check(RoundTrip(Fixture(0)).Gpus.Count == 0 && double.IsNaN(RoundTrip(Fixture(0)).Gpu.UtilPct), "CPU-only server has no invented GPU readings");
var duplicate = Fixture(4); duplicate.Gpus[1].Id = duplicate.Gpus[0].Id;
try { RoundTrip(duplicate); throw new Exception("Duplicate UUID accepted"); }
catch (InvalidDataException) { Check(true, "duplicate GPU identities rejected"); }

var profiles = SshDiscovery.Parse("""
Host four eight
  HostName 192.0.2.15
  User model
  Port 2222
  IdentityFile "~/.ssh/key with spaces"
Host !eight *
  User ignored
Host jump
  HostName 2001:db8::20
  ProxyJump gateway
Match exec "this-must-never-run"
  HostName malicious.invalid
Host *
  Port 22
Include never-executed.conf
""", @"C:\test-user", out var note);
Check(profiles.Count == 3 && profiles[0].Host == "192.0.2.15" && profiles[0].Port == 2222
    && profiles[0].User == "model" && profiles[0].KeyPath.EndsWith("key with spaces"),
    "SSH config imports aliases, quoted keys and first-value precedence");
Check(profiles[2].NeedsProxy && profiles[2].Host == "2001:db8::20" && note.Contains("Match"),
    "SSH discovery flags unsupported proxies and never executes Match/Include");

var protectedConfig = new RemoteConfig();
protectedConfig.SetPassword("fixture-secret"); protectedConfig.SetKeyPassphrase("fixture-key-secret");
string configJson = JsonSerializer.Serialize(protectedConfig);
using (var parsedConfig = JsonDocument.Parse(configJson))
    Check(!parsedConfig.RootElement.TryGetProperty("Password", out _) && !parsedConfig.RootElement.TryGetProperty("KeyPassphrase", out _)
        && !configJson.Contains("fixture-secret") && !configJson.Contains("fixture-key-secret"), "configuration never serializes decrypted credentials");
string legacyPath = Path.GetTempFileName();
try {
    File.WriteAllText(legacyPath, "{\"Password\":\"fixture-secret\",\"PasswordProtected\":\"ciphertext\"}");
    RemoteConfig.SanitizeLegacyFile(legacyPath);
    var clean = File.ReadAllText(legacyPath);
    Check(!clean.Contains("fixture-secret") && clean.Contains("ciphertext"), "legacy plaintext property migration retains encrypted credential");
} finally { File.Delete(legacyPath); }

string privateKeyPath = Path.GetTempFileName();
try {
    using var rsa = System.Security.Cryptography.RSA.Create(2048);
    File.WriteAllText(privateKeyPath, rsa.ExportRSAPrivateKeyPem());
    using var keyConnection = new SshConnection(new RemoteConfig {
        Host = "192.0.2.1", User = "fixture", Fingerprint = "SHA256:" + new string('A', 43),
        AuthMethod = "key", PrivateKeyPath = privateKeyPath
    });
    Check(!keyConnection.Client.IsConnected, "private-key authentication can load a key without connecting or requiring a password");
} finally { File.Delete(privateKeyPath); }

if (args.Contains("--local"))
{
    using var local = MonitorSource.Create(MonitorSource.Local, 500);
    await Until(() => local.IsLive);
    Check(!local.Latest.IsRemote && local.Latest.HostName == Environment.MachineName && local.Latest.MemTotalGB > 0,
        "local source reads this Windows PC without SSH");
    Check(local.Latest.Gpus.Select(g => g.Id).Distinct().Count() == local.Latest.Gpus.Count, "local GPUs have distinct identities");
    local.Dispose();
    Check(!local.IsLive, "disposed local source immediately stops claiming live status");
}

if (args.Contains("--live"))
{
    var actual = RemoteConfig.Load();
    string foundKey = await SshConnection.InspectHostAsync(actual, CancellationToken.None);
    Check(foundKey == actual.Fingerprint, "SSH detection retrieves the pinned host key without authentication");
    using var relay = new FaultRelay(actual.Host, actual.Port);
    var testConfig = new RemoteConfig
    {
        Host = "127.0.0.1", Port = relay.Port, User = actual.User,
        PasswordProtected = actual.PasswordProtected, Fingerprint = actual.Fingerprint,
        AuthMethod = actual.AuthMethod, PrivateKeyPath = actual.PrivateKeyPath, KeyPassphraseProtected = actual.KeyPassphraseProtected
    };
    using var sampler = new RemoteSampler(500, () => testConfig);
    await Until(() => sampler.IsLive && sampler.Latest.Tick >= 2);
    Check(sampler.Latest.HostName == sample.HostName, "SSH authenticated and receives real telemetry");
    long before = sampler.Latest.Tick;
    relay.Stall = true;
    await Until(() => !sampler.IsLive, 10);
    Check(sampler.Status == "数据已过期", "silent link stall marks displayed data stale");
    relay.Stall = false;
    await Until(() => sampler.IsLive && sampler.Latest.Tick > before);
    Check(true, "stream resumes after temporary stall");
    relay.Enabled = false;
    relay.DropConnections();
    await Until(() => !sampler.IsLive);
    before = sampler.Latest.Tick;
    await Task.Delay(1500);
    Check(!sampler.IsLive && sampler.Latest.Tick == before, "disconnected sampler does not invent samples or fall back to this PC");
    relay.Enabled = true;
    await Until(() => sampler.IsLive && sampler.Latest.Tick > before, 20);
    Check(true, "automatic SSH reconnection restores remote stream");
    sampler.IntervalMs = 2000;
    before = sampler.Latest.Tick;
    await Until(() => sampler.IsLive && sampler.Latest.Tick >= before + 3, 20);
    Check(sampler.IntervalMs == 2000, "sampling interval change restarts the collector");
    sampler.Dispose();
    Check(!sampler.IsLive, "disposed sampler immediately stops claiming live status");
    var wrong = new RemoteConfig
    {
        Host = actual.Host, Port = actual.Port, User = actual.User,
        PasswordProtected = actual.PasswordProtected, Fingerprint = "SHA256:" + new string('A', 43),
        AuthMethod = actual.AuthMethod, PrivateKeyPath = actual.PrivateKeyPath, KeyPassphraseProtected = actual.KeyPassphraseProtected
    };
    using var rejected = new RemoteSampler(1000, () => wrong);
    await Until(() => rejected.Error.Contains("指纹"));
    Check(!rejected.IsLive && rejected.Latest.Tick == 0, "mismatched SSH host key rejected");
}
Console.WriteLine($"{passed} checks passed.");

sealed class FaultRelay : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly System.Collections.Concurrent.ConcurrentBag<TcpClient> sockets = new();
    public volatile bool Enabled = true;
    public volatile bool Stall;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public FaultRelay(string host, int port)
    {
        listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var inbound = await listener.AcceptTcpClientAsync(stop.Token);
                    if (!Enabled) { inbound.Dispose(); continue; }
                    var outbound = new TcpClient(AddressFamily.InterNetworkV6);
                    sockets.Add(inbound); sockets.Add(outbound);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await outbound.ConnectAsync(host, port, stop.Token);
                            await Task.WhenAny(Pump(inbound.GetStream(), outbound.GetStream(), false),
                                               Pump(outbound.GetStream(), inbound.GetStream(), true));
                        }
                        catch { }
                        finally { inbound.Dispose(); outbound.Dispose(); }
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });
    }
    private async Task Pump(NetworkStream from, NetworkStream to, bool simulateStall)
    {
        var buffer = new byte[65536];
        while (!stop.IsCancellationRequested)
        {
            while (simulateStall && Stall) await Task.Delay(50, stop.Token);
            int n = await from.ReadAsync(buffer, stop.Token);
            if (n == 0) return;
            while (simulateStall && Stall) await Task.Delay(50, stop.Token);
            await to.WriteAsync(buffer.AsMemory(0, n), stop.Token);
        }
    }
    public void DropConnections()
    {
        while (sockets.TryTake(out var socket)) socket.Dispose();
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); DropConnections(); }
}

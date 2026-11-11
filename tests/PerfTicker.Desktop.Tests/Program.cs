using PerfTicker;
using System.Text.Json;

// Fixture-only checks: never start a sampler, GUI, nvidia-smi or SSH connection.
int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
foreach (int count in new[] { 1, 4, 8, 32 })
{
    var csv = string.Join('\n', Enumerable.Range(0, count).Select(i => $"{i}, GPU-test-{i}, NVIDIA test, 580, {10 + i}, 12, 1200, 2000, 2500, 1024, 4096, 60, 100, 300, [N/A]"));
    var cards = LocalReader.ParseNvidia(csv, out var driver, out var skipped);
    Check(cards.Count == count && !skipped && driver == "580", count + " cards parsed");
    var s = new Snapshot { Gpus = cards, CoreMHz = new double[4] }; s.NormalizeGpus();
    Check(s.Gpu.MemTotalMB == count * 4096 && s.Gpu.PowerW == count * 100, count + " card totals");
    var metrics = Catalog.Build(4, cards);
    Check(metrics.Count(m => m.GpuId.Length > 0 && m.Name.EndsWith("负载")) == count, count + " independent charts");
    var last = metrics.Single(m => m.GpuId == cards[^1].Id && m.Name.EndsWith("负载"));
    cards.Reverse(); Check(last.Read(s) == 9 + count, count + " UUID stable after reorder");
}
var bad = LocalReader.ParseNvidia("bad\n0, GPU-one, GPU, 580, 10, 2, N/A, 1000, 2000, N/A, 4000, 50, N/A, 300, N/A", out _, out bool partial);
Check(partial && bad.Count == 1 && double.IsNaN(bad[0].PowerW), "malformed card does not hide valid card or invent sensor");
var procs = new List<ProcSample> { new() { Pid = 7 }, new() { Pid = 8 }, new() { Pid = 9 } };
LocalReader.AttachGpuMemory("GPU-a, 7, 100\nGPU-b, 7, 200\nGPU-a, 8, N/A", procs);
Check(procs[0].GpuMemMB == 300 && procs[0].MemoryOnGpu("GPU-b") == 200, "multi-card process attribution");
Check(double.IsNaN(procs[1].GpuMemMB) && double.IsNaN(procs[2].GpuMemMB), "unknown process GPU memory stays unknown");
var cpu = LocalReader.LinuxCpuCounters("cpu 100 0 50 850 10 0 0 0 100 50");
Check(cpu.Total == 1010 && cpu.Idle == 860, "Linux guest counters not double counted");
string vm = "Mach Virtual Memory Statistics: (page size of 16384 bytes)\nPages free: 10.\nPages inactive: 20.\nPages speculative: 5.";
Check(LocalReader.MacMemoryUsed(vm, 1024 * 1024) == 1024 * 1024 - 35 * 16384, "Apple Silicon 16K memory pages");
Check(LocalReader.MacMemoryUsed(vm.Replace("16384", "4096"), 1024 * 1024) == 1024 * 1024 - 35 * 4096, "Intel Mac 4K memory pages");
var mac = LocalReader.ParseMacGpus("{\"SPDisplaysDataType\":[{\"sppci_model\":\"Apple M4\"},{\"_name\":\"AMD Radeon\"}]}");
Check(mac.Count == 2 && mac[0].Name == "Apple M4" && double.IsNaN(mac[0].MemTotalMB), "Mac unified memory not fabricated as VRAM");
var config = new RemoteConfig { Host = "example.invalid", Fingerprint = "SHA256:" + new string('a', 43) };
config.SetPassword("fixture-secret"); config.SetKeyPassphrase("fixture-key-secret");
string json = JsonSerializer.Serialize(config);
Check(!json.Contains("secret") && !json.Contains("Password") && !json.Contains("Passphrase"), "portable config never serializes credentials");
Check(config.Copy().Password == "fixture-secret", "credentials available only in session copy");
var discovered = SshDiscovery.Parse("Host test\n HostName 192.0.2.1\n User demo\n IdentityFile ~/.ssh/id_ed25519", "/home/demo", out _);
Check(discovered.Single().KeyPath == Path.Combine("/home/demo", ".ssh/id_ed25519"), "Unix SSH identity expands home");
var m = new Metric(); m.Push(20, 1000, 1); m.Push(30, 2000, 1); m.Push(double.NaN, 3000, 1); m.RebuildCandles(5000);
Check(m.Candles.Count == 1 && m.Candles[0].C == 30 && !m.Available, "missing sensor keeps history without fake zero candle");
Console.WriteLine($"{passed} fixture checks passed; no hardware sampling or network connections performed.");

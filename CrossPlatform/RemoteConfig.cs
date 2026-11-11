using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PerfTicker;

// Portable settings contain addresses and public host fingerprints only.
// Secrets live for this process; private keys remain at their original path.
internal sealed class RemoteConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = Environment.UserName;
    public string Fingerprint { get; set; } = "";
    public string AuthMethod { get; set; } = "password";
    public string PrivateKeyPath { get; set; } = "";
    [JsonIgnore] public string Password { get; private set; } = "";
    [JsonIgnore] public string KeyPassphrase { get; private set; } = "";
    public static string ConfigPath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "PerfTickerDesktop", "remote.json");
    public void SetPassword(string value) => Password = value ?? "";
    public void SetKeyPassphrase(string value) => KeyPassphrase = value ?? "";
    public RemoteConfig Copy() => (RemoteConfig)MemberwiseClone();
    public static RemoteConfig Load()
    {
        try { return File.Exists(ConfigPath) ? JsonSerializer.Deserialize<RemoteConfig>(File.ReadAllText(ConfigPath)) ?? new() : new(); }
        catch (Exception ex) { Diag.Write("CONFIG", ex.Message); return new(); }
    }
    public void ValidateAddress()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Any(char.IsWhiteSpace) || Host.Contains('/') || Port is < 1 or > 65535)
            throw new InvalidOperationException("请输入有效的主机/IP 和 SSH 端口（1–65535）。");
    }
    public void Validate()
    {
        ValidateAddress();
        if (string.IsNullOrWhiteSpace(User)) throw new InvalidOperationException("请填写 SSH 用户名。");
        if (!Regex.IsMatch(Fingerprint ?? "", @"^SHA256:[A-Za-z0-9+/]{43}$"))
            throw new InvalidOperationException("请检测并核实 SSH 主机指纹。");
        if (AuthMethod == "key" && !File.Exists(PrivateKeyPath)) throw new InvalidOperationException("找不到 SSH 私钥文件。");
        if (AuthMethod == "password" && Password.Length == 0) throw new InvalidOperationException("请填写 SSH 密码（仅在本次运行中保留）。");
        if (AuthMethod is not ("key" or "password")) throw new InvalidOperationException("不支持的认证方式。");
    }
    public void Save()
    {
        Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, ConfigPath, true);
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PerfTicker;

internal sealed class RemoteConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public string PasswordProtected { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int GpuIndex { get; set; }
    public static string ConfigPath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "PerfTickerRemote", "remote.json");

    public static RemoteConfig Load()
    {
        if (!File.Exists(ConfigPath)) return new();
        return JsonSerializer.Deserialize<RemoteConfig>(File.ReadAllText(ConfigPath)) ?? new();
    }

    public string Password => Encoding.UTF8.GetString(ProtectedData.Unprotect(
        Convert.FromBase64String(PasswordProtected), null, DataProtectionScope.CurrentUser));

    public void SetPassword(string password) => PasswordProtected = Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(User))
            throw new InvalidOperationException("请在远程连接中填写主机和用户名。");
        if (Port < 1 || Port > 65535 || GpuIndex < 0)
            throw new InvalidOperationException("SSH 端口或 GPU 编号不正确。");
        if (!Fingerprint.StartsWith("SHA256:") || Fingerprint.Length != 50)
            throw new InvalidOperationException("请填写已核实的 SSH 主机 SHA256 指纹。");
        if (string.IsNullOrEmpty(PasswordProtected))
            throw new InvalidOperationException("请在远程连接中填写登录密码。");
    }

    public void Save()
    {
        Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
        if (File.Exists(ConfigPath)) File.Copy(ConfigPath, ConfigPath + ".bak", true);
        File.WriteAllText(ConfigPath + ".tmp", JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(ConfigPath + ".tmp", ConfigPath, true);
    }
}

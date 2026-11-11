using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PerfTicker;

internal sealed class RemoteConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public string PasswordProtected { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string AuthMethod { get; set; } = "password";
    public string PrivateKeyPath { get; set; } = "";
    public string KeyPassphraseProtected { get; set; } = "";
    public static string ConfigPath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "PerfTickerRemote", "remote.json");

    public static RemoteConfig Load()
    {
        if (!File.Exists(ConfigPath)) return new();
        SanitizeLegacyFile(ConfigPath);
        if (File.Exists(ConfigPath + ".bak"))
        {
            try { SanitizeLegacyFile(ConfigPath + ".bak"); }
            catch (Exception ex) { Diag.Write("CONFIG", "旧备份无法整理，当前配置仍可使用：" + ex.Message); }
        }
        return JsonSerializer.Deserialize<RemoteConfig>(File.ReadAllText(ConfigPath)) ?? new();
    }

    internal static void SanitizeLegacyFile(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
        if (node == null) return;
        bool changed = node.Remove("Password");
        changed |= node.Remove("KeyPassphrase");
        if (!changed) return;
        File.WriteAllText(path + ".tmp", node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }

    private static string Unprotect(string value) => string.IsNullOrEmpty(value) ? "" :
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
    [JsonIgnore] public string Password => Unprotect(PasswordProtected);
    [JsonIgnore] public string KeyPassphrase => Unprotect(KeyPassphraseProtected);

    public void SetPassword(string password) => PasswordProtected = Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));
    public void SetKeyPassphrase(string password) => KeyPassphraseProtected = string.IsNullOrEmpty(password) ? "" : Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));

    public RemoteConfig Copy() => (RemoteConfig)MemberwiseClone();

    public void ValidateAddress()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Any(char.IsWhiteSpace) || Host.Contains('/') || Port < 1 || Port > 65535)
            throw new InvalidOperationException("请输入主机名或 IP 地址，以及 1–65535 范围的 SSH 端口。");
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(User))
            throw new InvalidOperationException("请在远程连接中填写主机和用户名。");
        ValidateAddress();
        if (!System.Text.RegularExpressions.Regex.IsMatch(Fingerprint ?? "", @"^SHA256:[A-Za-z0-9+/]{43}$"))
            throw new InvalidOperationException("请填写已核实的 SSH 主机 SHA256 指纹。");
        if (AuthMethod == "key" && !File.Exists(PrivateKeyPath))
            throw new InvalidOperationException("请选择此电脑上的 SSH 私钥文件。");
        if (AuthMethod != "key" && AuthMethod != "password") throw new InvalidOperationException("不支持的认证方式。");
        if (AuthMethod == "password" && string.IsNullOrEmpty(PasswordProtected))
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

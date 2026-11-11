using System.Security.Cryptography;
using Renci.SshNet;

namespace PerfTicker;

internal sealed class SshConnection : IDisposable
{
    private readonly PrivateKeyFile _key;
    private readonly AuthenticationMethod _auth;
    public SshClient Client { get; }
    public bool KeyMismatch { get; private set; }
    public static string Fingerprint(byte[] key) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');

    public SshConnection(RemoteConfig config)
    {
        config.Validate();
        try
        {
            if (config.AuthMethod == "key")
            {
                _key = new PrivateKeyFile(config.PrivateKeyPath, config.KeyPassphrase);
                _auth = new PrivateKeyAuthenticationMethod(config.User, _key);
            }
            else _auth = new PasswordAuthenticationMethod(config.User, config.Password);
            Client = new SshClient(new ConnectionInfo(config.Host, config.Port, config.User, _auth)
                { Timeout = TimeSpan.FromSeconds(8) });
            Client.KeepAliveInterval = TimeSpan.FromSeconds(5);
            Client.HostKeyReceived += (_, e) => {
                e.CanTrust = string.Equals(Fingerprint(e.HostKey), config.Fingerprint, StringComparison.Ordinal);
                KeyMismatch = !e.CanTrust;
            };
        }
        catch { _auth?.Dispose(); _key?.Dispose(); throw; }
    }

    public void Connect()
    {
        try { Client.Connect(); }
        catch when (KeyMismatch) { throw new InvalidOperationException("SSH 主机指纹不匹配，已拒绝连接。请核实服务器身份。"); }
    }

    // Only exchange host keys. Always reject before user authentication; never sends credentials.
    public static Task<string> InspectHostAsync(RemoteConfig config, CancellationToken token) => Task.Run(() => {
        config.ValidateAddress();
        var user = string.IsNullOrWhiteSpace(config.User) ? "probe" : config.User;
        using var client = new SshClient(new ConnectionInfo(config.Host, config.Port, user,
            new NoneAuthenticationMethod(user)) { Timeout = TimeSpan.FromSeconds(8) });
        string found = null;
        client.HostKeyReceived += (_, e) => { found = Fingerprint(e.HostKey); e.CanTrust = false; };
        using var cancel = token.Register(() => { try { client.Dispose(); } catch { } });
        try { client.Connect(); }
        catch when (found != null && !token.IsCancellationRequested) { }
        token.ThrowIfCancellationRequested();
        return found ?? throw new IOException("未能获取 SSH 主机指纹。请检查 IP、端口、VPN 和防火墙。");
    }, token);

    public void Dispose() { Client?.Dispose(); _auth?.Dispose(); _key?.Dispose(); }
}

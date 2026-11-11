using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace PerfTicker;

internal sealed class ConnectionWindow : Window
{
    private readonly TextBox _host = new(), _port = new() { Text = "22" }, _user = new(), _password = new() { PasswordChar = '●' },
        _key = new(), _passphrase = new() { PasswordChar = '●' }, _fingerprint = new() { IsReadOnly = true };
    private readonly ComboBox _auth = new() { ItemsSource = new[] { "密码", "SSH 私钥" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _trust = new() { Content = "我已核实这是目标服务器的主机指纹" };
    private readonly TextBlock _message = MarketStyle.Label("", 12, MarketStyle.Gold);
    private readonly CancellationTokenSource _stop = new();
    private readonly Button _detect = new() { Content = "检测 SSH 指纹" }, _connect = new() { Content = "测试并连接", MinWidth = 140 };
    private bool _busy;
    public ConnectionWindow(RemoteConfig previous)
    {
        Title = "连接 Linux 服务器 · PerfTicker"; Width = 660; Height = 760; MinWidth = 570; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = MarketStyle.Background; Foreground = MarketStyle.Text;
        FontFamily = new FontFamily("Microsoft YaHei, PingFang SC, Noto Sans CJK SC, sans-serif");
        _host.Text = previous.Host; _port.Text = previous.Port.ToString(); _user.Text = previous.User;
        _password.Text = previous.Password; _key.Text = previous.PrivateKeyPath; _passphrase.Text = previous.KeyPassphrase;
        _fingerprint.Text = previous.Fingerprint; _auth.SelectedIndex = previous.AuthMethod == "key" ? 1 : 0;
        _trust.IsChecked = false;
        _host.TextChanged += (_, _) => EndpointChanged(); _port.TextChanged += (_, _) => EndpointChanged();
        _user.TextChanged += (_, _) => { _password.Text = ""; _passphrase.Text = ""; };
        _key.TextChanged += (_, _) => _passphrase.Text = "";
        var profiles = new ComboBox { ItemsSource = SshDiscovery.Read(out var note), PlaceholderText = "从此电脑的 SSH config 选择", HorizontalAlignment = HorizontalAlignment.Stretch };
        profiles.SelectionChanged += (_, _) => {
            if (profiles.SelectedItem is not SshProfile p) return;
            _host.Text = p.Host; _port.Text = p.Port.ToString(); _user.Text = p.User; _key.Text = p.KeyPath;
            _auth.SelectedIndex = p.KeyPath.Length > 0 ? 1 : 0;
            _message.Text = p.NeedsProxy ? "此配置使用跳板机；当前仅支持直接 SSH，请填写可直达地址。" : "已导入地址，请检测并核实指纹。";
        };
        var browse = new Button { Content = "选择…" };
        browse.Click += async (_, _) => {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择 SSH 私钥", AllowMultiple = false });
            if (files.Count > 0) _key.Text = files[0].TryGetLocalPath() ?? "";
        };
        _detect.Click += async (_, _) => await Detect();
        _connect.Click += async (_, _) => await Connect();
        _message.TextWrapping = TextWrapping.Wrap;
        var tip = MarketStyle.Label(note + "\n支持 Linux 服务器上的 Python 3；NVIDIA 指标需要 nvidia-smi。\n密码、私钥口令仅在本次运行中保留；不会保存明文密码。", 12, MarketStyle.Muted); tip.TextWrapping = TextWrapping.Wrap;
        var cancel = new Button { Content = "取消" }; cancel.Click += (_, _) => Close();
        var stack = new StackPanel { Margin = new Thickness(24), Spacing = 10, Children = {
            MarketStyle.Label("选择监控服务器", 22, MarketStyle.Gold), tip, profiles,
            Field("主机名 / IP（支持 IPv4、IPv6）", _host),
            MarketStyle.Row("*,12,*", Field("SSH 端口", _port), new Border(), Field("用户名", _user)),
            Field("认证方式", _auth), Field("登录密码", _password),
            Field("私钥文件", MarketStyle.Row("*,Auto", _key, browse)), Field("私钥口令（如有）", _passphrase),
            _detect, Field("服务器 SHA256 指纹", _fingerprint), _trust, _message,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { cancel, _connect } }
        } };
        void AuthChanged() { _password.IsEnabled = _auth.SelectedIndex == 0; _key.IsEnabled = _passphrase.IsEnabled = browse.IsEnabled = _auth.SelectedIndex == 1; }
        _auth.SelectionChanged += (_, _) => AuthChanged(); AuthChanged();
        Content = new ScrollViewer { Content = stack };
        Closed += (_, _) => _stop.Cancel();
    }
    private static StackPanel Field(string label, Control input) => new() { Spacing = 5, Children = { MarketStyle.Label(label, 12, MarketStyle.Muted), input } };
    private void EndpointChanged() { _fingerprint.Text = ""; _trust.IsChecked = false; _password.Text = ""; _passphrase.Text = ""; }
    private RemoteConfig Read()
    {
        if (!int.TryParse(_port.Text, out int port)) throw new InvalidOperationException("SSH 端口应为数字。");
        var c = new RemoteConfig { Host = (_host.Text ?? "").Trim().Trim('[', ']'), Port = port, User = (_user.Text ?? "").Trim(),
            AuthMethod = _auth.SelectedIndex == 1 ? "key" : "password", PrivateKeyPath = (_key.Text ?? "").Trim(), Fingerprint = _fingerprint.Text ?? "" };
        if (c.PrivateKeyPath.StartsWith("~/")) c.PrivateKeyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), c.PrivateKeyPath[2..]);
        c.SetPassword(_password.Text); c.SetKeyPassphrase(_passphrase.Text); return c;
    }
    private void Busy(bool value) { _busy = value; _detect.IsEnabled = _connect.IsEnabled = !value; }
    private async Task Detect()
    {
        if (_busy) return;
        try
        {
            var c = Read(); c.ValidateAddress(); Busy(true); _message.Text = "正在检测 SSH 服务…";
            var fp = await SshConnection.InspectHostAsync(c, _stop.Token);
            if (_stop.IsCancellationRequested) return;
            var current = Read();
            if (current.Host != c.Host || current.Port != c.Port) throw new InvalidOperationException("地址已改变，请重新检测。");
            _fingerprint.Text = fp; _trust.IsChecked = false; _message.Text = "检测成功。请与服务器上的 SSH 主机指纹核对后勾选确认。";
        }
        catch (Exception ex) { if (!_stop.IsCancellationRequested) _message.Text = ex.Message; }
        finally { Busy(false); }
    }
    private async Task Connect()
    {
        if (_busy) return;
        try
        {
            if (_trust.IsChecked != true) throw new InvalidOperationException("请先核实并确认 SSH 主机指纹。");
            var c = Read(); c.Validate(); Busy(true); _message.Text = "正在登录并读取首条行情…";
            using var test = new RemoteSampler(1000, () => c);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!test.IsLive && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200, _stop.Token);
                if (!test.IsLive && test.Error.Length > 0) throw new IOException(test.Error);
            }
            if (!test.IsLive) throw new TimeoutException("未收到行情。请检查服务器是否安装 Python 3，以及 SSH 是否允许执行命令。");
            // Save the exact endpoint that was tested, not any fields edited during the operation.
            c.Save(); Close(c);
        }
        catch (Exception ex) { if (!_stop.IsCancellationRequested) _message.Text = ex.Message; }
        finally { Busy(false); }
    }
}

namespace PerfTicker;

internal sealed class ConnectionForm : Form
{
    private readonly CancellationTokenSource _stop = new();
    private readonly RemoteConfig _saved;
    private readonly ComboBox _mode = new(), _profiles = new(), _auth = new();
    private readonly TextBox _host = new(), _port = new(), _user = new(), _password = new(), _key = new(), _phrase = new(), _fingerprint = new();
    private readonly CheckBox _trust = new();
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Panel _remote = new();
    private readonly Button _save = new(), _inspect = new(), _test = new(), _refresh = new(), _browse = new();
    public string SelectedMode => _mode.SelectedIndex == 0 ? MonitorSource.Local : MonitorSource.Remote;

    public ConnectionForm(string mode = MonitorSource.Remote)
    {
        SuspendLayout();
        Text = "监控目标 · 本机 / SSH 服务器";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(730, 640);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9);
        try { _saved = RemoteConfig.Load(); }
        catch (Exception ex) { _saved = new(); _status.Text = "旧连接配置无法读取：" + ex.Message; }
        Controls.Add(new Label { Text = "监控目标", Left = 20, Top = 23, Width = 115 });
        _mode.SetBounds(145, 18, 560, 30);
        _mode.DropDownStyle = ComboBoxStyle.DropDownList;
        _mode.Items.AddRange(new object[] { "这台电脑（Windows 本机）", "SSH 服务器（Linux）" });
        _mode.SelectedIndex = mode == MonitorSource.Local ? 0 : 1;
        Controls.Add(_mode);
        _remote.SetBounds(0, 60, 730, 474);
        Controls.Add(_remote);

        void Field(string label, Control control, int y, string value = "")
        {
            _remote.Controls.Add(new Label { Text = label, Left = 20, Top = y + 4, Width = 120, Height = 25 });
            control.SetBounds(145, y, 560, 28); control.Text = value; _remote.Controls.Add(control);
        }
        Field("已配置 SSH 主机", _profiles, 0);
        _profiles.Width = 440; _profiles.DropDownStyle = ComboBoxStyle.DropDownList;
        _refresh.Text = "重新识别"; _refresh.SetBounds(595, 0, 110, 28); _remote.Controls.Add(_refresh);
        Field("主机名 / IPv4 / IPv6", _host, 38, _saved.Host);
        Field("SSH 端口", _port, 76, _saved.Port.ToString());
        Field("用户名", _user, 114, _saved.User);
        Field("认证方式", _auth, 152);
        _auth.DropDownStyle = ComboBoxStyle.DropDownList;
        _auth.Items.AddRange(new object[] { "密码", "SSH 私钥文件" });
        _auth.SelectedIndex = _saved.AuthMethod == "key" ? 1 : 0;
        Field("密码（留空保留）", _password, 190); _password.UseSystemPasswordChar = true;
        Field("此电脑的私钥", _key, 228, _saved.PrivateKeyPath); _key.Width = 440;
        _browse.Text = "选择文件"; _browse.SetBounds(595, 228, 110, 28); _remote.Controls.Add(_browse);
        Field("私钥口令（可空）", _phrase, 266); _phrase.UseSystemPasswordChar = true;
        Field("主机 SHA256 指纹", _fingerprint, 304, _saved.Fingerprint);
        _trust.Text = "我已核实此地址的主机指纹（可向服务器管理员核对）";
        _trust.SetBounds(145, 340, 560, 28); _remote.Controls.Add(_trust);
        _trust.Checked = _saved.Fingerprint.Length > 0;
        _inspect.Text = "检测 SSH / 读取指纹"; _inspect.SetBounds(145, 380, 210, 34); _remote.Controls.Add(_inspect);
        _test.Text = "测试登录与显卡"; _test.SetBounds(375, 380, 210, 34); _remote.Controls.Add(_test);
        _remote.Controls.Add(new Label { Text = "先检测 SSH，再核对指纹并测试登录。将自动采集全部 NVIDIA 显卡。",
            Left = 20, Top = 430, Width = 685, Height = 35 });
        _status.SetBounds(20, 540, 685, 50); Controls.Add(_status);
        _save.Text = "保存并切换"; _save.SetBounds(565, 596, 140, 32); Controls.Add(_save);
        var cancel = new Button { Text = "取消", Left = 450, Top = 596, Width = 100, Height = 32, DialogResult = DialogResult.Cancel };
        Controls.Add(cancel); CancelButton = cancel; AcceptButton = _save;
        _mode.SelectedIndexChanged += (_, _) => UpdateMode();
        _auth.SelectedIndexChanged += (_, _) => UpdateAuth();
        foreach (var field in new[] { _host, _port })
            field.TextChanged += (_, _) => {
                _fingerprint.Text = SameSavedEndpoint() ? _saved.Fingerprint : "";
                _trust.Checked = _saved.Fingerprint.Length > 0 && SameSavedEndpoint();
                _password.Clear(); _phrase.Clear();
            };
        _fingerprint.TextChanged += (_, _) => _trust.Checked = _saved.Fingerprint.Length > 0
            && SameSavedEndpoint() && _fingerprint.Text.Trim() == _saved.Fingerprint;
        _user.TextChanged += (_, _) => { _password.Clear(); _phrase.Clear(); };
        _key.TextChanged += (_, _) => _phrase.Clear();
        _refresh.Click += (_, _) => Discover();
        _profiles.SelectedIndexChanged += (_, _) => ImportProfile();
        _browse.Click += (_, _) => { using var picker = new OpenFileDialog { Title = "选择 SSH 私钥（不是 .pub 公钥）", CheckFileExists = true };
            if (picker.ShowDialog(this) == DialogResult.OK) _key.Text = picker.FileName; };
        _inspect.Click += async (_, _) => await RunOperation(async () => {
            var config = ReadFields(false);
            string found = await SshConnection.InspectHostAsync(config, _stop.Token);
            if (_fingerprint.Text.Length > 0 && _fingerprint.Text.Trim() != found)
            {
                _trust.Checked = false;
                throw new InvalidOperationException("检测到不同指纹，未覆盖已存指纹。实际为 " + found + "；请核实后手动更新。");
            }
            _fingerprint.Text = found;
            _status.Text = "SSH 可达。请核实指纹，勾选后测试登录；仅端口可达不代表账号或采集环境可用。";
        });
        _test.Click += async (_, _) => await RunOperation(async () => {
            var config = ReadFields(true);
            using var sampler = new RemoteSampler(1000, () => config);
            var until = DateTime.UtcNow.AddSeconds(25);
            while (!sampler.IsLive && DateTime.UtcNow < until)
            {
                await Task.Delay(150, _stop.Token);
                if (sampler.Status.StartsWith("断线")) throw new IOException(sampler.Error);
            }
            if (!sampler.IsLive) throw new IOException("采集超时。请检查远端 Python 3、SSH 权限和网络。" + sampler.Error);
            var s = sampler.Latest;
            _status.Text = $"连接成功：{s.HostName} · {s.CoreMHz.Length} 线程 · 检测到 {s.Gpus.Count} 张 NVIDIA GPU。" + sampler.Error;
        });
        _save.Click += (_, _) => {
            try {
                if (SelectedMode == MonitorSource.Remote) ReadFields(true).Save();
                DialogResult = DialogResult.OK; Close();
            } catch (Exception ex) { _status.Text = ex.Message; }
        };
        Discover(); UpdateMode();
        ResumeLayout(false);
        PerformLayout();
    }

    private bool SameSavedEndpoint() => _host.Text.Trim().Trim('[', ']') == _saved.Host
        && int.TryParse(_port.Text, out var port) && port == _saved.Port;
    private RemoteConfig ReadFields(bool validate)
    {
        var c = _saved.Copy();
        c.Host = _host.Text.Trim().Trim('[', ']'); c.Port = int.TryParse(_port.Text, out var port) ? port : 0;
        c.User = _user.Text.Trim(); c.Fingerprint = _fingerprint.Text.Trim();
        c.AuthMethod = _auth.SelectedIndex == 1 ? "key" : "password"; c.PrivateKeyPath = _key.Text.Trim();
        // Never silently reuse another server/account's secret.
        if (!SameSavedEndpoint() || c.User != _saved.User) { c.PasswordProtected = ""; c.KeyPassphraseProtected = ""; }
        if (c.PrivateKeyPath != _saved.PrivateKeyPath) c.KeyPassphraseProtected = "";
        if (_password.Text.Length > 0) c.SetPassword(_password.Text);
        if (_phrase.Text.Length > 0) c.SetKeyPassphrase(_phrase.Text);
        c.ValidateAddress();
        if (validate) {
            if (!_trust.Checked) throw new InvalidOperationException("请先检测 SSH 并核实主机指纹。");
            c.Validate();
        }
        return c;
    }
    private void Discover()
    {
        var profiles = SshDiscovery.Read(out var note);
        _profiles.Items.Clear(); _profiles.Items.Add("手动输入 / 当前连接");
        foreach (var profile in profiles) _profiles.Items.Add(profile);
        _profiles.SelectedIndex = 0; _status.Text = note;
    }
    private void ImportProfile()
    {
        if (_profiles.SelectedItem is not SshProfile profile) return;
        _host.Text = profile.Host; _port.Text = profile.Port.ToString(); _user.Text = profile.User;
        _key.Text = profile.KeyPath; _auth.SelectedIndex = profile.KeyPath.Length > 0 ? 1 : 0;
        _password.Clear(); _phrase.Clear();
        _fingerprint.Text = SameSavedEndpoint() ? _saved.Fingerprint : "";
        _status.Text = profile.NeedsProxy ? "此条目使用跳板机，本版不执行 ProxyJump/ProxyCommand。请填直连地址，或已建 SSH 隧道的本地端口。"
            : "已填入 SSH 配置。密码和私钥口令不会从终端读取，请检测连接。";
    }
    private void UpdateMode()
    {
        _remote.Enabled = SelectedMode == MonitorSource.Remote; UpdateAuth();
        if (SelectedMode == MonitorSource.Local) _status.Text = "监控当前 Windows 电脑，不需要 SSH。服务器断线时不会自动切换到本机。";
    }
    private void UpdateAuth()
    {
        bool key = _auth.SelectedIndex == 1;
        _password.Enabled = !key; _key.Enabled = _phrase.Enabled = _browse.Enabled = key;
    }
    private async Task RunOperation(Func<Task> operation)
    {
        _remote.Enabled = _mode.Enabled = _save.Enabled = false; _status.Text = "正在检测，请稍候…";
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) _status.Text = ex.Message; }
        finally { if (!IsDisposed) { _mode.Enabled = _save.Enabled = true; UpdateMode(); } }
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _stop.Cancel(); base.OnFormClosed(e);
    }
}

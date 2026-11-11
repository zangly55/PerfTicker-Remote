namespace PerfTicker;

internal sealed class ConnectionForm : Form
{
    public ConnectionForm()
    {
        Text = "远程算力机连接";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(600, 340);
        MinimumSize = new Size(600, 380);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Font = new Font("Microsoft YaHei UI", 10);
        var c = RemoteConfig.Load();
        TextBox Field(string label, string value, int y, bool password = false)
        {
            Controls.Add(new Label { Text = label, Left = 20, Top = y + 3, Width = 130 });
            var box = new TextBox { Text = value, Left = 154, Top = y, Width = 420,
                UseSystemPasswordChar = password };
            Controls.Add(box);
            return box;
        }
        var host = Field("主机 / IPv6 地址", c.Host, 22);
        var port = Field("SSH 端口", c.Port.ToString(), 62);
        var user = Field("用户名", c.User, 102);
        var pass = Field("密码（留空保留）", "", 142, true);
        var fingerprint = Field("主机 SHA256 指纹", c.Fingerprint, 182);
        Controls.Add(new Label { Text = "密码仅在当前 Windows 账户下加密保存。连接后自动加载行情。",
            Left = 20, Top = 228, Width = 555, Height = 30 });
        var save = new Button { Text = "保存并连接", Left = 430, Top = 280, Width = 145, Height = 36 };
        Controls.Add(save);
        AcceptButton = save;
        save.Click += (_, _) =>
        {
            try
            {
                c.Host = host.Text.Trim().Trim('[', ']');
                c.Port = int.Parse(port.Text);
                c.User = user.Text.Trim();
                c.Fingerprint = fingerprint.Text.Trim();
                if (pass.Text.Length > 0) c.SetPassword(pass.Text);
                c.Save();
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "连接设置", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
    }
}

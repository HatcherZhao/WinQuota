namespace WinQuota.Tray;

/// <summary>管理员 PIN 输入对话框。</summary>
internal sealed class PinDialog : Form
{
    public string Pin { get; private set; } = string.Empty;

    public PinDialog()
    {
        Text = "管理员 PIN";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(320, 120);

        var label = new Label
        {
            Text = "请输入管理员 PIN：",
            Location = new Point(12, 15),
            AutoSize = true,
        };
        var box = new TextBox
        {
            Location = new Point(12, 40),
            Width = 290,
            UseSystemPasswordChar = true,
        };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(146, 80), Width = 75 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(227, 80), Width = 75 };

        Controls.AddRange([label, box, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;

        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text))
            {
                MessageBox.Show("PIN 不能为空。", "WinQuota", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            Pin = box.Text;
        };
    }
}

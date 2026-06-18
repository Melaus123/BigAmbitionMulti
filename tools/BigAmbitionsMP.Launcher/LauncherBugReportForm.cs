namespace BigAmbitionsMP.Launcher;

internal sealed class LauncherBugReportForm : Form
{
    private readonly TextBox _description = new();
    private readonly ListBox _attachments = new();

    public LauncherBugReportForm()
    {
        Text = "Report launcher bug";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(560, 440);
        MinimumSize = new Size(520, 400);
        BackColor = Color.FromArgb(14, 22, 34);
        ForeColor = Color.White;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 5,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Describe the launcher bug",
            Font = new Font(Font.FontFamily, 13, FontStyle.Bold),
            ForeColor = Color.FromArgb(245, 248, 252),
            Margin = new Padding(0, 0, 0, 8),
        });

        _description.Multiline = true;
        _description.ScrollBars = ScrollBars.Vertical;
        _description.Dock = DockStyle.Fill;
        _description.BorderStyle = BorderStyle.FixedSingle;
        _description.BackColor = Color.FromArgb(7, 12, 20);
        _description.ForeColor = Color.White;
        _description.Font = new Font("Segoe UI", 10);
        root.Controls.Add(_description);

        var attachRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 12, 0, 6),
        };
        root.Controls.Add(attachRow);

        var attachButton = new RoundedButton { Text = "Attach files", MinimumSize = new Size(124, 36), BackColor = Color.FromArgb(54, 86, 126), ForeColor = Color.White };
        attachButton.Click += (_, _) => AttachFiles();
        attachRow.Controls.Add(attachButton);

        var removeButton = new RoundedButton { Text = "Remove selected", MinimumSize = new Size(140, 36), BackColor = Color.FromArgb(64, 74, 88), ForeColor = Color.White };
        removeButton.Click += (_, _) => RemoveSelected();
        attachRow.Controls.Add(removeButton);

        _attachments.Dock = DockStyle.Fill;
        _attachments.BackColor = Color.FromArgb(7, 12, 20);
        _attachments.ForeColor = Color.FromArgb(220, 230, 242);
        _attachments.BorderStyle = BorderStyle.FixedSingle;
        root.Controls.Add(_attachments);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Margin = new Padding(0, 14, 0, 0),
        };
        root.Controls.Add(buttons);

        var send = new RoundedButton { Text = "Send report", MinimumSize = new Size(120, 38), BackColor = Color.FromArgb(54, 110, 164), ForeColor = Color.White, DialogResult = DialogResult.OK };
        var cancel = new RoundedButton { Text = "Cancel", MinimumSize = new Size(96, 38), BackColor = Color.FromArgb(92, 52, 58), ForeColor = Color.White, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(send);
        buttons.Controls.Add(cancel);

        AcceptButton = send;
        CancelButton = cancel;
    }

    public string Description => _description.Text;

    public string[] Attachments => _attachments.Items.Cast<string>().ToArray();

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK && string.IsNullOrWhiteSpace(_description.Text))
        {
            MessageBox.Show(this, "Describe the issue before sending the report.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }

    private void AttachFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Attach files to the BAMP Manager bug report",
            Multiselect = true,
            CheckFileExists = true,
            Filter = "Useful report files|*.txt;*.log;*.json;*.png;*.jpg;*.jpeg;*.webp;*.mp4;*.zip|All files|*.*",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        foreach (string file in dialog.FileNames)
        {
            if (!_attachments.Items.Contains(file))
                _attachments.Items.Add(file);
        }
    }

    private void RemoveSelected()
    {
        var selected = _attachments.SelectedItems.Cast<object>().ToArray();
        foreach (var item in selected)
            _attachments.Items.Remove(item);
    }
}

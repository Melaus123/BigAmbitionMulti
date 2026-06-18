namespace BigAmbitionsMP.Launcher;

internal sealed class MainForm : Form
{
    private readonly LauncherSettings _settings;
    private readonly ModManager _manager;
    private readonly GitHubReleaseClient _releases;
    private readonly LauncherBugReporter _bugReporter;
    private readonly Image? _backgroundImage;
    private readonly Image? _bugReportIcon;
    private Bitmap? _backgroundCache;
    private Size _backgroundCacheSize;
    private bool _isLiveResizing;

    private readonly Label _statusLabel = new();
    private readonly Label _installedLabel = new();
    private readonly Label _latestLabel = new();
    private readonly Label _missingLabel = new();
    private readonly RoundedProgressBar _progress = new();
    private readonly TextBox _log = new();
    private readonly Button _refreshButton = new RoundedButton();
    private readonly Button _checkButton = new RoundedButton();
    private readonly Button _installButton = new RoundedButton();
    private readonly Button _repairButton = new RoundedButton();
    private readonly Button _uninstallButton = new RoundedButton();
    private readonly Button _modFolderButton = new RoundedButton();
    private readonly Button _logsButton = new RoundedButton();
    private readonly Button _launchButton = new RoundedButton();
    private readonly Button _bugReportButton = new ImageIconButton();

    private ReleaseInfo? _latestRelease;
    private ModStatus? _currentStatus;
    private bool _busy;

    public MainForm(LauncherSettings settings, ModManager manager, GitHubReleaseClient releases, LauncherBugReporter bugReporter)
    {
        _settings = settings;
        _manager = manager;
        _releases = releases;
        _bugReporter = bugReporter;
        _backgroundImage = LoadAssetImage(settings.LauncherBackgroundImage);
        _bugReportIcon = LoadAssetImage(settings.LauncherBugReportIcon, new Size(34, 34));

        Text = settings.AppTitle;
        MinimumSize = new Size(820, 560);
        Size = new Size(900, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(12, 20, 31);
        ForeColor = Color.White;
        DoubleBuffered = true;

        BuildLayout();
        ConfigureBugReportButton();
        RefreshStatus();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(30, 26, 30, 24),
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Color.Transparent,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Height = 64,
            ColumnCount = 3,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        root.Controls.Add(header);

        var title = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = _settings.AppTitle,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, 26, FontStyle.Bold),
            ForeColor = Color.FromArgb(245, 248, 252),
            BackColor = Color.Transparent,
        };
        header.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent }, 0, 0);
        header.Controls.Add(title, 1, 0);

        header.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent }, 2, 0);

        var subtitle = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 32,
            Text = _settings.Disclaimer,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(172, 187, 205),
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 18),
        };
        root.Controls.Add(subtitle);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 0),
            BackColor = Color.Transparent,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        root.Controls.Add(body);

        var statusPanel = CreatePanel();
        statusPanel.Margin = new Padding(0, 0, 8, 0);
        statusPanel.Controls.Add(Stack(
            CreateSectionLabel("Status"),
            _statusLabel,
            _installedLabel,
            _latestLabel,
            _missingLabel,
            _progress));
        body.Controls.Add(statusPanel, 0, 0);

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BorderStyle = BorderStyle.None;
        _log.BackColor = Color.FromArgb(7, 12, 20);
        _log.ForeColor = Color.FromArgb(220, 230, 242);
        _log.Font = new Font("Consolas", 10);
        _log.Dock = DockStyle.Fill;
        var logPanel = CreatePanel();
        logPanel.Padding = new Padding(14);
        logPanel.Margin = new Padding(8, 0, 0, 0);
        logPanel.Controls.Add(_log);
        body.Controls.Add(logPanel, 1, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 18, 0, 0),
            BackColor = Color.Transparent,
        };
        root.Controls.Add(buttons);

        ConfigureButton(_refreshButton, "Refresh", (_, _) => RefreshStatus());
        ConfigureButton(_checkButton, "Check updates", async (_, _) => await RunOperationAsync(CheckUpdatesAsync));
        ConfigureButton(_installButton, "Install / Update", async (_, _) => await RunOperationAsync(InstallOrUpdateAsync));
        ConfigureButton(_repairButton, "Repair", async (_, _) => await RunOperationAsync(RepairAsync));
        ConfigureButton(_uninstallButton, "Uninstall", async (_, _) => await RunOperationAsync(UninstallAsync));
        ConfigureButton(_modFolderButton, "Open mod folder", (_, _) => _manager.OpenModFolder());
        ConfigureButton(_logsButton, "Open logs", (_, _) => _manager.OpenLogsFolder());
        ConfigureButton(_launchButton, "Launch game", (_, _) => LaunchGame());

        buttons.Controls.AddRange(new Control[]
        {
            _refreshButton,
            _checkButton,
            _installButton,
            _repairButton,
            _uninstallButton,
            _modFolderButton,
            _logsButton,
            _launchButton,
        });

        StyleStatusLabels();
    }

    private static RoundedPanel CreatePanel()
    {
        return new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            BackColor = Color.FromArgb(216, 22, 32, 48),
            CornerRadius = 16,
            BorderColor = Color.FromArgb(46, 67, 94),
        };
    }

    private static FlowLayoutPanel Stack(params Control[] controls)
    {
        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Color.Transparent,
        };
        foreach (var control in controls)
        {
            control.Margin = new Padding(0, 0, 0, 12);
            stack.Controls.Add(control);
        }
        return stack;
    }

    private Label CreateSectionLabel(string text)
        => new()
        {
            AutoSize = true,
            Text = text,
            Font = new Font(Font.FontFamily, 14, FontStyle.Bold),
            ForeColor = Color.FromArgb(245, 248, 252),
            BackColor = Color.Transparent,
        };

    private void StyleStatusLabels()
    {
        foreach (var label in new[] { _statusLabel, _installedLabel, _latestLabel, _missingLabel })
        {
            label.AutoSize = false;
            label.Width = 300;
            label.Height = 58;
            label.ForeColor = Color.FromArgb(215, 226, 240);
        }

        _progress.Width = 300;
        _progress.Height = 18;
        _progress.Value = 0;
    }

    private void ConfigureButton(Button button, string text, EventHandler onClick)
    {
        button.Text = text;
        button.AutoSize = true;
        button.MinimumSize = new Size(124, 40);
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Color.FromArgb(54, 86, 126);
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderColor = Color.FromArgb(103, 135, 174);
        button.Margin = new Padding(0, 0, 10, 10);
        button.Click += onClick;
    }

    private void ConfigureBugReportButton()
    {
        _bugReportButton.Size = new Size(48, 48);
        _bugReportButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _bugReportButton.TabStop = false;
        _bugReportButton.Text = "";
        _bugReportButton.FlatStyle = FlatStyle.Flat;
        _bugReportButton.FlatAppearance.BorderSize = 0;
        _bugReportButton.BackColor = Color.Transparent;
        if (_bugReportButton is ImageIconButton iconButton)
            iconButton.IconImage = _bugReportIcon;

        _bugReportButton.Click += async (_, _) => await OpenLauncherBugReportAsync();
        new ToolTip().SetToolTip(_bugReportButton, "Report a BAMP Manager bug");
        Controls.Add(_bugReportButton);
        PositionBugReportButton();
        _bugReportButton.BringToFront();
    }

    private async Task RunOperationAsync(Func<IProgress<OperationProgress>, CancellationToken, Task> operation)
    {
        if (_busy) return;
        _busy = true;
        SetButtonsEnabled(false);

        using var cts = new CancellationTokenSource();
        var progress = new Progress<OperationProgress>(UpdateProgress);

        try
        {
            await operation(progress, cts.Token);
            RefreshStatus();
        }
        catch (Exception ex)
        {
            AppendLog("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, _settings.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            UpdateButtonStates();
        }
    }

    private async Task CheckUpdatesAsync(IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new OperationProgress("Checking GitHub version...", 10));
        _latestRelease = await _releases.GetLatestReleaseAsync(cancellationToken);
        AppendLog($"Latest version: {_latestRelease.DisplayName}");
        if (_latestRelease.HasInstallableZip)
            AppendLog($"Installable package: {_latestRelease.ZipAssetName}");
        else
            AppendLog("No installable release zip was found. A GitHub release asset is required for install/update.");
        progress.Report(new OperationProgress("Update check complete.", 100));
    }

    private async Task InstallOrUpdateAsync(IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var release = await EnsureLatestReleaseAsync(progress, cancellationToken);
        await _manager.InstallOrUpdateAsync(release, _releases, progress, cancellationToken);
    }

    private async Task RepairAsync(IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var release = await EnsureLatestReleaseAsync(progress, cancellationToken);
        await _manager.RepairAsync(release, _releases, progress, cancellationToken);
    }

    private async Task UninstallAsync(IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var answer = MessageBox.Show(
            this,
            "Uninstall the mod files? Logs, config, and backups will be preserved.",
            _settings.AppTitle,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (answer != DialogResult.Yes)
            return;

        await _manager.UninstallAsync(progress, cancellationToken);
    }

    private async Task<ReleaseInfo> EnsureLatestReleaseAsync(IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        if (_latestRelease != null) return _latestRelease;
        progress.Report(new OperationProgress("Checking GitHub version...", 5));
        _latestRelease = await _releases.GetLatestReleaseAsync(cancellationToken);
        return _latestRelease;
    }

    private void RefreshStatus()
    {
        try
        {
            var status = _manager.GetStatus();
            _currentStatus = status;
            _statusLabel.Text = status.GameRunning ? "Game: running. Close it before install/update." : "Game: not running.";
            _installedLabel.Text = status.Installed
                ? $"Installed version: {status.InstalledVersion}{BuildVersionStateSuffix(status)}"
                : "Installed version: not installed";
            _latestLabel.Text = _latestRelease == null
                ? "Latest version: not checked"
                : $"Latest version: {_latestRelease.DisplayName}";
            _missingLabel.Text = BuildMissingText(status);
            UpdateButtonStates();
            AppendLog($"Status refreshed. Folder: {status.ModDirectory}");
        }
        catch (Exception ex)
        {
            AppendLog("ERROR: " + ex.Message);
        }
    }

    private static string BuildMissingText(ModStatus status)
    {
        if (!status.Installed) return "Files: mod is not installed.";
        if (status.MissingRequiredFiles.Count == 0 && status.MissingRecommendedFiles.Count == 0)
            return "Files: complete.";
        if (status.MissingRequiredFiles.Count > 0)
            return "Missing required: " + string.Join(", ", status.MissingRequiredFiles);
        return "Missing optional: " + string.Join(", ", status.MissingRecommendedFiles);
    }

    private void UpdateProgress(OperationProgress update)
    {
        if (update.Percent >= 0)
            _progress.Value = update.Percent;
        AppendLog(update.Message);
    }

    private void LaunchGame()
    {
        if (_manager.TryLaunchGame(out string message))
            AppendLog(message);
        else
            MessageBox.Show(this, message, _settings.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SetButtonsEnabled(bool enabled)
    {
        if (enabled)
        {
            UpdateButtonStates();
            return;
        }

        foreach (var button in new[]
        {
            _refreshButton,
            _checkButton,
            _installButton,
            _repairButton,
            _uninstallButton,
            _modFolderButton,
            _logsButton,
            _launchButton,
            _bugReportButton,
        })
        {
            button.Enabled = enabled;
        }
    }

    private void UpdateButtonStates()
    {
        bool enabled = !_busy;

        _refreshButton.Enabled = enabled;
        _checkButton.Enabled = enabled;
        _repairButton.Enabled = enabled;
        _uninstallButton.Enabled = enabled && _currentStatus?.Installed == true;
        _modFolderButton.Enabled = enabled;
        _logsButton.Enabled = enabled;
        _launchButton.Enabled = enabled;
        _bugReportButton.Enabled = enabled;

        bool canInstallOrUpdate = enabled && CanInstallOrUpdate(_currentStatus, _latestRelease);
        _installButton.Enabled = canInstallOrUpdate;
        _installButton.Text = BuildInstallButtonText(_currentStatus, _latestRelease, canInstallOrUpdate);

        foreach (var button in new[]
        {
            _refreshButton,
            _checkButton,
            _installButton,
            _repairButton,
            _uninstallButton,
            _modFolderButton,
            _logsButton,
            _launchButton,
            _bugReportButton,
        })
        {
            button.Invalidate();
        }
    }

    private async Task OpenLauncherBugReportAsync()
    {
        if (_busy) return;

        using var form = new LauncherBugReportForm();
        if (form.ShowDialog(this) != DialogResult.OK)
            return;

        _busy = true;
        SetButtonsEnabled(false);
        try
        {
            AppendLog("Creating launcher bug report...");
            var result = await _bugReporter.CreateAndSendAsync(
                form.Description,
                form.Attachments,
                _log.Text,
                _currentStatus,
                _latestRelease,
                CancellationToken.None);

            AppendLog(result.DiscordUploaded
                ? "Launcher bug report uploaded to Discord."
                : "Launcher bug report saved locally. Discord webhook is not configured.");

            MessageBox.Show(
                this,
                result.DiscordUploaded
                    ? "Launcher bug report sent to Discord."
                    : $"Launcher bug report saved locally:\r\n{result.DirectoryPath}\r\n\r\nDiscord webhook is not configured.",
                _settings.AppTitle,
                MessageBoxButtons.OK,
                result.DiscordUploaded ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            AppendLog("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, _settings.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            UpdateButtonStates();
        }
    }

    private static bool CanInstallOrUpdate(ModStatus? status, ReleaseInfo? latest)
    {
        if (status == null) return false;

        if (!status.Installed)
            return latest == null || latest.HasInstallableZip;

        if (latest == null || !latest.HasInstallableZip)
            return false;

        if (!TryParseVersion(status.InstalledVersion, out var installed))
            return true;
        if (!TryParseVersion(latest.Version, out var available))
            return false;

        return available.CompareTo(installed) > 0;
    }

    private static string BuildInstallButtonText(ModStatus? status, ReleaseInfo? latest, bool canInstallOrUpdate)
    {
        if (status?.Installed != true)
            return "Install";
        if (latest == null)
            return "Check first";
        if (!latest.HasInstallableZip)
            return "No package";
        return canInstallOrUpdate ? "Update" : "Up to date";
    }

    private string BuildVersionStateSuffix(ModStatus status)
    {
        if (_latestRelease == null || !status.Installed)
            return "";
        if (!_latestRelease.HasInstallableZip)
            return " (latest tag found, no package)";
        if (!TryParseVersion(status.InstalledVersion, out var installed) ||
            !TryParseVersion(_latestRelease.Version, out var available))
            return "";
        int comparison = available.CompareTo(installed);
        if (comparison > 0) return " (update available)";
        if (comparison <= 0) return " (up to date)";
        return "";
    }

    private void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        _log.AppendText(line);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (_backgroundImage == null)
        {
            base.OnPaintBackground(e);
            return;
        }

        if (_isLiveResizing)
        {
            using var brush = new SolidBrush(Color.FromArgb(9, 15, 24));
            e.Graphics.FillRectangle(brush, ClientRectangle);
            if (_backgroundCache != null)
                e.Graphics.DrawImageUnscaled(_backgroundCache, Point.Empty);
            return;
        }

        EnsureBackgroundCache();
        if (_backgroundCache != null)
            e.Graphics.DrawImageUnscaled(_backgroundCache, Point.Empty);
    }

    protected override void OnResizeBegin(EventArgs e)
    {
        EnsureBackgroundCache();
        _isLiveResizing = true;
        SetLayoutSuspended(this, suspended: true);
        base.OnResizeBegin(e);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        _isLiveResizing = false;
        SetLayoutSuspended(this, suspended: false);
        ResetBackgroundCache();
        Invalidate(invalidateChildren: true);
        base.OnResizeEnd(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        PositionBugReportButton();
        if (!_isLiveResizing && _backgroundCacheSize != ClientSize)
            ResetBackgroundCache();
        base.OnSizeChanged(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _backgroundCache?.Dispose();
            _backgroundImage?.Dispose();
            _bugReportIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void EnsureBackgroundCache()
    {
        if (_backgroundImage == null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;
        if (_backgroundCache != null && _backgroundCacheSize == ClientSize)
            return;

        ResetBackgroundCache();
        _backgroundCache = new Bitmap(ClientSize.Width, ClientSize.Height);
        _backgroundCacheSize = ClientSize;

        using var graphics = Graphics.FromImage(_backgroundCache);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(_backgroundImage, CalculateCoverRectangle(ClientSize, _backgroundImage.Size));
        using var overlay = new SolidBrush(Color.FromArgb(176, 5, 10, 18));
        graphics.FillRectangle(overlay, new Rectangle(Point.Empty, ClientSize));
    }

    private void ResetBackgroundCache()
    {
        _backgroundCache?.Dispose();
        _backgroundCache = null;
        _backgroundCacheSize = Size.Empty;
    }

    internal void PaintBackgroundSection(Graphics graphics, Rectangle destination, Rectangle sourceClientRectangle)
    {
        if (_backgroundImage == null)
        {
            using var fallback = new SolidBrush(BackColor);
            graphics.FillRectangle(fallback, destination);
            return;
        }

        EnsureBackgroundCache();
        if (_backgroundCache != null && sourceClientRectangle.Width > 0 && sourceClientRectangle.Height > 0)
        {
            graphics.DrawImage(
                _backgroundCache,
                destination,
                sourceClientRectangle,
                GraphicsUnit.Pixel);
            return;
        }

        using var brush = new SolidBrush(Color.FromArgb(9, 15, 24));
        graphics.FillRectangle(brush, destination);
    }

    private void PositionBugReportButton()
    {
        if (_bugReportButton.Parent == null) return;
        _bugReportButton.Location = new Point(
            Math.Max(8, ClientSize.Width - 30 - _bugReportButton.Width),
            34);
        _bugReportButton.BringToFront();
    }

    private static Image? LoadAssetImage(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "assets", fileName);
        return File.Exists(path) ? Image.FromFile(path) : null;
    }

    private static Image? LoadAssetImage(string fileName, Size size)
    {
        using var source = LoadAssetImage(fileName);
        if (source == null) return null;

        var bitmap = new Bitmap(size.Width, size.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, new Rectangle(Point.Empty, size));
        return bitmap;
    }

    private static Rectangle CalculateCoverRectangle(Size canvas, Size image)
    {
        if (canvas.Width <= 0 || canvas.Height <= 0 || image.Width <= 0 || image.Height <= 0)
            return Rectangle.Empty;

        double scale = Math.Max(canvas.Width / (double)image.Width, canvas.Height / (double)image.Height);
        int width = (int)Math.Ceiling(image.Width * scale);
        int height = (int)Math.Ceiling(image.Height * scale);
        int x = (canvas.Width - width) / 2;
        int y = (canvas.Height - height) / 2;
        return new Rectangle(x, y, width, height);
    }

    private static bool TryParseVersion(string value, out VersionParts version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();
        if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            value = value[1..];

        int metadataIndex = value.IndexOf('+');
        if (metadataIndex >= 0)
            value = value[..metadataIndex];

        string prerelease = "";
        int prereleaseIndex = value.IndexOf('-');
        if (prereleaseIndex >= 0)
        {
            prerelease = value[(prereleaseIndex + 1)..];
            value = value[..prereleaseIndex];
        }

        string[] parts = value.Split('.');
        if (parts.Length < 3 ||
            !int.TryParse(parts[0], out int major) ||
            !int.TryParse(parts[1], out int minor) ||
            !int.TryParse(parts[2], out int patch))
        {
            return false;
        }

        version = new VersionParts(major, minor, patch, prerelease);
        return true;
    }

    private static void SetLayoutSuspended(Control control, bool suspended)
    {
        if (suspended)
        {
            control.SuspendLayout();
            foreach (Control child in control.Controls)
                SetLayoutSuspended(child, suspended: true);
            return;
        }

        foreach (Control child in control.Controls)
            SetLayoutSuspended(child, suspended: false);
        control.ResumeLayout(performLayout: true);
    }

    private readonly record struct VersionParts(int Major, int Minor, int Patch, string Prerelease) : IComparable<VersionParts>
    {
        public int CompareTo(VersionParts other)
        {
            int major = Major.CompareTo(other.Major);
            if (major != 0) return major;

            int minor = Minor.CompareTo(other.Minor);
            if (minor != 0) return minor;

            int patch = Patch.CompareTo(other.Patch);
            if (patch != 0) return patch;

            bool thisPrerelease = !string.IsNullOrWhiteSpace(Prerelease);
            bool otherPrerelease = !string.IsNullOrWhiteSpace(other.Prerelease);
            if (thisPrerelease != otherPrerelease)
                return thisPrerelease ? -1 : 1;

            return string.Compare(Prerelease, other.Prerelease, StringComparison.OrdinalIgnoreCase);
        }
    }
}

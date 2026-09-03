using System.Diagnostics;
using DgVoodooEasyInstaller.Core;

namespace DgVoodooEasyInstaller;

public sealed class MainForm : Form
{
    private readonly string? initialGame;
    private readonly InstallManager installManager = new();
    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly Label gameLabel = new();
    private readonly Label detectionLabel = new();
    private readonly Label statusLabel = new();
    private readonly CheckedListBox apiList = new();
    private readonly Button browseButton = new();
    private readonly Button actionButton = new();
    private readonly Button manualButton = new();
    private readonly ProgressBar progress = new();
    private GameAnalysis? analysis;
    private InstallState installState;

    public MainForm(string? initialGame)
    {
        this.initialGame = initialGame;
        Text = "dgVoodoo2 Easy Installer";
        ClientSize = new Size(760, 560);
        MinimumSize = new Size(650, 520);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(17, 20, 24);
        ForeColor = Color.FromArgb(235, 238, 241);
        Font = new Font("Segoe UI", 10F);

        BuildUi();
        Shown += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(this.initialGame) && File.Exists(this.initialGame))
                await SelectGameAsync(this.initialGame);
            else
                BrowseForGame();
        };
    }

    private void BuildUi()
    {
        var title = new Label
        {
            Text = "MAKE OLD GAMES PLAY NICE",
            Font = new Font("Segoe UI Semibold", 24F, FontStyle.Bold),
            ForeColor = Color.FromArgb(247, 153, 58),
            AutoSize = true,
            Location = new Point(34, 28)
        };
        var subtitle = new Label
        {
            Text = "Select a game. DirectX, Glide, and OpenGL compatibility are handled for you.",
            AutoSize = true,
            ForeColor = Color.FromArgb(170, 177, 186),
            Location = new Point(38, 78)
        };

        var panel = new Panel
        {
            Location = new Point(36, 116),
            Size = new Size(688, 350),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(27, 31, 37)
        };
        var step = MakeLabel("GAME EXECUTABLE", 20, 18, Color.FromArgb(247, 153, 58), true);
        gameLabel.Text = "No game selected";
        gameLabel.Location = new Point(20, 48);
        gameLabel.Size = new Size(530, 48);
        gameLabel.AutoEllipsis = true;
        browseButton.Text = "Browse...";
        browseButton.Location = new Point(566, 47);
        browseButton.Size = new Size(100, 34);
        StyleButton(browseButton, false);
        browseButton.Click += (_, _) => BrowseForGame();

        detectionLabel.Text = "Choose the game's main .exe to begin.";
        detectionLabel.Location = new Point(20, 105);
        detectionLabel.Size = new Size(646, 48);
        detectionLabel.ForeColor = Color.FromArgb(190, 196, 203);

        apiList.Location = new Point(20, 164);
        apiList.Size = new Size(646, 112);
        apiList.BackColor = Color.FromArgb(21, 24, 29);
        apiList.ForeColor = ForeColor;
        apiList.BorderStyle = BorderStyle.FixedSingle;
        apiList.CheckOnClick = true;
        apiList.Enabled = false;
        apiList.ItemCheck += ApiListOnItemCheck;

        statusLabel.Text = "Waiting for a game";
        statusLabel.Location = new Point(20, 296);
        statusLabel.Size = new Size(646, 22);
        statusLabel.ForeColor = Color.FromArgb(170, 177, 186);
        progress.Location = new Point(20, 324);
        progress.Size = new Size(646, 7);
        progress.Style = ProgressBarStyle.Continuous;

        panel.Controls.AddRange([step, gameLabel, browseButton, detectionLabel, apiList, statusLabel, progress]);

        var buttonPanel = new TableLayoutPanel
        {
            Location = new Point(36, 488),
            Size = new Size(688, 45),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = BackColor
        };
        buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
        buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        actionButton.Text = "Install compatibility files";
        actionButton.Dock = DockStyle.Fill;
        actionButton.Margin = new Padding(0, 0, 6, 0);
        actionButton.Enabled = false;
        StyleButton(actionButton, true);
        actionButton.Click += async (_, _) => await PerformActionAsync();

        manualButton.Text = "Use local packages...";
        manualButton.Dock = DockStyle.Fill;
        manualButton.Margin = new Padding(6, 0, 0, 0);
        manualButton.Enabled = false;
        StyleButton(manualButton, false);
        manualButton.Click += async (_, _) => await PerformManualInstallAsync();
        buttonPanel.Controls.Add(actionButton, 0, 0);
        buttonPanel.Controls.Add(manualButton, 1, 0);

        Controls.AddRange([title, subtitle, panel, buttonPanel]);
    }

    private async void BrowseForGame()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select the game's main executable",
            Filter = "Game executables (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await SelectGameAsync(dialog.FileName);
    }

    private async Task SelectGameAsync(string path)
    {
        SetBusy(true, "Inspecting game executable...");
        try
        {
            analysis = await Task.Run(() => GameAnalyzer.Analyze(path));
            var directory = Path.GetDirectoryName(path)!;
            installState = installManager.GetInstallState(directory);
            gameLabel.Text = path;
            PopulateApis(analysis);

            if (installState == InstallState.Managed)
            {
                var manifest = await installManager.ReadManifestAsync(directory);
                var products = new[]
                {
                    manifest.DgVoodooVersion is null ? null : $"dgVoodoo2 {manifest.DgVoodooVersion}",
                    manifest.MesaVersion is null ? null : $"Mesa3D {manifest.MesaVersion}"
                }.Where(value => value is not null);
                var productText = string.Join(" and ", products);
                detectionLabel.Text = $"{(productText.Length == 0 ? "Compatibility files" : productText)} was installed here by this tool.";
                actionButton.Text = "Uninstall and restore backups";
                apiList.Enabled = false;
            }
            else if (installState == InstallState.Unmanaged)
            {
                detectionLabel.Text = "Existing compatibility files were detected. No installer backups are available.";
                actionButton.Text = "Remove existing compatibility files";
                apiList.Enabled = false;
            }
            else
            {
                var apiText = analysis.Apis.Count == 0 ? "No graphics import was identified." :
                    $"Detected {string.Join(", ", analysis.Apis.Select(FormatApi))}.";
                detectionLabel.Text = $"{FormatArchitecture(analysis.Architecture)} executable. {apiText}";
                actionButton.Text = "Download and install compatibility files";
                apiList.Enabled = true;
            }

            statusLabel.Text = GetReadyStatus(analysis, installState);
            actionButton.Enabled = installState != InstallState.NotInstalled || CanInstall();
            manualButton.Enabled = installState == InstallState.NotInstalled && CanInstall();
        }
        catch (Exception ex)
        {
            analysis = null;
            ShowError("Could not inspect this executable", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void PopulateApis(GameAnalysis game)
    {
        apiList.Items.Clear();
        GraphicsApi[] options = game.Architecture switch
        {
            GameArchitecture.X86 =>
            [
                GraphicsApi.DirectDraw, GraphicsApi.Direct3DRetained, GraphicsApi.Direct3D8,
                GraphicsApi.Direct3D9, GraphicsApi.Glide, GraphicsApi.Glide2, GraphicsApi.Glide3,
                GraphicsApi.OpenGl
            ],
            GameArchitecture.X64 =>
            [
                GraphicsApi.Direct3D9, GraphicsApi.Glide, GraphicsApi.Glide2, GraphicsApi.Glide3,
                GraphicsApi.OpenGl
            ],
            _ =>
            [
                GraphicsApi.Direct3D9, GraphicsApi.Glide, GraphicsApi.Glide2, GraphicsApi.Glide3
            ]
        };
        foreach (var api in options)
            apiList.Items.Add(new ApiItem(api, FormatApi(api)), game.Apis.Contains(api));
    }

    private void ApiListOnItemCheck(object? sender, ItemCheckEventArgs e) => BeginInvoke(() =>
    {
        actionButton.Enabled = CanInstall();
        manualButton.Enabled = CanInstall();
    });

    private bool CanInstall() => analysis is { Architecture: not GameArchitecture.Unknown } && apiList.CheckedItems.Count > 0;

    private static string GetReadyStatus(GameAnalysis game, InstallState state)
    {
        if (state != InstallState.NotInstalled)
            return "Ready";
        if (game.Architecture == GameArchitecture.Arm64 && game.Apis.Contains(GraphicsApi.OpenGl))
            return "The Mesa3D Windows distribution does not provide ARM64 binaries.";
        if (game.Architecture != GameArchitecture.X86 &&
            game.Apis.Any(api => api is GraphicsApi.DirectDraw or GraphicsApi.Direct3D8))
            return "dgVoodoo2 only provides DirectX 1-8 wrappers for 32-bit games.";
        return "Ready";
    }

    private async Task PerformActionAsync()
    {
        if (analysis is null) return;
        var directory = Path.GetDirectoryName(analysis.ExecutablePath)!;
        try
        {
            SetBusy(true, installState == InstallState.NotInstalled ? "Finding the latest stable releases..." : "Removing compatibility files...");
            if (installState == InstallState.Managed)
            {
                if (!Confirm("Uninstall the compatibility files and restore every file backed up by this installer?")) return;
                await installManager.UninstallAsync(directory);
                MessageBox.Show(this, "Compatibility files were removed and all installer backups were restored.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (installState == InstallState.Unmanaged)
            {
                if (!Confirm("Remove the detected compatibility files? This installation was not made by this tool, so no backups can be restored.")) return;
                installManager.RemoveUnmanaged(directory);
                MessageBox.Show(this, "The detected compatibility files were removed.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                await InstallAsync(directory, null, null, null);
                return;
            }
            await SelectGameAsync(analysis.ExecutablePath);
        }
        catch (Exception ex)
        {
            ShowError("The operation could not be completed", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task PerformManualInstallAsync()
    {
        if (analysis is null || installState != InstallState.NotInstalled) return;
        var selectedApis = GetSelectedApis();
        string? dgArchive = null;
        string? d3drmArchive = null;
        string? mesaArchive = null;

        if (selectedApis.Any(IsDgVoodooApi))
        {
            dgArchive = SelectPackage("Select a dgVoodoo2 release ZIP",
                "dgVoodoo2 ZIP archives (*.zip)|*.zip");
            if (dgArchive is null) return;
        }
        if (selectedApis.Contains(GraphicsApi.Direct3DRetained))
        {
            d3drmArchive = SelectPackage("Select the D3DRM ZIP",
                "D3DRM ZIP archives (*.zip)|*.zip");
            if (d3drmArchive is null) return;
        }
        if (selectedApis.Contains(GraphicsApi.OpenGl))
        {
            mesaArchive = SelectPackage("Select a Mesa3D Windows release archive",
                "Mesa3D archives (*.7z;*.zip)|*.7z;*.zip|All files (*.*)|*.*");
            if (mesaArchive is null) return;
        }

        try
        {
            SetBusy(true, "Validating local packages...");
            await InstallAsync(Path.GetDirectoryName(analysis.ExecutablePath)!, dgArchive, d3drmArchive, mesaArchive);
        }
        catch (Exception ex)
        {
            ShowError("The local package could not be installed", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private string? SelectPackage(string title, string filter)
    {
        using var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    private async Task InstallAsync(string directory, string? localDgArchive, string? localD3DrmArchive,
        string? localMesaArchive)
    {
        var selectedApis = GetSelectedApis();
        var releaseClient = new ReleaseClient(httpClient);
        var temporaryFiles = new List<string>();
        string? dgVersion = localDgArchive is null ? null : VersionFromFile(localDgArchive, "dgVoodoo2_");
        string? mesaVersion = localMesaArchive is null ? null : VersionFromFile(localMesaArchive, "mesa3d-");
        var dgArchive = localDgArchive;
        var mesaArchive = localMesaArchive;
        var d3drmArchive = localD3DrmArchive;
        try
        {
            progress.Style = ProgressBarStyle.Continuous;
            var downloadProgress = new Progress<int>(value => progress.Value = value);
            if (dgArchive is null && selectedApis.Any(IsDgVoodooApi))
            {
                var release = await releaseClient.GetLatestAsync();
                dgVersion = release.Version;
                dgArchive = NewTemporaryFile(".zip", temporaryFiles);
                statusLabel.Text = $"Downloading dgVoodoo2 {release.Version} from Dege's official sites...";
                await releaseClient.DownloadAsync(release, dgArchive, downloadProgress);
            }
            if (d3drmArchive is null && selectedApis.Contains(GraphicsApi.Direct3DRetained))
            {
                d3drmArchive = NewTemporaryFile(".zip", temporaryFiles);
                statusLabel.Text = "Downloading the official D3DRM package...";
                var uri = await releaseClient.GetD3DrmDownloadAsync();
                await releaseClient.DownloadFileAsync(uri, d3drmArchive, downloadProgress);
            }
            if (mesaArchive is null && selectedApis.Contains(GraphicsApi.OpenGl))
            {
                var release = await new MesaClient(httpClient).GetLatestAsync();
                mesaVersion = release.Version;
                mesaArchive = NewTemporaryFile(".7z", temporaryFiles);
                statusLabel.Text = $"Downloading Mesa3D {release.Version} from GitHub...";
                await new MesaClient(httpClient).DownloadAsync(release, mesaArchive, downloadProgress);
            }

            statusLabel.Text = "Backing up existing files and installing compatibility libraries...";
            await installManager.InstallAsync(analysis!, selectedApis, dgVersion, dgArchive,
                d3drmArchive, mesaVersion, mesaArchive);
        }
        finally
        {
            foreach (var path in temporaryFiles)
                if (File.Exists(path)) File.Delete(path);
        }

        installState = InstallState.Managed;
        if (selectedApis.Any(IsDgVoodooApi))
        {
            statusLabel.Text = "Installed. Opening dgVoodoo2 configuration...";
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(directory, "dgVoodooCpl.exe"),
                WorkingDirectory = directory,
                UseShellExecute = true
            });
        }
        else
        {
            MessageBox.Show(this, "The selected compatibility files were installed for this game.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        await SelectGameAsync(analysis!.ExecutablePath);
    }

    private GraphicsApi[] GetSelectedApis() =>
        apiList.CheckedItems.Cast<ApiItem>().Select(item => item.Api).ToArray();

    private static bool IsDgVoodooApi(GraphicsApi api) => api is GraphicsApi.DirectDraw or
        GraphicsApi.Direct3D8 or GraphicsApi.Direct3D9 or GraphicsApi.Glide or GraphicsApi.Glide2 or GraphicsApi.Glide3;

    private static string NewTemporaryFile(string extension, ICollection<string> files)
    {
        var path = Path.Combine(Path.GetTempPath(), $"compat-{Guid.NewGuid():N}{extension}");
        files.Add(path);
        return path;
    }

    private static string VersionFromFile(string path, string prefix)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var start = name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return "manual package";
        var version = name[(start + prefix.Length)..].Replace('_', '.');
        if (prefix.StartsWith("dgVoodoo", StringComparison.OrdinalIgnoreCase) && !version.StartsWith("2."))
            version = $"2.{version}";
        if (prefix.StartsWith("mesa", StringComparison.OrdinalIgnoreCase))
            version = version.Split("-release", StringSplitOptions.RemoveEmptyEntries)[0];
        return version;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        browseButton.Enabled = !busy;
        actionButton.Enabled = !busy && analysis is not null && (installState != InstallState.NotInstalled || CanInstall());
        manualButton.Enabled = !busy && analysis is not null && installState == InstallState.NotInstalled && CanInstall();
        apiList.Enabled = !busy && installState == InstallState.NotInstalled;
        progress.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        if (!busy) progress.Value = 0;
        if (status is not null) statusLabel.Text = status;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private bool Confirm(string message) => MessageBox.Show(this, message, Text,
        MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private void ShowError(string heading, Exception ex) => MessageBox.Show(this, $"{heading}.\n\n{ex.Message}", Text,
        MessageBoxButtons.OK, MessageBoxIcon.Error);

    private static Label MakeLabel(string text, int x, int y, Color color, bool bold = false) => new()
    {
        Text = text,
        AutoSize = true,
        Location = new Point(x, y),
        ForeColor = color,
        Font = new Font("Segoe UI", 9F, bold ? FontStyle.Bold : FontStyle.Regular)
    };

    private static void StyleButton(Button button, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(82, 89, 99);
        button.BackColor = primary ? Color.FromArgb(224, 119, 40) : Color.FromArgb(37, 42, 49);
        button.ForeColor = Color.White;
        button.Cursor = Cursors.Hand;
    }

    private static string FormatArchitecture(GameArchitecture architecture) => architecture switch
    {
        GameArchitecture.X86 => "32-bit",
        GameArchitecture.X64 => "64-bit",
        GameArchitecture.Arm64 => "ARM64",
        _ => "Unknown architecture"
    };

    private static string FormatApi(GraphicsApi api) => api switch
    {
        GraphicsApi.DirectDraw => "DirectX 1-7 (DirectDraw)",
        GraphicsApi.Direct3DRetained => "Direct3D Retained Mode (D3DRM)",
        GraphicsApi.Direct3D8 => "Direct3D 8",
        GraphicsApi.Direct3D9 => "Direct3D 9",
        GraphicsApi.Glide => "Glide 1",
        GraphicsApi.Glide2 => "Glide 2",
        GraphicsApi.Glide3 => "Glide 3",
        GraphicsApi.OpenGl => "OpenGL (Mesa3D on Direct3D 12)",
        _ => api.ToString()
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) httpClient.Dispose();
        base.Dispose(disposing);
    }

    private sealed record ApiItem(GraphicsApi Api, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
}

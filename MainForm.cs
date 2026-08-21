using System.Diagnostics;
using System.Drawing;

namespace JACKOBsWartalesModLauncher;

internal sealed class MainForm : Form
{
    private readonly TextBox _gamePath = new();
    private readonly Button _changeFolder = new();
    private readonly ListView _mods = new();
    private readonly Button _install = new();
    private readonly Button _uninstall = new();
    private readonly Button _verify = new();
    private readonly Button _restore = new();
    private readonly Label _status = new();
    private readonly Label _dropHint = new();
    private readonly Label _gameDetected = new();
    private readonly Label _pakDetected = new();
    private readonly Panel _emptyState = new();
    private readonly Panel _statusDot = new();
    private readonly ToolTip _tips = new();

    private string? _gameDirectory;
    private bool _busy;

    private static readonly Color Bg = Color.FromArgb(18, 20, 24);
    private static readonly Color PanelBg = Color.FromArgb(27, 30, 36);
    private static readonly Color PanelBg2 = Color.FromArgb(34, 38, 45);
    private static readonly Color Border = Color.FromArgb(59, 65, 76);
    private static readonly Color TextMain = Color.FromArgb(235, 239, 243);
    private static readonly Color TextMuted = Color.FromArgb(158, 168, 180);
    private static readonly Color Accent = Color.FromArgb(105, 207, 215);
    private static readonly Color Green = Color.FromArgb(84, 201, 126);
    private static readonly Color Amber = Color.FromArgb(235, 181, 72);
    private static readonly Color Red = Color.FromArgb(232, 91, 91);

    public MainForm()
    {
        Text = $"JACKOB's Wartales Mod Launcher v{LauncherCore.LauncherVersion}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 690);
        ClientSize = new Size(1060, 760);
        BackColor = Bg;
        ForeColor = TextMain;
        Font = new Font("Segoe UI", 10F);
        AllowDrop = true;

        BuildUi();
        WireEvents();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _gameDirectory = LauncherCore.FindKnownGameDirectory();
        if (_gameDirectory is null)
        {
            _gamePath.Text = "Wartales folder not selected";
            SetStatus("Select your Wartales folder to begin.", StatusKind.Warning);
        }
        else
        {
            _gamePath.Text = _gameDirectory;
            SetStatus("Ready.", StatusKind.Ready);
        }
        UpdateGameDetection();
        RefreshMods();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(24),
            BackColor = Bg
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 144));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildGamePathPanel(), 0, 1);
        root.Controls.Add(BuildModsPanel(), 0, 2);
        root.Controls.Add(BuildButtonsPanel(), 0, 3);
        root.Controls.Add(BuildStatusPanel(), 0, 4);
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Bg };

        var logo = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 78,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Accent,
            BackColor = Bg,
            Font = new Font("Consolas", 8.0F, FontStyle.Bold),
            Text =
@"     ██╗ █████╗  ██████╗██╗  ██╗ ██████╗ ██████╗ ████████╗██╗  ██╗ █████╗ ████████╗███████╗███╗   ███╗███████╗
     ██║██╔══██╗██╔════╝██║ ██╔╝██╔═══██╗██╔══██╗╚══██╔══╝██║  ██║██╔══██╗╚══██╔══╝██╔════╝████╗ ████║██╔════╝
     ██║███████║██║     █████╔╝ ██║   ██║██████╔╝   ██║   ███████║███████║   ██║   ███████╗██╔████╔██║█████╗
██   ██║██╔══██║██║     ██╔═██╗ ██║   ██║██╔══██╗   ██║   ██╔══██║██╔══██║   ██║   ╚════██║██║╚██╔╝██║██╔══╝
╚█████╔╝██║  ██║╚██████╗██║  ██╗╚██████╔╝██████╔╝   ██║   ██║  ██║██║  ██║   ██║   ███████║██║ ╚═╝ ██║███████╗
 ╚════╝ ╚═╝  ╚═╝ ╚═════╝╚═╝  ╚═╝ ╚═════╝ ╚═════╝    ╚═╝   ╚═╝  ╚═╝╚═╝  ╚═╝   ╚═╝   ╚══════╝╚═╝     ╚═╝╚══════╝"
        };

        var title = new Label
        {
            AutoSize = false,
            Location = new Point(6, 84),
            Size = new Size(720, 23),
            ForeColor = TextMain,
            BackColor = Bg,
            Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold),
            Text = $"WARTALES MOD LAUNCHER   •   v{LauncherCore.LauncherVersion}"
        };

        var safety = new Label
        {
            AutoSize = false,
            Location = new Point(6, 108),
            Size = new Size(900, 22),
            ForeColor = Color.FromArgb(134, 194, 153),
            BackColor = Bg,
            Font = new Font("Segoe UI", 9F),
            Text = "✓ Offline     ✓ No PowerShell     ✓ No downloads     ✓ No registry changes"
        };

        panel.Controls.Add(logo);
        panel.Controls.Add(title);
        panel.Controls.Add(safety);
        return panel;
    }

    private Control BuildGamePathPanel()
    {
        var card = CreateCard();
        var title = CreateSectionTitle("WARTALES INSTALLATION");
        title.Location = new Point(18, 10);
        title.Size = new Size(300, 22);

        _gamePath.ReadOnly = true;
        _gamePath.BorderStyle = BorderStyle.FixedSingle;
        _gamePath.BackColor = PanelBg2;
        _gamePath.ForeColor = TextMain;
        _gamePath.Location = new Point(18, 39);
        _gamePath.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        _gamePath.Width = card.Width - 172;

        StyleButton(_changeFolder, "Change Folder", secondary: true);
        _changeFolder.Size = new Size(130, 32);
        _changeFolder.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _changeFolder.Location = new Point(card.Width - 148, 36);

        _gameDetected.AutoSize = true;
        _gameDetected.Location = new Point(19, 77);
        _gameDetected.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);

        _pakDetected.AutoSize = true;
        _pakDetected.Location = new Point(205, 77);
        _pakDetected.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);

        card.Resize += (_, _) =>
        {
            _gamePath.Width = Math.Max(120, card.ClientSize.Width - 184);
            _changeFolder.Left = card.ClientSize.Width - 148;
        };

        card.Controls.Add(title);
        card.Controls.Add(_gamePath);
        card.Controls.Add(_changeFolder);
        card.Controls.Add(_gameDetected);
        card.Controls.Add(_pakDetected);
        return card;
    }

    private Control BuildModsPanel()
    {
        var card = CreateCard();
        var title = CreateSectionTitle("MANAGED MODS");
        title.Location = new Point(18, 14);
        title.Size = new Size(220, 22);

        _dropHint.Text = "Drop a compatible mod ZIP anywhere in this window, or click Install Mod.";
        _dropHint.ForeColor = TextMuted;
        _dropHint.AutoSize = true;
        _dropHint.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _dropHint.Location = new Point(490, 16);

        _mods.View = View.Details;
        _mods.FullRowSelect = true;
        _mods.MultiSelect = false;
        _mods.HideSelection = false;
        _mods.BorderStyle = BorderStyle.FixedSingle;
        _mods.BackColor = PanelBg2;
        _mods.ForeColor = TextMain;
        _mods.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _mods.Columns.Add("Mod", 520);
        _mods.Columns.Add("Version", 120);
        _mods.Columns.Add("Status", 160);
        _mods.Location = new Point(18, 48);
        _mods.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _mods.Size = new Size(card.Width - 36, card.Height - 66);

        _emptyState.BackColor = PanelBg2;
        _emptyState.Location = _mods.Location;
        _emptyState.Size = _mods.Size;
        _emptyState.Anchor = _mods.Anchor;
        _emptyState.Visible = false;

        var emptyTitle = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 36,
            Text = "No mods installed",
            TextAlign = ContentAlignment.BottomCenter,
            ForeColor = TextMain,
            Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold),
            Padding = new Padding(0, 0, 0, 3)
        };
        var emptyHint = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 48,
            Text = "Drop a compatible mod ZIP here\nor click Install Mod to get started.",
            TextAlign = ContentAlignment.TopCenter,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 9.5F)
        };
        var emptyCenter = new Panel
        {
            Size = new Size(470, 92),
            BackColor = PanelBg2
        };
        emptyCenter.Controls.Add(emptyHint);
        emptyCenter.Controls.Add(emptyTitle);
        _emptyState.Controls.Add(emptyCenter);

        void CenterEmptyState()
        {
            emptyCenter.Left = Math.Max(0, (_emptyState.ClientSize.Width - emptyCenter.Width) / 2);
            emptyCenter.Top = Math.Max(20, (_emptyState.ClientSize.Height - emptyCenter.Height) / 2);
        }
        _emptyState.Resize += (_, _) => CenterEmptyState();
        CenterEmptyState();

        card.Resize += (_, _) =>
        {
            _mods.Size = new Size(Math.Max(300, card.ClientSize.Width - 36), Math.Max(120, card.ClientSize.Height - 66));
            _emptyState.Size = _mods.Size;
            _dropHint.Left = Math.Max(260, card.ClientSize.Width - _dropHint.PreferredWidth - 18);
            if (_mods.Columns.Count == 3)
            {
                var usable = Math.Max(500, _mods.ClientSize.Width - 12);
                _mods.Columns[0].Width = Math.Max(300, usable - 280);
                _mods.Columns[1].Width = 110;
                _mods.Columns[2].Width = 150;
            }
        };

        card.Controls.Add(title);
        card.Controls.Add(_dropHint);
        card.Controls.Add(_mods);
        card.Controls.Add(_emptyState);
        _emptyState.BringToFront();
        return card;
    }

    private Control BuildButtonsPanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 17, 0, 10)
        };

        StyleButton(_install, "Install Mod", secondary: false);
        StyleButton(_uninstall, "Uninstall Selected", secondary: true);
        StyleButton(_verify, "Verify Files", secondary: true);
        StyleButton(_restore, "Restore Vanilla", secondary: true, danger: true);

        _install.Width = 168;
        _uninstall.Width = 190;
        _verify.Width = 150;
        _restore.Width = 168;

        panel.Controls.Add(_install);
        panel.Controls.Add(_uninstall);
        panel.Controls.Add(_verify);
        panel.Controls.Add(_restore);
        return panel;
    }

    private Control BuildStatusPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = PanelBg };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawLine(pen, 0, 0, panel.ClientSize.Width, 0);
        };

        _statusDot.Size = new Size(10, 10);
        _statusDot.Location = new Point(14, 19);
        _statusDot.BackColor = Green;

        _status.AutoSize = false;
        _status.Location = new Point(34, 10);
        _status.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _status.Size = new Size(850, 30);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = TextMuted;

        var format = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            ForeColor = TextMuted,
            Text = "Package format: v1"
        };
        format.Location = new Point(panel.Width - 300, 15);
        panel.Resize += (_, _) =>
        {
            _status.Width = Math.Max(200, panel.ClientSize.Width - 350);
            format.Left = panel.ClientSize.Width - format.PreferredWidth - 14;
        };

        panel.Controls.Add(_statusDot);
        panel.Controls.Add(_status);
        panel.Controls.Add(format);
        return panel;
    }

    private Panel CreateCard()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = PanelBg, Margin = new Padding(0, 6, 0, 6) };
        p.Paint += (_, e) =>
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawRectangle(pen, 0, 0, p.ClientSize.Width - 1, p.ClientSize.Height - 1);
        };
        return p;
    }

    private static Label CreateSectionTitle(string text) => new()
    {
        Text = text,
        ForeColor = Color.FromArgb(218, 225, 231),
        Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private void StyleButton(Button button, string text, bool secondary, bool danger = false)
    {
        button.Text = text;
        button.Height = 46;
        button.Margin = new Padding(0, 0, 12, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.Cursor = Cursors.Hand;
        button.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold);
        button.ForeColor = TextMain;

        if (danger)
        {
            button.BackColor = Color.FromArgb(78, 38, 42);
            button.FlatAppearance.BorderColor = Color.FromArgb(135, 62, 68);
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(98, 44, 50);
        }
        else if (secondary)
        {
            button.BackColor = PanelBg2;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(47, 52, 61);
        }
        else
        {
            button.BackColor = Color.FromArgb(39, 104, 110);
            button.FlatAppearance.BorderColor = Accent;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(47, 126, 133);
        }
    }

    private void WireEvents()
    {
        _changeFolder.Click += (_, _) => ChangeGameFolder();
        _install.Click += async (_, _) => await ChooseAndInstallAsync();
        _uninstall.Click += async (_, _) => await UninstallSelectedAsync();
        _verify.Click += async (_, _) => await VerifyAsync();
        _restore.Click += async (_, _) => await RestoreAsync();
        _mods.SelectedIndexChanged += (_, _) => UpdateEnabledState();

        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            {
                var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
                if (files?.Length == 1 && files[0].EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    e.Effect = DragDropEffects.Copy;
            }
        };

        DragDrop += async (_, e) =>
        {
            var files = (string[]?)e.Data?.GetData(DataFormats.FileDrop);
            if (files?.Length == 1)
                await InstallPackageAsync(files[0]);
        };

        _tips.SetToolTip(_restore, "Remove all launcher-managed mods and rebuild the captured vanilla baseline.");
        _tips.SetToolTip(_verify, "Verify that managed PAK entries still match the launcher's state.");
    }

    private void ChangeGameFolder()
    {
        if (_busy) return;
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the Wartales installation folder containing res.pak",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = _gameDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            LauncherCore.SetGameDirectory(dialog.SelectedPath);
            _gameDirectory = Path.GetFullPath(dialog.SelectedPath);
            _gamePath.Text = _gameDirectory;
            UpdateGameDetection();
            RefreshMods();
            SetStatus("Wartales folder selected and res.pak detected.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async Task ChooseAndInstallAsync()
    {
        if (!EnsureGameSelected()) return;
        using var dialog = new OpenFileDialog
        {
            Title = "Select a JACKOB Wartales mod package",
            Filter = "JACKOB Wartales mod package (*.zip)|*.zip|ZIP files (*.zip)|*.zip",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await InstallPackageAsync(dialog.FileName);
    }

    private async Task InstallPackageAsync(string packagePath)
    {
        if (!EnsureGameSelected()) return;

        ModManifest manifest;
        try
        {
            manifest = LauncherCore.PreviewPackage(packagePath);
        }
        catch (Exception ex)
        {
            ShowError("This ZIP is not a valid launcher-compatible mod package.\n\n" + ex.Message);
            return;
        }

        var description = string.IsNullOrWhiteSpace(manifest.Description) ? "" : $"\n\n{manifest.Description}";
        var answer = MessageBox.Show(this,
            $"{manifest.Name}\nVersion {manifest.Version}\nAuthor: {manifest.Author}{description}\n\nInstall / update this mod?",
            "Install mod",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);
        if (answer != DialogResult.Yes) return;

        await RunOperationAsync(
            $"Installing {manifest.Name}...",
            () => LauncherCore.InstallPackage(_gameDirectory!, packagePath),
            $"Installed {manifest.Name} v{manifest.Version}.");
    }

    private async Task UninstallSelectedAsync()
    {
        if (!EnsureGameSelected() || _mods.SelectedItems.Count != 1) return;
        if (_mods.SelectedItems[0].Tag is not InstalledModState mod) return;

        var answer = MessageBox.Show(this,
            $"Uninstall {mod.Name} v{mod.Version}?\n\nThe launcher will rebuild the managed PAK entries from the captured baseline and any remaining managed mods.",
            "Uninstall mod",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        await RunOperationAsync(
            $"Uninstalling {mod.Name}...",
            () => LauncherCore.UninstallMod(_gameDirectory!, mod.Id),
            $"Uninstalled {mod.Name}.");
    }

    private async Task VerifyAsync()
    {
        if (!EnsureGameSelected()) return;
        await RunOperationAsync(
            "Verifying managed files...",
            () => LauncherCore.Verify(_gameDirectory!),
            "Managed files match launcher state.");
    }

    private async Task RestoreAsync()
    {
        if (!EnsureGameSelected()) return;
        var answer = MessageBox.Show(this,
            "Remove ALL launcher-managed mods and restore the captured baseline?",
            "Restore vanilla baseline",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        await RunOperationAsync(
            "Restoring baseline...",
            () => LauncherCore.RestoreBaseline(_gameDirectory!),
            "All launcher-managed mods were removed. Baseline restored.");
    }

    private async Task RunOperationAsync(string busyText, Action operation, string successText)
    {
        if (_busy) return;
        _busy = true;
        UpdateEnabledState();
        SetStatus(busyText, StatusKind.Busy);
        UseWaitCursor = true;

        try
        {
            await Task.Run(operation);
            RefreshMods();
            SetStatus(successText, StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus("Operation stopped. No further action was taken.", StatusKind.Error);
            ShowError(ex.Message);
        }
        finally
        {
            UseWaitCursor = false;
            _busy = false;
            UpdateEnabledState();
        }
    }

    private void RefreshMods()
    {
        _mods.BeginUpdate();
        try
        {
            _mods.Items.Clear();
            UpdateGameDetection();
            if (_gameDirectory is null || !LauncherCore.IsValidGameDirectory(_gameDirectory))
            {
                _emptyState.Visible = true;
                _emptyState.BringToFront();
                UpdateEnabledState();
                return;
            }

            foreach (var mod in LauncherCore.GetInstalledMods(_gameDirectory))
            {
                var item = new ListViewItem(mod.Name) { Tag = mod };
                item.SubItems.Add(mod.Version);
                item.SubItems.Add("Installed");
                item.ForeColor = TextMain;
                _mods.Items.Add(item);
            }

            _emptyState.Visible = _mods.Items.Count == 0;
            if (_emptyState.Visible)
                _emptyState.BringToFront();
            else
                _mods.BringToFront();
        }
        catch (Exception ex)
        {
            SetStatus("Could not read launcher state.", StatusKind.Error);
            ShowError(ex.Message);
        }
        finally
        {
            _mods.EndUpdate();
            UpdateEnabledState();
        }
    }

    private void UpdateGameDetection()
    {
        var folderExists = _gameDirectory is not null && Directory.Exists(_gameDirectory);
        var pakExists = folderExists && File.Exists(Path.Combine(_gameDirectory!, "res.pak"));

        _gameDetected.Text = folderExists ? "✓ Wartales detected" : "⚠ Wartales not detected";
        _gameDetected.ForeColor = folderExists ? Green : Amber;

        _pakDetected.Text = pakExists ? "✓ res.pak detected" : "⚠ res.pak not detected";
        _pakDetected.ForeColor = pakExists ? Green : Amber;
    }

    private bool EnsureGameSelected()
    {
        if (_gameDirectory is not null && LauncherCore.IsValidGameDirectory(_gameDirectory)) return true;
        MessageBox.Show(this, "Select your Wartales installation folder containing res.pak first.", "Wartales folder required", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    private void UpdateEnabledState()
    {
        var gameOk = !_busy && _gameDirectory is not null && LauncherCore.IsValidGameDirectory(_gameDirectory);
        _changeFolder.Enabled = !_busy;
        _install.Enabled = gameOk;
        _verify.Enabled = gameOk;
        _restore.Enabled = gameOk;
        _uninstall.Enabled = gameOk && _mods.SelectedItems.Count == 1;
    }

    private void SetStatus(string text, StatusKind kind)
    {
        _status.Text = text;
        _status.ForeColor = kind == StatusKind.Error ? Red : TextMuted;
        _statusDot.BackColor = kind switch
        {
            StatusKind.Success or StatusKind.Ready => Green,
            StatusKind.Warning or StatusKind.Busy => Amber,
            StatusKind.Error => Red,
            _ => TextMuted
        };
    }

    private void ShowError(string message)
    {
        MessageBox.Show(this, message, "JACKOB's Wartales Mod Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private enum StatusKind
    {
        Ready,
        Success,
        Warning,
        Busy,
        Error
    }
}

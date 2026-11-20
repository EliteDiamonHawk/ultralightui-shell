using System.Drawing;
using System.Windows.Forms;

namespace Chronos;

internal sealed record ShellUiSnapshot(
    RendererCapabilities Capabilities,
    bool Connected,
    bool TrayEnabled,
    bool TrayVisible,
    bool WindowsKeyOverrideEnabled,
    bool EscapePanicEnabled,
    string ThemeOrigin,
    string? RendererFailure,
    bool StatusPollingEnabled,
    bool Exiting,
    PersistentSettings Settings);

[Flags]
internal enum ShellRenderTarget
{
    None = 0,
    Status = 1,
    Renderer = 2,
    Themes = 4,
    Options = 8,
    All = Status | Renderer | Themes | Options
}

internal sealed class ShellForm : Form
{
    private readonly ChronosHostContext _host;
    private readonly ListBox _themes = new();
    private readonly ListBox _groups = new();
    private readonly ListBox _windows = new();
    private readonly RichTextBox _themeFiles = new();
    private readonly RichTextBox _activeThemeFiles = new();
    private readonly RichTextBox _statusOutput = new();
    private readonly Label _connection = new();
    private readonly Label _trayStatus = new();
    private readonly Label _windowsKeyOverrideStatus = new();
    private readonly Label _polling = new();
    private readonly Label _themeOrigin = new();
    private readonly CheckBox _trayEnabled = new();
    private readonly CheckBox _windowsKeyOverride = new();
    private readonly CheckBox _escapePanic = new();
    private readonly CheckBox _statusPollingEnabled = new();
    private readonly CheckBox _restoreState = new();
    private readonly TextBox _rendererPath = new();
    private readonly TextBox _ultralightRoot = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1500 };
    private readonly List<ThemeDefinition> _themeItems = new();
    private readonly List<RendererWindowCapability> _windowItems = new();
    private readonly List<string> _groupItems = new();
    private bool _updating;
    private bool _refreshInFlight;
    private bool _allowClose;

    public ShellForm(ChronosHostContext host)
    {
        _host = host;
        Text = "KhaosShell";
        Width = 900;
        Height = 700;
        MinimumSize = new Size(640, 460);
        StartPosition = FormStartPosition.CenterScreen;
        FormClosing += OnFormClosing;

        BuildUi();
        _host.UiStateChanged += OnHostStateChanged;
        _refreshTimer.Tick += async (_, _) => await RefreshAsync(false);
        if (_host.StatusPollingEnabled)
            _refreshTimer.Start();
        Render(_host.GetUiSnapshot(), ShellRenderTarget.All);
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _host.UiStateChanged -= OnHostStateChanged;
        }
        base.Dispose(disposing);
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), RowCount = 2, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var status = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 2 };
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        AddStatusCell(status, 0, 0, "Connection", _connection);
        AddStatusCell(status, 2, 0, "Tray", _trayStatus);
        AddStatusCell(status, 0, 1, "Windows Key Override", _windowsKeyOverrideStatus);
        AddStatusCell(status, 2, 1, "Polling", _polling);
        _themeOrigin.Dock = DockStyle.Fill;
        _themeOrigin.TextAlign = ContentAlignment.MiddleLeft;
        status.Controls.Add(_themeOrigin, 4, 0);
        status.SetRowSpan(_themeOrigin, 2);
        root.Controls.Add(status, 0, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(new TabPage("Themes") { Padding = new Padding(6), Controls = { BuildThemesPage() } });
        tabs.TabPages.Add(new TabPage("Aether commands") { Padding = new Padding(6), Controls = { BuildCommandsPage() } });
        tabs.TabPages.Add(new TabPage("Status") { Padding = new Padding(6), Controls = { BuildStatusPage() } });
        tabs.TabPages.Add(new TabPage("Options") { Padding = new Padding(6), Controls = { BuildOptionsPage() } });
        root.Controls.Add(tabs, 0, 1);
    }

    private Control BuildThemesPage()
    {
        var split = CreateVerticalSplitContainer(250, 170, 260);
        _themes.Dock = DockStyle.Fill;
        _themes.SelectedIndexChanged += (_, _) => RenderSelectedTheme();
        split.Panel1.Controls.Add(_themes);

        var details = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        ConfigureReader(_themeFiles);
        details.Controls.Add(_themeFiles, 0, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true };
        buttons.Controls.Add(Button("Import theme", async (_, _) => await ImportThemeAsync()));
        buttons.Controls.Add(Button("Load", async (_, _) => await LoadThemeAsync()));
        buttons.Controls.Add(Button("Unload", async (_, _) => await UnloadThemeAsync()));
        buttons.Controls.Add(Button("Rename", (_, _) => RenameTheme()));
        buttons.Controls.Add(Button("Remove", (_, _) => RemoveTheme()));
        details.Controls.Add(buttons, 0, 1);
        split.Panel2.Controls.Add(details);
        return split;
    }

    private Control BuildCommandsPage()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        var menuButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true };
        menuButtons.Controls.Add(Button("Show menu", async (_, _) => await RunAsync(_host.ShowMenuAsync)));
        menuButtons.Controls.Add(Button("Hide menu", async (_, _) => await RunAsync(_host.HideMenuAsync)));
        menuButtons.Controls.Add(Button("Toggle menu", async (_, _) => await RunAsync(_host.ToggleMenuAsync)));
        menuButtons.Controls.Add(Button("Refresh", async (_, _) => await RefreshAsync(true)));
        layout.Controls.Add(menuButtons, 0, 0);

        layout.Controls.Add(BuildGroupPanel(), 0, 1);
        layout.Controls.Add(BuildWindowPanel(), 0, 2);
        return layout;
    }

    private Control BuildGroupPanel()
    {
        var box = new GroupBox { Text = "Groups", Dock = DockStyle.Fill };
        var split = CreateVerticalSplitContainer(260, 150, 180);
        _groups.Dock = DockStyle.Fill;
        split.Panel1.Controls.Add(_groups);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true };
        buttons.Controls.Add(Button("Pause", async (_, _) => await RunGroupAsync(_host.PauseAsync)));
        buttons.Controls.Add(Button("Resume", async (_, _) => await RunGroupAsync(_host.ResumeAsync)));
        buttons.Controls.Add(Button("Reload", async (_, _) => await RunGroupAsync(_host.ReloadAsync)));
        buttons.Controls.Add(Button("Status", async (_, _) => await RunGroupAsync(_host.StatusAsync)));
        buttons.Controls.Add(Button("Pause all", async (_, _) => await RunAsync(() => _host.PauseAsync(null, true))));
        buttons.Controls.Add(Button("Resume all", async (_, _) => await RunAsync(() => _host.ResumeAsync(null, true))));
        buttons.Controls.Add(Button("Reload all", async (_, _) => await RunAsync(() => _host.ReloadAsync(null, true))));
        buttons.Controls.Add(Button("Status all", async (_, _) => await RunAsync(() => _host.StatusAsync(null, true))));
        split.Panel2.Controls.Add(buttons);
        box.Controls.Add(split);
        return box;
    }

    private Control BuildWindowPanel()
    {
        var box = new GroupBox { Text = "Windows", Dock = DockStyle.Fill };
        var split = CreateVerticalSplitContainer(420, 180, 120);
        _windows.Dock = DockStyle.Fill;
        split.Panel1.Controls.Add(_windows);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true };
        buttons.Controls.Add(Button("Show", async (_, _) => await SetWindowAsync(true)));
        buttons.Controls.Add(Button("Hide", async (_, _) => await SetWindowAsync(false)));
        split.Panel2.Controls.Add(buttons);
        box.Controls.Add(split);
        return box;
    }

    private static SplitContainer CreateVerticalSplitContainer(int splitterDistance, int panel1MinSize, int panel2MinSize)
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };

        void ApplyLayout(object? sender, EventArgs e)
        {
            var maxSplitterDistance = split.Width - panel2MinSize;
            if (maxSplitterDistance < panel1MinSize)
                return;

            split.Panel1MinSize = panel1MinSize;
            split.Panel2MinSize = panel2MinSize;
            split.SplitterDistance = Math.Clamp(splitterDistance, panel1MinSize, maxSplitterDistance);
            split.SizeChanged -= ApplyLayout;
        }

        split.SizeChanged += ApplyLayout;
        ApplyLayout(split, EventArgs.Empty);
        return split;
    }

    private Control BuildStatusPage()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        layout.Controls.Add(new Label { Text = "Active theme files", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        ConfigureReader(_activeThemeFiles);
        ConfigureReader(_statusOutput);
        layout.Controls.Add(_activeThemeFiles, 0, 1);
        layout.Controls.Add(_statusOutput, 0, 2);
        return layout;
    }

    private Control BuildOptionsPage()
    {
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8) };
        _trayEnabled.Text = "Tray enabled";
        _windowsKeyOverride.Text = "Windows Key Override";
        _escapePanic.Text = "Panic Escape enabled";
        _statusPollingEnabled.Text = "Status polling enabled";
        _restoreState.Text = "Restore theme state on launch";
        _trayEnabled.AutoSize = true;
        _windowsKeyOverride.AutoSize = true;
        _escapePanic.AutoSize = true;
        _statusPollingEnabled.AutoSize = true;
        _restoreState.AutoSize = true;
        _trayEnabled.CheckedChanged += (_, _) =>
        {
            if (!_updating)
                RunSync(() => _host.SetTrayVisible(_trayEnabled.Checked));
        };
        _windowsKeyOverride.CheckedChanged += (_, _) =>
        {
            if (!_updating)
                RunSync(() => _host.SetCurrentWinToggle(_windowsKeyOverride.Checked));
        };
        _escapePanic.CheckedChanged += (_, _) =>
        {
            if (_updating)
                return;
            if (!_escapePanic.Checked && MessageBox.Show(
                    this,
                    "Disable Panic Escape? Rapid Escape presses will no longer disable Windows Key Override.",
                    "KhaosShell",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
            {
                SetEscapePanicCheckbox(true);
                return;
            }

            if (!RunSync(() => _host.SetEscapePanicEnabled(_escapePanic.Checked)))
                Render(_host.GetUiSnapshot(), ShellRenderTarget.Options);
        };
        _statusPollingEnabled.CheckedChanged += (_, _) =>
        {
            if (!_updating)
                RunSync(() => _host.SetStatusPolling(_statusPollingEnabled.Checked));
        };
        _restoreState.CheckedChanged += (_, _) =>
        {
            if (!_updating)
                RunSync(() => _host.SetRestoreState(_restoreState.Checked));
        };
        _rendererPath.Dock = DockStyle.Top;
        _rendererPath.Width = 520;
        _rendererPath.PlaceholderText = "Default Aether discovery";
        var rendererButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        rendererButtons.Controls.Add(new Label { Text = "Renderer path", AutoSize = true, Padding = new Padding(0, 7, 8, 0) });
        rendererButtons.Controls.Add(_rendererPath);
        rendererButtons.Controls.Add(Button("Browse", (_, _) => BrowseRendererPath()));
        rendererButtons.Controls.Add(Button("Save", (_, _) => SaveRendererPath()));
        _ultralightRoot.Width = 520;
        _ultralightRoot.PlaceholderText = "Aether default resolution";
        var ultralightButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        ultralightButtons.Controls.Add(new Label { Text = "Ultralight root", AutoSize = true, Padding = new Padding(0, 7, 8, 0) });
        ultralightButtons.Controls.Add(_ultralightRoot);
        ultralightButtons.Controls.Add(Button("Browse", (_, _) => BrowseUltralightRoot()));
        ultralightButtons.Controls.Add(Button("Save", (_, _) => SaveUltralightRoot()));
        options.Controls.Add(_trayEnabled);
        options.Controls.Add(_windowsKeyOverride);
        options.Controls.Add(_escapePanic);
        options.Controls.Add(_statusPollingEnabled);
        options.Controls.Add(_restoreState);
        options.Controls.Add(rendererButtons);
        options.Controls.Add(ultralightButtons);
        options.Controls.Add(new Label
        {
            Text = "Optional Aether Ultralight install root. Empty passes no root and uses Aether default resolution.",
            AutoSize = true,
            Margin = new Padding(3, 0, 3, 3)
        });
        options.Controls.Add(Button("Exit KhaosShell", (_, _) => _host.RequestExitFromUi()));
        options.Controls.Add(new Label
        {
            Text = "Restore theme state applies at launch. Current theme origin appears in status.",
            AutoSize = true,
            Margin = new Padding(3, 12, 3, 3)
        });
        return options;
    }

    private static void ConfigureReader(RichTextBox reader)
    {
        reader.Dock = DockStyle.Fill;
        reader.ReadOnly = true;
        reader.WordWrap = false;
        reader.ScrollBars = RichTextBoxScrollBars.Both;
        reader.BackColor = SystemColors.Window;
    }

    private static void AddStatusCell(TableLayoutPanel table, int column, int row, string name, Label value)
    {
        table.Controls.Add(new Label { Text = name, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, column, row);
        value.Dock = DockStyle.Fill;
        value.TextAlign = ContentAlignment.MiddleLeft;
        table.Controls.Add(value, column + 1, row);
    }

    private static Button Button(string text, EventHandler click)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 28, Margin = new Padding(3) };
        button.Click += click;
        return button;
    }

    private async Task ImportThemeAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import theme",
            Filter = "JSON theme files (*.json)|*.json|All files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
            InitialDirectory = _host.ThemesDirectory
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        string? name = PromptForName("Import theme", Path.GetFileNameWithoutExtension(dialog.FileNames[0]));
        if (name == null)
            return;
        try
        {
            _host.ImportTheme(name, dialog.FileNames);
            Render(_host.GetUiSnapshot(), ShellRenderTarget.Themes);
            SetOutput("Theme registered. Select it and press Load.");
        }
        catch (Exception ex) { SetOutput(ex.Message); }
        await Task.CompletedTask;
    }

    private async Task LoadThemeAsync()
    {
        ThemeDefinition? theme = SelectedTheme();
        if (theme == null)
        {
            SetOutput("Select a theme first.");
            return;
        }
        await RunAsync(() => _host.ApplyThemeAsync(theme));
    }

    private async Task UnloadThemeAsync() => await RunAsync(() => _host.UnloadThemeAsync());

    private void RenameTheme()
    {
        ThemeDefinition? theme = SelectedTheme();
        if (theme == null)
        {
            SetOutput("Select a theme first.");
            return;
        }
        string? name = PromptForName("Rename theme", theme.Name);
        if (name == null)
            return;
        RunSync(() => _host.RenameTheme(theme, name));
        Render(_host.GetUiSnapshot(), ShellRenderTarget.Themes);
    }

    private void RemoveTheme()
    {
        ThemeDefinition? theme = SelectedTheme();
        if (theme == null)
        {
            SetOutput("Select a theme first.");
            return;
        }
        if (MessageBox.Show(this, $"Remove '{theme.Name}' from register?", "KhaosShell", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        RunSync(() => _host.RemoveTheme(theme));
        Render(_host.GetUiSnapshot(), ShellRenderTarget.Themes);
    }

    private async Task RunGroupAsync(Func<string?, bool, Task<AetherCommandResult>> action)
    {
        string? group = _groups.SelectedIndex >= 0 && _groups.SelectedIndex < _groupItems.Count ? _groupItems[_groups.SelectedIndex] : null;
        if (group == null)
        {
            SetOutput("Select a group first.");
            return;
        }
        await RunAsync(() => action(group, false));
    }

    private async Task SetWindowAsync(bool visible)
    {
        if (_windows.SelectedIndex < 0 || _windows.SelectedIndex >= _windowItems.Count)
        {
            SetOutput("Select a window first.");
            return;
        }
        await RunAsync(() => _host.SetWindowVisibilityAsync(_windowItems[_windows.SelectedIndex].Id, visible));
    }

    private async Task RunAsync(Func<Task<AetherCommandResult>> action)
    {
        try
        {
            AetherCommandResult result = await action();
            SetOutput(result.Success ? result.Payload : result.Error ?? result.Payload);
            if (result.Success)
                await RefreshAsync(false);
        }
        catch (Exception ex) { SetOutput(ex.Message); }
    }

    private bool RunSync(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            SetOutput(ex.Message);
            return false;
        }
    }

    private async Task RefreshAsync(bool showResult)
    {
        if (_refreshInFlight || IsDisposed)
            return;
        _refreshInFlight = true;
        try
        {
            await _host.RefreshUiAsync();
            Render(_host.GetUiSnapshot(), ShellRenderTarget.Status | ShellRenderTarget.Renderer);
            if (showResult)
                SetOutput("Status refreshed.");
        }
        catch (Exception ex) { SetOutput(ex.Message); }
        finally { _refreshInFlight = false; }
    }

    private void Render(ShellUiSnapshot snapshot, ShellRenderTarget target)
    {
        if (IsDisposed)
            return;
        _updating = true;
        try
        {
            if (target.HasFlag(ShellRenderTarget.Status))
            {
                _connection.Text = snapshot.RendererFailure is { Length: > 0 }
                    ? $"Failed: {snapshot.RendererFailure}"
                    : snapshot.Connected ? $"Connected ({snapshot.Capabilities.PipeState})" : "Disconnected";
                _trayStatus.Text = snapshot.TrayEnabled ? (snapshot.TrayVisible ? "Enabled / visible" : "Enabled / hidden") : "Disabled";
                _windowsKeyOverrideStatus.Text = snapshot.WindowsKeyOverrideEnabled ? "Enabled" : "Disabled";
                _polling.Text = snapshot.StatusPollingEnabled ? "Enabled" : "Disabled (--status-poll false; events active)";
                _themeOrigin.Text = $"Theme origin: {snapshot.ThemeOrigin}";
                if (snapshot.RendererFailure is { Length: > 0 })
                    _statusOutput.Text = snapshot.RendererFailure;
                if (snapshot.StatusPollingEnabled && !_refreshTimer.Enabled)
                    _refreshTimer.Start();
                else if (!snapshot.StatusPollingEnabled && _refreshTimer.Enabled)
                    _refreshTimer.Stop();
            }

            if (target.HasFlag(ShellRenderTarget.Renderer))
            {
                _activeThemeFiles.Text = snapshot.Capabilities.ConfigFiles.Count == 0
                    ? "(none)"
                    : string.Join(Environment.NewLine, snapshot.Capabilities.ConfigFiles);
                UpdateList(_groups, _groupItems, snapshot.Capabilities.Groups);
                UpdateList(_windows, _windowItems, snapshot.Capabilities.Windows,
                    window => $"{window.Id} [{window.Kind ?? "window"}] {(window.Visible == true ? "visible" : "hidden/unknown")}");
            }

            if (target.HasFlag(ShellRenderTarget.Themes))
            {
                Guid? selectedThemeId = SelectedTheme()?.Id;
                _themeItems.Clear();
                _themeItems.AddRange(_host.Themes);
                _themes.Items.Clear();
                foreach (ThemeDefinition theme in _themeItems)
                    _themes.Items.Add(FormatTheme(theme));
                if (selectedThemeId is { } themeId)
                {
                    int selectedIndex = _themeItems.FindIndex(theme => theme.Id == themeId);
                    if (selectedIndex >= 0)
                        _themes.SelectedIndex = selectedIndex;
                }
                RenderSelectedTheme();
            }

            if (target.HasFlag(ShellRenderTarget.Options))
            {
                _trayEnabled.Enabled = snapshot.TrayEnabled;
                _trayEnabled.Checked = snapshot.TrayVisible;
                _windowsKeyOverride.Checked = snapshot.WindowsKeyOverrideEnabled;
                SetEscapePanicCheckbox(snapshot.EscapePanicEnabled);
                _statusPollingEnabled.Checked = snapshot.Settings.StatusPollingEnabled;
                _restoreState.Checked = snapshot.Settings.RestoreState;
                if (!_rendererPath.Focused && !_rendererPath.Modified)
                {
                    _rendererPath.Text = snapshot.Settings.RendererPath ?? string.Empty;
                    _rendererPath.Modified = false;
                }
                if (!_ultralightRoot.Focused && !_ultralightRoot.Modified)
                {
                    _ultralightRoot.Text = snapshot.Settings.UltralightRoot ?? string.Empty;
                    _ultralightRoot.Modified = false;
                }
            }
        }
        finally { _updating = false; }
    }

    private static void UpdateList<T>(ListBox list, List<T> items, IReadOnlyList<T> next, Func<T, string>? format = null)
    {
        if (items.SequenceEqual(next))
            return;

        int selectedIndex = list.SelectedIndex;
        items.Clear();
        items.AddRange(next);
        list.Items.Clear();
        foreach (T item in items)
        {
            object display = format == null ? item! : format(item);
            list.Items.Add(display);
        }

        if (selectedIndex >= 0 && selectedIndex < list.Items.Count)
            list.SelectedIndex = selectedIndex;
    }

    private void RenderSelectedTheme()
    {
        ThemeDefinition? theme = SelectedTheme();
        _themeFiles.Text = theme == null ? "Select a theme to view its files." : string.Join(Environment.NewLine, theme.Files);
    }

    private ThemeDefinition? SelectedTheme() =>
        _themes.SelectedIndex >= 0 && _themes.SelectedIndex < _themeItems.Count ? _themeItems[_themes.SelectedIndex] : null;

    private static string FormatTheme(ThemeDefinition theme) =>
        $"{theme.Name} ({theme.Files.Count} file{(theme.Files.Count == 1 ? "" : "s")})";

    private void OnHostStateChanged()
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            try { BeginInvoke((Action)(() => Render(_host.GetUiSnapshot(), ShellRenderTarget.Status | ShellRenderTarget.Renderer))); } catch (InvalidOperationException) { }
            return;
        }
        Render(_host.GetUiSnapshot(), ShellRenderTarget.Status | ShellRenderTarget.Renderer);
    }

    private void SetOutput(string text) => _statusOutput.Text = text;

    private void SetEscapePanicCheckbox(bool value)
    {
        bool wasUpdating = _updating;
        _updating = true;
        try { _escapePanic.Checked = value; }
        finally { _updating = wasUpdating; }
    }

    private void BrowseRendererPath()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select Aether renderer",
            Filter = "Aether executable (Aether.exe)|Aether.exe|Executable (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            FileName = _rendererPath.Text
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _rendererPath.Text = dialog.FileName;
    }

    private void SaveRendererPath()
    {
        if (RunSync(() => _host.SetRendererPath(_rendererPath.Text)))
        {
            _rendererPath.Modified = false;
            Render(_host.GetUiSnapshot(), ShellRenderTarget.Options);
        }
    }

    private void BrowseUltralightRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select Ultralight install root",
            ShowNewFolderButton = false
        };
        if (Directory.Exists(_ultralightRoot.Text.Trim()))
            dialog.SelectedPath = _ultralightRoot.Text.Trim();
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _ultralightRoot.Text = dialog.SelectedPath;
    }

    private void SaveUltralightRoot()
    {
        if (RunSync(() => _host.SetUltralightRoot(_ultralightRoot.Text)))
        {
            _ultralightRoot.Modified = false;
            Render(_host.GetUiSnapshot(), ShellRenderTarget.Options);
        }
    }

    private static string? PromptForName(string title, string initial)
    {
        using var dialog = new Form { Text = title, Width = 380, Height = 140, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
        var text = new TextBox { Left = 12, Top = 12, Width = 340, Text = initial };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 190, Top = 48, Width = 76 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 276, Top = 48, Width = 76 };
        dialog.Controls.AddRange(new Control[] { text, ok, cancel });
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(text.Text) ? text.Text.Trim() : null;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }
}

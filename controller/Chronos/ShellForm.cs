using System.Drawing;
using System.Windows.Forms;

namespace Chronos;

internal sealed record ShellUiSnapshot(
    RendererCapabilities Capabilities,
    bool Connected,
    bool TrayEnabled,
    bool WinMenuEnabled,
    WrapperSettings Settings,
    bool StatusPollingEnabled,
    bool Exiting);

internal sealed class ShellForm : Form
{
    private readonly ChronosHostContext _host;
    private readonly ListBox _themes = new();
    private readonly ListBox _groups = new();
    private readonly ListBox _windows = new();
    private readonly TextBox _configFiles = new();
    private readonly TextBox _output = new();
    private readonly Label _connection = new();
    private readonly Label _tray = new();
    private readonly Label _winMenu = new();
    private readonly Label _polling = new();
    private readonly CheckBox _currentWin = new();
    private readonly CheckBox _nextNoTray = new();
    private readonly CheckBox _nextNoLastConfig = new();
    private readonly CheckBox _nextWinMenu = new();
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
        Width = 760;
        Height = 620;
        MinimumSize = new Size(680, 500);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        _host.UiStateChanged += OnHostStateChanged;
        _refreshTimer.Tick += async (_, _) => await RefreshAsync(false);
        if (_host.StatusPollingEnabled)
            _refreshTimer.Start();
        Render(_host.GetUiSnapshot());
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
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), RowCount = 5, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 122));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        Controls.Add(root);

        var status = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, AutoSize = true };
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddStatusRow(status, 0, "Connection", _connection);
        AddStatusRow(status, 1, "Tray", _tray);
        AddStatusRow(status, 2, "Win-key", _winMenu);
        AddStatusRow(status, 3, "Status polling", _polling);
        root.Controls.Add(status, 0, 0);

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        root.Controls.Add(content, 0, 1);

        var themeBox = new GroupBox { Text = "Themes / config register", Dock = DockStyle.Fill };
        var themeLayout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(6), RowCount = 2 };
        themeLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        themeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        _themes.Dock = DockStyle.Fill;
        themeLayout.Controls.Add(_themes, 0, 0);
        var themeButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        themeButtons.Controls.Add(Button("Import", async (_, _) => await ImportThemeAsync()));
        themeButtons.Controls.Add(Button("Apply", async (_, _) => await ApplyThemeAsync()));
        themeButtons.Controls.Add(Button("Rename", (_, _) => RenameTheme()));
        themeButtons.Controls.Add(Button("Remove", (_, _) => RemoveTheme()));
        themeLayout.Controls.Add(themeButtons, 0, 1);
        themeBox.Controls.Add(themeLayout);
        content.Controls.Add(themeBox, 0, 0);

        var commandBox = new GroupBox { Text = "Aether commands", Dock = DockStyle.Fill };
        var commandLayout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(6), RowCount = 4 };
        commandLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        commandLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        commandLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        commandLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        var menuButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        menuButtons.Controls.Add(Button("Show menu", async (_, _) => await RunAsync(_host.ShowMenuAsync)));
        menuButtons.Controls.Add(Button("Hide menu", async (_, _) => await RunAsync(_host.HideMenuAsync)));
        menuButtons.Controls.Add(Button("Toggle menu", async (_, _) => await RunAsync(_host.ToggleMenuAsync)));
        commandLayout.Controls.Add(menuButtons, 0, 0);

        var groupBox = new GroupBox { Text = "Groups", Dock = DockStyle.Fill };
        var groupLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        groupLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        groupLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        _groups.Dock = DockStyle.Fill;
        groupLayout.Controls.Add(_groups, 0, 0);
        var groupButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        groupButtons.Controls.Add(Button("Pause", async (_, _) => await RunGroupAsync(_host.PauseAsync)));
        groupButtons.Controls.Add(Button("Resume", async (_, _) => await RunGroupAsync(_host.ResumeAsync)));
        groupButtons.Controls.Add(Button("Reload", async (_, _) => await RunGroupAsync(_host.ReloadAsync)));
        groupButtons.Controls.Add(Button("Status", async (_, _) => await RunGroupAsync(_host.StatusAsync)));
        groupButtons.Controls.Add(Button("All: pause / resume / reload / status", async (_, _) => await RunAllCommandsAsync()));
        groupLayout.Controls.Add(groupButtons, 1, 0);
        groupBox.Controls.Add(groupLayout);
        commandLayout.Controls.Add(groupBox, 0, 1);

        var windowBox = new GroupBox { Text = "Windows", Dock = DockStyle.Fill };
        var windowLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        windowLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        windowLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        _windows.Dock = DockStyle.Fill;
        windowLayout.Controls.Add(_windows, 0, 0);
        var windowButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        windowButtons.Controls.Add(Button("Show", async (_, _) => await SetWindowAsync(true)));
        windowButtons.Controls.Add(Button("Hide", async (_, _) => await SetWindowAsync(false)));
        windowLayout.Controls.Add(windowButtons, 1, 0);
        windowBox.Controls.Add(windowLayout);
        commandLayout.Controls.Add(windowBox, 0, 2);

        var commandBottom = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        commandBottom.Controls.Add(Button("Refresh", async (_, _) => await RefreshAsync(true)));
        commandBottom.Controls.Add(Button("Exit KhaosShell", (_, _) => _host.RequestExitFromUi()));
        commandLayout.Controls.Add(commandBottom, 0, 3);
        commandBox.Controls.Add(commandLayout);
        content.Controls.Add(commandBox, 1, 0);

        var configBox = new GroupBox { Text = "Current config", Dock = DockStyle.Fill };
        _configFiles.Multiline = true;
        _configFiles.ReadOnly = true;
        _configFiles.ScrollBars = ScrollBars.Vertical;
        _configFiles.Dock = DockStyle.Fill;
        configBox.Controls.Add(_configFiles);
        root.Controls.Add(configBox, 0, 2);

        var settingsBox = new GroupBox { Text = "Next start / current Win-key", Dock = DockStyle.Fill };
        var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(6), WrapContents = false };
        _nextNoTray.Text = "No tray next start";
        _nextNoLastConfig.Text = "No last config next start";
        _nextWinMenu.Text = "Win-key menu next start";
        _currentWin.Text = "Enable Win-key now";
        foreach (CheckBox box in new[] { _nextNoTray, _nextNoLastConfig, _nextWinMenu, _currentWin })
        {
            box.AutoSize = true;
            box.Margin = new Padding(3, 5, 12, 3);
            settings.Controls.Add(box);
        }
        _nextNoTray.CheckedChanged += (_, _) => UpdateStartupSettings();
        _nextNoLastConfig.CheckedChanged += (_, _) => UpdateStartupSettings();
        _nextWinMenu.CheckedChanged += (_, _) => UpdateStartupSettings();
        _currentWin.CheckedChanged += (_, _) =>
        {
            if (!_updating)
                RunSync(() => _host.SetCurrentWinToggle(_currentWin.Checked));
        };
        settingsBox.Controls.Add(settings);
        root.Controls.Add(settingsBox, 0, 3);

        _output.Multiline = true;
        _output.ReadOnly = true;
        _output.ScrollBars = ScrollBars.Vertical;
        _output.Height = 60;
        _output.Dock = DockStyle.Fill;
        root.Controls.Add(_output, 0, 4);
    }

    private void AddStatusRow(TableLayoutPanel table, int row, string name, Label value)
    {
        table.Controls.Add(new Label { Text = name, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        value.Dock = DockStyle.Fill;
        value.TextAlign = ContentAlignment.MiddleLeft;
        table.Controls.Add(value, 1, row);
    }

    private static Button Button(string text, EventHandler click)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 26 };
        button.Click += click;
        return button;
    }

    private async Task ImportThemeAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import KhaosShell theme/config",
            Filter = "JSON configuration (*.json)|*.json|All files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        string suggested = Path.GetFileNameWithoutExtension(dialog.FileNames[0]);
        string? name = PromptForName("Import theme", suggested);
        if (name == null)
            return;
        try
        {
            _host.ImportTheme(name, dialog.FileNames);
            Render(_host.GetUiSnapshot());
            SetOutput("Theme registered. Select it and press Apply.");
        }
        catch (Exception ex) { SetOutput(ex.Message); }
        await Task.CompletedTask;
    }

    private async Task ApplyThemeAsync()
    {
        ThemeDefinition? theme = SelectedTheme();
        if (theme == null)
        {
            SetOutput("Select a theme first.");
            return;
        }
        await RunAsync(() => _host.ApplyThemeAsync(theme));
    }

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
        Render(_host.GetUiSnapshot());
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
        Render(_host.GetUiSnapshot());
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

    private async Task RunAllCommandsAsync()
    {
        foreach (Func<Task<AetherCommandResult>> action in new[]
        {
            () => _host.PauseAsync(null, true),
            () => _host.ResumeAsync(null, true),
            () => _host.ReloadAsync(null, true),
            () => _host.StatusAsync(null, true)
        })
            await RunAsync(action);
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

    private void RunSync(Action action)
    {
        try { action(); }
        catch (Exception ex) { SetOutput(ex.Message); }
    }

    private async Task RefreshAsync(bool showResult)
    {
        if (_refreshInFlight || IsDisposed)
            return;
        _refreshInFlight = true;
        try
        {
            await _host.RefreshUiAsync();
            Render(_host.GetUiSnapshot());
            if (showResult)
                SetOutput("Status refreshed.");
        }
        catch (Exception ex)
        {
            SetOutput(ex.Message);
        }
        finally { _refreshInFlight = false; }
    }

    private void UpdateStartupSettings()
    {
        if (_updating)
            return;
        RunSync(() => _host.UpdateNextStartSettings(_nextNoTray.Checked, _nextNoLastConfig.Checked, _nextWinMenu.Checked));
    }

    private void Render(ShellUiSnapshot snapshot)
    {
        if (IsDisposed)
            return;
        _updating = true;
        try
        {
            _connection.Text = snapshot.Connected ? $"Connected ({snapshot.Capabilities.PipeState})" : "Disconnected";
            _tray.Text = snapshot.TrayEnabled ? "Enabled" : "Disabled (--no-tray)";
            _winMenu.Text = snapshot.WinMenuEnabled ? "Enabled" : "Disabled";
            _polling.Text = snapshot.StatusPollingEnabled ? "Enabled" : "Disabled (--no-status-poll; events active)";
            _configFiles.Text = snapshot.Capabilities.ConfigFiles.Count == 0
                ? "(none)"
                : string.Join(Environment.NewLine, snapshot.Capabilities.ConfigFiles);

            _themeItems.Clear();
            _themeItems.AddRange(_host.Themes);
            _themes.Items.Clear();
            foreach (ThemeDefinition theme in _themeItems)
                _themes.Items.Add(FormatTheme(theme));

            _groupItems.Clear();
            _groupItems.AddRange(snapshot.Capabilities.Groups);
            _groups.Items.Clear();
            foreach (string group in _groupItems)
                _groups.Items.Add(group);

            _windowItems.Clear();
            _windowItems.AddRange(snapshot.Capabilities.Windows);
            _windows.Items.Clear();
            foreach (RendererWindowCapability window in _windowItems)
                _windows.Items.Add($"{window.Id} [{window.Kind ?? "window"}] {(window.Visible == true ? "visible" : "hidden/unknown")}");

            _currentWin.Checked = snapshot.WinMenuEnabled;
            _nextNoTray.Checked = snapshot.Settings.NoTrayNextStart;
            _nextNoLastConfig.Checked = snapshot.Settings.NoLastConfigNextStart;
            _nextWinMenu.Checked = snapshot.Settings.WinMenuToggleEnabled;
        }
        finally { _updating = false; }
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
            try { BeginInvoke((Action)(() => Render(_host.GetUiSnapshot()))); } catch (InvalidOperationException) { }
            return;
        }
        Render(_host.GetUiSnapshot());
    }

    private void SetOutput(string text) => _output.Text = text;

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

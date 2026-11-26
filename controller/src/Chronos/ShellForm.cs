using System.Drawing;
using System.Windows.Forms;
using KhaosShell.Contracts;

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
    Keybinds = 16,
    Backends = 32,
    All = Status | Renderer | Themes | Options | Keybinds | Backends
}

internal sealed class ShellForm : Form
{
    private readonly ChronosHostContext _host;
    private readonly ListBox _themes = new();
    private readonly ListBox _backendList = new();
    private readonly ListBox _groups = new();
    private readonly ListBox _windows = new();
    private readonly CheckedListBox _keybindList = new() { CheckOnClick = true };
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
    private readonly ComboBox _windowsKeyHideMode = new();
    private readonly TextBox _windowsKeyHideCue = new();
    private readonly CheckBox _windowsKeyShowCueEnabled = new();
    private readonly TextBox _windowsKeyShowCue = new();
    private readonly CheckBox _escapePanic = new();
    private readonly CheckBox _statusPollingEnabled = new();
    private readonly CheckBox _restoreState = new();
    private readonly TextBox _rendererPath = new();
    private readonly TextBox _ultralightRoot = new();
    private readonly TextBox _backendId = new();
    private readonly TextBox _backendExecutable = new();
    private readonly TextBox _keybindGesture = new();
    private readonly CheckedListBox _keybindCueList = new() { CheckOnClick = true };
    private readonly TextBox _keybindGroup = new();
    private readonly TextBox _keybindTarget = new();
    private readonly TextBox _keybindTopic = new();
    private readonly TextBox _keybindConfigPaths = new();
    private readonly CheckBox _keybindOverride = new();
    private readonly CheckBox _keybindAll = new();
    private readonly ComboBox _keybindActionKind = new();
    private readonly ComboBox _keybindCommand = new();
    private readonly ComboBox _keybindMenuAction = new();
    private readonly ComboBox _keybindTrayAction = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1500 };
    private readonly List<ThemeDefinition> _themeItems = new();
    private readonly List<string> _backendItems = new();
    private readonly List<RendererWindowCapability> _windowItems = new();
    private readonly List<string> _groupItems = new();
    private readonly List<KeybindDefinition> _keybindItems = new();
    private readonly List<KeybindDefinition> _visibleKeybindItems = new();
    private readonly List<CueOption> _cueOptions = new();
    private KeybindDefinition? _editingKeybind;
    private bool _capturingGesture;
    private bool _keybindEditorDirty;
    private bool _updating;
    private bool _refreshInFlight;
    private bool _allowClose;

    private sealed record CueOption(string Name, bool Current)
    {
        public override string ToString() => Current ? Name : $"{Name} (archived)";
    }

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
        var themesTab = new TabPage("Themes") { Padding = new Padding(6), Controls = { BuildThemesPage() } };
        var backendsTab = new TabPage("Backends") { Padding = new Padding(6), Controls = { BuildBackendsPage() } };
        var keybindsTab = new TabPage("Keybinds") { Padding = new Padding(6), Controls = { BuildKeybindsPage() } };
        tabs.TabPages.Add(themesTab);
        tabs.TabPages.Add(backendsTab);
        tabs.TabPages.Add(keybindsTab);
        tabs.TabPages.Add(new TabPage("Aether commands") { Padding = new Padding(6), Controls = { BuildCommandsPage() } });
        tabs.TabPages.Add(new TabPage("Status") { Padding = new Padding(6), Controls = { BuildStatusPage() } });
        tabs.TabPages.Add(new TabPage("Options") { Padding = new Padding(6), Controls = { BuildOptionsPage() } });
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (tabs.SelectedTab == keybindsTab)
                Render(_host.GetUiSnapshot(), ShellRenderTarget.Keybinds);
            else if (tabs.SelectedTab == backendsTab)
                Render(_host.GetUiSnapshot(), ShellRenderTarget.Backends);
        };
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

    private Control BuildBackendsPage()
    {
        var split = CreateVerticalSplitContainer(220, 160, 300);
        _backendList.Dock = DockStyle.Fill;
        _backendList.SelectedIndexChanged += (_, _) => RenderSelectedBackend();
        split.Panel1.Controls.Add(_backendList);

        var editor = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(8)
        };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _backendId.Dock = DockStyle.Fill;
        _backendId.PlaceholderText = "nyx or another backend ID";
        _backendExecutable.Dock = DockStyle.Fill;
        _backendExecutable.PlaceholderText = "Path to backend executable";
        editor.Controls.Add(new Label { Text = "Backend ID", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        editor.Controls.Add(_backendId, 1, 0);
        editor.Controls.Add(new Label { Text = "Executable", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        editor.Controls.Add(_backendExecutable, 1, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        buttons.Controls.Add(Button("Browse", (_, _) => BrowseBackendExecutable()));
        buttons.Controls.Add(Button("Register / Update", (_, _) => RegisterBackend()));
        buttons.Controls.Add(Button("Remove", (_, _) => RemoveBackend()));
        buttons.Controls.Add(Button("Clear", (_, _) => ClearBackendEditor()));
        editor.Controls.Add(buttons, 1, 2);
        editor.Controls.Add(new Label
        {
            Text = "Nyx opens in its installation folder. Other backends open in KhaosShell\\backends, falling back to KhaosShell.",
            AutoSize = true,
            Dock = DockStyle.Top,
            MaximumSize = new Size(520, 0)
        }, 1, 3);
        split.Panel2.Controls.Add(editor);
        return split;
    }

    private Control BuildKeybindsPage()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(6) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));

        _escapePanic.Text = "Panic Escape Enabled";
        _windowsKeyOverride.Text = "Override Windows Key to Toggle Menu";
        _windowsKeyOverride.AutoSize = true;
        _escapePanic.AutoSize = true;
        _windowsKeyOverride.Dock = DockStyle.Fill;
        _escapePanic.Dock = DockStyle.Fill;
        _windowsKeyOverride.Margin = new Padding(4, 2, 4, 2);
        _escapePanic.Margin = new Padding(4, 2, 4, 2);
        _windowsKeyOverride.CheckedChanged += (_, _) =>
        {
            if (!_updating)
            {
                if (RunSync(() => _host.SetCurrentWinToggle(_windowsKeyOverride.Checked)))
                    Render(_host.GetUiSnapshot(), ShellRenderTarget.Keybinds);
            }
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

            if (RunSync(() => _host.SetEscapePanicEnabled(_escapePanic.Checked)))
                Render(_host.GetUiSnapshot(), ShellRenderTarget.Keybinds);
        };
        root.Controls.Add(_escapePanic, 0, 0);
        root.Controls.Add(_windowsKeyOverride, 0, 1);
        root.Controls.Add(BuildWindowsKeyPolicyEditor(), 0, 2);

        _keybindList.Dock = DockStyle.Fill;
        _keybindList.SelectedIndexChanged += (_, _) => LoadSelectedKeybind();
        _keybindList.ItemCheck += OnKeybindItemCheck;
        var listPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 2 };
        listPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        listPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        listPanel.Controls.Add(_keybindList, 0, 0);
        var listButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        listButtons.Controls.Add(Button("Add", (_, _) => AddKeybind()));
        listButtons.Controls.Add(Button("Remove", (_, _) => RemoveKeybind()));
        listPanel.Controls.Add(listButtons, 1, 0);
        root.Controls.Add(listPanel, 0, 3);

        root.Controls.Add(BuildKeybindEditor(), 0, 4);
        return root;
    }

    private Control BuildWindowsKeyPolicyEditor()
    {
        var box = new GroupBox { Text = "Windows Key Override menu behavior", Dock = DockStyle.Fill };
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = true,
            AutoScroll = true,
            Padding = new Padding(4)
        };

        _windowsKeyHideMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _windowsKeyHideMode.Items.AddRange(new object[] { WindowsKeyMenuHideMode.Direct, WindowsKeyMenuHideMode.Cue });
        _windowsKeyHideMode.Width = 100;
        _windowsKeyHideCue.Width = 120;
        _windowsKeyShowCue.Width = 120;
        _windowsKeyShowCueEnabled.Text = "Send cue after showing";
        _windowsKeyShowCueEnabled.AutoSize = true;
        _windowsKeyHideMode.SelectedIndexChanged += (_, _) => UpdateWindowsKeyPolicyEditor();
        _windowsKeyShowCueEnabled.CheckedChanged += (_, _) => UpdateWindowsKeyPolicyEditor();
        panel.Controls.Add(new Label { Text = "Hide:", AutoSize = true, Padding = new Padding(0, 5, 4, 0) });
        panel.Controls.Add(_windowsKeyHideMode);
        panel.Controls.Add(new Label { Text = "Cue", AutoSize = true, Padding = new Padding(8, 5, 4, 0) });
        panel.Controls.Add(_windowsKeyHideCue);
        panel.Controls.Add(_windowsKeyShowCueEnabled);
        panel.Controls.Add(new Label { Text = "Cue", AutoSize = true, Padding = new Padding(8, 5, 4, 0) });
        panel.Controls.Add(_windowsKeyShowCue);
        panel.Controls.Add(Button("Save", (_, _) => SaveWindowsKeyPolicy()));
        box.Controls.Add(panel);
        return box;
    }

    private void UpdateWindowsKeyPolicyEditor()
    {
        if (_windowsKeyHideMode.SelectedItem is not WindowsKeyMenuHideMode hideMode)
            hideMode = WindowsKeyMenuHideMode.Direct;
        if (!_updating && hideMode == WindowsKeyMenuHideMode.Cue && string.IsNullOrWhiteSpace(_windowsKeyHideCue.Text))
            _windowsKeyHideCue.Text = "hide-menu";
        if (!_updating && _windowsKeyShowCueEnabled.Checked && string.IsNullOrWhiteSpace(_windowsKeyShowCue.Text))
            _windowsKeyShowCue.Text = "show-menu";
        _windowsKeyHideCue.Enabled = hideMode == WindowsKeyMenuHideMode.Cue;
        _windowsKeyShowCue.Enabled = _windowsKeyShowCueEnabled.Checked;
    }

    private void SaveWindowsKeyPolicy()
    {
        WindowsKeyMenuHideMode hideMode = _windowsKeyHideMode.SelectedItem is WindowsKeyMenuHideMode selected
            ? selected
            : WindowsKeyMenuHideMode.Direct;
        string hideCue = string.IsNullOrWhiteSpace(_windowsKeyHideCue.Text) ? "hide-menu" : _windowsKeyHideCue.Text.Trim();
        string showCue = string.IsNullOrWhiteSpace(_windowsKeyShowCue.Text) ? "show-menu" : _windowsKeyShowCue.Text.Trim();
        if (RunSync(() => _host.SetWindowsKeyMenuPolicy(hideMode, hideCue, _windowsKeyShowCueEnabled.Checked, showCue)))
        {
            _windowsKeyHideCue.Text = hideCue;
            _windowsKeyShowCue.Text = showCue;
            SetOutput("Windows Key Override menu policy saved.");
            Render(_host.GetUiSnapshot(), ShellRenderTarget.Keybinds);
        }
    }

    private Control BuildKeybindEditor()
    {
        var editor = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(4)
        };

        _keybindGesture.ReadOnly = true;
        _keybindGesture.Width = 180;
        _keybindGesture.KeyDown += (_, e) =>
        {
            if (!_capturingGesture)
                return;
            if (_editingKeybind?.SystemBinding == true)
                return;
            if (KeybindCatalog.IsModifier(e.KeyCode))
                return;
            _editingKeybind = (_editingKeybind ?? NewKeybind(_keybindItems)) with { Gesture = KeybindGesture.FromKeyEvent(e) };
            _keybindGesture.Text = _editingKeybind.Gesture.DisplayName;
            _capturingGesture = false;
            e.SuppressKeyPress = true;
            e.Handled = true;
        };
        _keybindGesture.Height = 28;
        var gesturePanel = new FlowLayoutPanel { AutoSize = true, MinimumSize = new Size(0, 34), WrapContents = false };
        gesturePanel.Controls.Add(_keybindGesture);
        gesturePanel.Controls.Add(Button("Capture", (_, _) =>
        {
            if (_editingKeybind?.SystemBinding == true)
                return;
            _capturingGesture = true;
            _keybindGesture.Text = "Press key...";
            _keybindGesture.Focus();
        }));
        editor.Controls.Add(EditorField("Key", gesturePanel));

        _keybindActionKind.DropDownStyle = ComboBoxStyle.DropDownList;
        _keybindActionKind.Items.AddRange(new object[] { KeybindActionKind.Cue, KeybindActionKind.AetherCommand, KeybindActionKind.ManagedMenu });
        _keybindActionKind.SelectedIndexChanged += (_, _) => { UpdateKeybindEditorVisibility(); MarkKeybindDirty(); };
        _keybindCommand.DropDownStyle = ComboBoxStyle.DropDownList;
        _keybindCommand.Items.AddRange(Enum.GetValues<RendererCommandKind>().Where(value => value != RendererCommandKind.None).Cast<object>().ToArray());
        _keybindCommand.SelectedIndexChanged += (_, _) => { UpdateKeybindEditorVisibility(); MarkKeybindDirty(); };
        _keybindMenuAction.DropDownStyle = ComboBoxStyle.DropDownList;
        _keybindMenuAction.Items.AddRange(Enum.GetValues<ManagedMenuAction>().Cast<object>().ToArray());
        _keybindMenuAction.SelectedIndexChanged += (_, _) => MarkKeybindDirty();
        _keybindTrayAction.DropDownStyle = ComboBoxStyle.DropDownList;
        _keybindTrayAction.Items.AddRange(Enum.GetValues<SystemTrayAction>().Cast<object>().ToArray());
        _keybindGroup.Width = 220;
        _keybindTarget.Width = 220;
        _keybindTopic.Width = 220;
        _keybindConfigPaths.Width = 220;
        _keybindConfigPaths.Multiline = true;
        _keybindConfigPaths.Height = 42;
        _keybindAll.Text = "All groups";
        _keybindOverride.Text = "Suppress matched key";
        _keybindOverride.AutoSize = true;
        _keybindAll.AutoSize = true;
        _keybindCueList.Height = 82;
        _keybindCueList.Width = 420;
        _keybindCueList.ItemCheck += (_, _) => MarkKeybindDirty();
        _keybindConfigPaths.Width = 420;
        foreach (Control control in new Control[] { _keybindGroup, _keybindTarget, _keybindTopic, _keybindConfigPaths, _keybindAll, _keybindOverride })
        {
            control.TextChanged += (_, _) => MarkKeybindDirty();
            if (control is CheckBox checkBox)
                checkBox.CheckedChanged += (_, _) => MarkKeybindDirty();
        }
        editor.Controls.Add(EditorField("Action", _keybindActionKind));
        var options = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 560, Padding = new Padding(0) };
        _keybindOptionsPanel = options;
        editor.Controls.Add(options);
        var flags = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        flags.Controls.Add(_keybindOverride);
        editor.Controls.Add(EditorField("Options", flags));
        var save = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        save.Controls.Add(Button("Save binding", (_, _) => SaveKeybind()));
        save.Controls.Add(new Label { Text = "Cue choices come from current theme plus archived names.", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
        editor.Controls.Add(save);
        UpdateKeybindEditorVisibility();
        return editor;
    }

    private FlowLayoutPanel? _keybindOptionsPanel;

    private static FlowLayoutPanel EditorField(string label, Control control)
    {
        var field = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        field.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 5, 8, 0) });
        field.Controls.Add(control);
        return field;
    }

    private KeybindDefinition NewKeybind(IReadOnlyList<KeybindDefinition> existing)
    {
        var candidates = new List<KeybindGesture>();
        foreach (KeybindModifiers modifiers in new[] { KeybindModifiers.Control | KeybindModifiers.Alt, KeybindModifiers.Control | KeybindModifiers.Alt | KeybindModifiers.Shift })
        {
            candidates.AddRange(Enumerable.Range((int)Keys.A, 26).Select(value => new KeybindGesture((Keys)value, modifiers)));
            candidates.AddRange(Enumerable.Range((int)Keys.F1, 12).Select(value => new KeybindGesture((Keys)value, modifiers)));
        }
        KeybindGesture gesture = candidates.First(candidate => existing.All(binding => binding.Gesture != candidate));
        return new(
            Guid.NewGuid(),
            gesture,
            Action: new KeybindAction(KeybindActionKind.ManagedMenu, MenuAction: ManagedMenuAction.Toggle));
    }

    private void AddKeybind()
    {
        KeybindDefinition binding = NewKeybind(_keybindItems);
        List<KeybindDefinition> next = _keybindItems.Concat(new[] { binding }).ToList();
        if (!RunSync(() => _host.SetKeybinds(next)))
            return;
        _editingKeybind = binding;
        _keybindEditorDirty = false;
        Render(_host.GetUiSnapshot(), ShellRenderTarget.Keybinds);
        SelectKeybind(binding.Id);
    }

    private void RemoveKeybind()
    {
        if (_keybindList.SelectedIndex < 0 || _keybindList.SelectedIndex >= _visibleKeybindItems.Count)
            return;
        KeybindDefinition selected = _visibleKeybindItems[_keybindList.SelectedIndex];
        List<KeybindDefinition> next = _keybindItems.Where(item => item.Id != selected.Id).ToList();
        if (!RunSync(() => _host.SetKeybinds(next)))
            return;
        _editingKeybind = null;
        Render(_host.GetUiSnapshot(), ShellRenderTarget.Keybinds);
    }

    private void OnKeybindItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (_updating || e.Index < 0 || e.Index >= _visibleKeybindItems.Count)
            return;
        KeybindDefinition binding = _visibleKeybindItems[e.Index];
        bool enabled = e.NewValue == CheckState.Checked;
        BeginInvoke((Action)(() =>
        {
            List<KeybindDefinition> next = _keybindItems.Select(item => item.Id == binding.Id ? item with { Enabled = enabled } : item).ToList();
            if (RunSync(() => _host.SetKeybinds(next)))
                Render(_host.GetUiSnapshot(), ShellRenderTarget.Keybinds);
        }));
    }

    private void SelectKeybind(Guid id)
    {
        int index = _visibleKeybindItems.FindIndex(binding => binding.Id == id);
        if (index >= 0)
            _keybindList.SelectedIndex = index;
    }

    private void LoadSelectedKeybind()
    {
        if (_keybindList.SelectedIndex < 0 || _keybindList.SelectedIndex >= _visibleKeybindItems.Count)
            return;
        bool wasUpdating = _updating;
        _updating = true;
        try
        {
            _editingKeybind = _visibleKeybindItems[_keybindList.SelectedIndex];
            KeybindAction action = _editingKeybind.EffectiveAction;
            _keybindGesture.Text = _editingKeybind.Gesture.DisplayName;
            _keybindOverride.Checked = _editingKeybind.Override;
            _keybindActionKind.SelectedItem = action.Kind;
            _keybindCommand.SelectedItem = action.Command == RendererCommandKind.None ? RendererCommandKind.Show : action.Command;
            _keybindMenuAction.SelectedItem = action.MenuAction;
            _keybindGroup.Text = action.Group ?? string.Empty;
            _keybindTarget.Text = action.TargetId ?? string.Empty;
            _keybindTopic.Text = action.Topic ?? string.Empty;
            _keybindConfigPaths.Text = action.ConfigPaths == null ? string.Empty : string.Join(Environment.NewLine, action.ConfigPaths);
            _keybindAll.Checked = action.All;
            _keybindTrayAction.SelectedItem = action.TrayAction;
            LoadCueChecks(action.EffectiveCueNames);
            _keybindEditorDirty = false;
            UpdateKeybindEditorVisibility();
        }
        finally { _updating = wasUpdating; }
    }

    private void SaveKeybind()
    {
        if (_editingKeybind == null)
        {
            SetOutput("Select or add a binding first.");
            return;
        }
        if (_editingKeybind.SystemBinding)
        {
            SetOutput("Windows Key Override binding is managed above.");
            return;
        }
        KeybindActionKind kind = _keybindActionKind.SelectedItem is KeybindActionKind selectedKind ? selectedKind : KeybindActionKind.Cue;
        string[] configPaths = _keybindConfigPaths.Text.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var action = new KeybindAction(
            kind,
            CueNames: _keybindCueList.CheckedItems.Cast<CueOption>().Select(option => option.Name).ToArray(),
            Command: _keybindCommand.SelectedItem is RendererCommandKind command ? command : RendererCommandKind.Show,
            Group: _keybindGroup.Text,
            All: _keybindAll.Checked,
            TargetId: _keybindTarget.Text,
            Topic: _keybindTopic.Text,
            ConfigPaths: configPaths,
            TrayAction: _keybindTrayAction.SelectedItem is SystemTrayAction trayAction ? trayAction : SystemTrayAction.Toggle,
            MenuAction: _keybindMenuAction.SelectedItem is ManagedMenuAction menuAction ? menuAction : ManagedMenuAction.Toggle);
        KeybindDefinition nextBinding = _editingKeybind with
        {
            Gesture = ParseGesture(_keybindGesture.Text, _editingKeybind.Gesture),
            Override = _keybindOverride.Checked,
            Action = action
        };
        List<KeybindDefinition> next = _keybindItems.Select(item => item.Id == nextBinding.Id ? nextBinding : item).ToList();
        if (!RunSync(() => _host.SetKeybinds(next)))
            return;
        _editingKeybind = nextBinding;
        _keybindEditorDirty = false;
        Render(_host.GetUiSnapshot(), ShellRenderTarget.Keybinds);
        SetOutput("Keybind saved.");
    }

    private static KeybindGesture ParseGesture(string text, KeybindGesture fallback) => fallback;

    private void LoadCueChecks(IReadOnlyList<string> selected)
    {
        _keybindCueList.Items.Clear();
        for (int index = 0; index < _cueOptions.Count; index++)
        {
            CueOption option = _cueOptions[index];
            _keybindCueList.Items.Add(option);
            if (selected.Contains(option.Name, StringComparer.Ordinal))
                _keybindCueList.SetItemChecked(index, true);
        }
    }

    private void MarkKeybindDirty()
    {
        if (!_updating && _editingKeybind?.SystemBinding != true)
            _keybindEditorDirty = true;
    }

    private void UpdateKeybindEditorVisibility()
    {
        if (_keybindOptionsPanel == null)
            return;
        _keybindOptionsPanel.Controls.Clear();
        bool editable = _editingKeybind?.SystemBinding != true;
        _keybindActionKind.Enabled = editable;
        _keybindOverride.Enabled = editable;
        bool cue = _keybindActionKind.SelectedItem is KeybindActionKind.Cue;
        bool managedMenu = _keybindActionKind.SelectedItem is KeybindActionKind.ManagedMenu;
        if (cue)
        {
            _keybindCueList.Enabled = editable;
            _keybindOptionsPanel.Controls.Add(EditorField("Cues", _keybindCueList));
        }
        else if (managedMenu)
        {
            _keybindMenuAction.Enabled = editable;
            _keybindOptionsPanel.Controls.Add(EditorField("Menu action", _keybindMenuAction));
        }
        else
        {
            _keybindCommand.Enabled = editable;
            _keybindOptionsPanel.Controls.Add(EditorField("Command", _keybindCommand));
            RendererCommandKind command = _keybindCommand.SelectedItem is RendererCommandKind selected ? selected : RendererCommandKind.Show;
            switch (command)
            {
                case RendererCommandKind.Show:
                    _keybindTopic.Enabled = editable;
                    _keybindOptionsPanel.Controls.Add(EditorField("Topic", _keybindTopic));
                    break;
                case RendererCommandKind.HideWindow:
                case RendererCommandKind.RevealWindow:
                    _keybindTarget.Enabled = editable;
                    _keybindOptionsPanel.Controls.Add(EditorField("Target ID", _keybindTarget));
                    break;
                case RendererCommandKind.Pause:
                case RendererCommandKind.Resume:
                case RendererCommandKind.Status:
                case RendererCommandKind.Reload:
                    _keybindGroup.Enabled = editable;
                    _keybindAll.Enabled = editable;
                    _keybindOptionsPanel.Controls.Add(EditorField("Group", _keybindGroup));
                    _keybindOptionsPanel.Controls.Add(_keybindAll);
                    break;
                case RendererCommandKind.SystemTray:
                    _keybindTrayAction.Enabled = editable;
                    _keybindOptionsPanel.Controls.Add(EditorField("Tray action", _keybindTrayAction));
                    break;
                case RendererCommandKind.ApplyConfig:
                    _keybindConfigPaths.Enabled = editable;
                    _keybindOptionsPanel.Controls.Add(EditorField("Config paths", _keybindConfigPaths));
                    break;
            }
        }
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
        _statusPollingEnabled.Text = "Status polling enabled";
        _restoreState.Text = "Restore theme state on launch";
        _trayEnabled.AutoSize = true;
        _statusPollingEnabled.AutoSize = true;
        _restoreState.AutoSize = true;
        _trayEnabled.CheckedChanged += (_, _) =>
        {
            if (!_updating)
                RunSync(() => _host.SetTrayVisible(_trayEnabled.Checked));
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

            if (target.HasFlag(ShellRenderTarget.Backends))
            {
                string? selectedBackendId = SelectedBackendId();
                IReadOnlyList<string> backendIds = _host.Backends.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
                UpdateList(_backendList, _backendItems, backendIds);
                if (selectedBackendId != null)
                {
                    int selectedIndex = _backendItems.FindIndex(id => string.Equals(id, selectedBackendId, StringComparison.Ordinal));
                    if (selectedIndex >= 0)
                        _backendList.SelectedIndex = selectedIndex;
                }
                if (_backendList.SelectedIndex < 0 && _backendItems.Count > 0)
                    _backendList.SelectedIndex = 0;
                if (!_backendId.Focused && !_backendExecutable.Focused && !_backendId.Modified && !_backendExecutable.Modified)
                    RenderSelectedBackend();
            }

            if (target.HasFlag(ShellRenderTarget.Keybinds))
            {
                _windowsKeyOverride.Checked = snapshot.WindowsKeyOverrideEnabled;
                _windowsKeyHideMode.SelectedItem = snapshot.Settings.WindowsKeyMenuHideMode;
                _windowsKeyHideCue.Text = snapshot.Settings.WindowsKeyMenuHideCue;
                _windowsKeyShowCueEnabled.Checked = snapshot.Settings.WindowsKeyMenuShowCueEnabled;
                _windowsKeyShowCue.Text = snapshot.Settings.WindowsKeyMenuShowCue;
                UpdateWindowsKeyPolicyEditor();
                SetEscapePanicCheckbox(snapshot.EscapePanicEnabled);
                IReadOnlyList<string> historicalCues = (snapshot.Settings.Keybinds ?? KeybindCatalog.DefaultBindings)
                    .Where(binding => binding.EffectiveAction.Kind == KeybindActionKind.Cue)
                    .SelectMany(binding => binding.EffectiveAction.EffectiveCueNames)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                _cueOptions.Clear();
                foreach (string cue in snapshot.Capabilities.Cues)
                    _cueOptions.Add(new CueOption(cue, true));
                foreach (string cue in historicalCues.Where(cue => !snapshot.Capabilities.Cues.Contains(cue, StringComparer.Ordinal)))
                    _cueOptions.Add(new CueOption(cue, false));
                IReadOnlyList<KeybindDefinition> persistedBindings = snapshot.Settings.Keybinds ?? KeybindCatalog.DefaultBindings;
                KeybindDefinition? persistedWindows = persistedBindings.FirstOrDefault(binding => binding.SystemBinding);
                int localWindowsIndex = _keybindItems.FindIndex(binding => binding.SystemBinding);
                if (persistedWindows != null && localWindowsIndex >= 0)
                    _keybindItems[localWindowsIndex] = persistedWindows;
                if (!_keybindEditorDirty || _editingKeybind == null)
                {
                    Guid? selectedId = _editingKeybind?.Id;
                    _keybindItems.Clear();
                    _keybindItems.AddRange(persistedBindings);
                    _visibleKeybindItems.Clear();
                    _visibleKeybindItems.AddRange(_keybindItems.Where(binding => !binding.SystemBinding));
                    _keybindList.Items.Clear();
                    foreach (KeybindDefinition binding in _visibleKeybindItems)
                    {
                        int itemIndex = _keybindList.Items.Add(binding);
                        _keybindList.SetItemChecked(itemIndex, binding.Enabled);
                    }
                    int selectedIndex = selectedId is { } id ? _visibleKeybindItems.FindIndex(binding => binding.Id == id) : -1;
                    if (selectedIndex >= 0)
                        _keybindList.SelectedIndex = selectedIndex;
                    else if (_visibleKeybindItems.Count > 0)
                        _keybindList.SelectedIndex = 0;
                    LoadSelectedKeybind();
                }
            }

            if (target.HasFlag(ShellRenderTarget.Options))
            {
                _trayEnabled.Enabled = snapshot.TrayEnabled;
                _trayEnabled.Checked = snapshot.TrayVisible;
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
            try { BeginInvoke((Action)(() => Render(_host.GetUiSnapshot(), ShellRenderTarget.Status | ShellRenderTarget.Renderer | ShellRenderTarget.Backends))); } catch (InvalidOperationException) { }
            return;
        }
        Render(_host.GetUiSnapshot(), ShellRenderTarget.Status | ShellRenderTarget.Renderer | ShellRenderTarget.Backends);
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

    private void BrowseBackendExecutable()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select backend executable",
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = _host.BackendChooserDirectory(_backendId.Text)
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _backendExecutable.Text = dialog.FileName;
    }

    private void RegisterBackend()
    {
        string backendId = _backendId.Text.Trim();
        if (RunSync(() => _host.RegisterBackend(backendId, _backendExecutable.Text)))
        {
            SetOutput($"Backend '{backendId}' registered.");
            Render(_host.GetUiSnapshot(), ShellRenderTarget.Backends);
            int selectedIndex = _backendItems.FindIndex(id => string.Equals(id, backendId, StringComparison.Ordinal));
            if (selectedIndex >= 0)
                _backendList.SelectedIndex = selectedIndex;
        }
    }

    private void RemoveBackend()
    {
        string? backendId = SelectedBackendId();
        if (backendId == null)
        {
            SetOutput("Select a backend first.");
            return;
        }
        if (MessageBox.Show(this, $"Remove '{backendId}' from the backend registry?", "KhaosShell", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        if (RunSync(() => _host.RemoveBackend(backendId)))
        {
            SetOutput($"Backend '{backendId}' removed.");
            Render(_host.GetUiSnapshot(), ShellRenderTarget.Backends);
        }
    }

    private void ClearBackendEditor()
    {
        _backendList.ClearSelected();
        _backendId.Clear();
        _backendExecutable.Clear();
    }

    private string? SelectedBackendId() =>
        _backendList.SelectedIndex >= 0 && _backendList.SelectedIndex < _backendItems.Count
            ? _backendItems[_backendList.SelectedIndex]
            : null;

    private void RenderSelectedBackend()
    {
        string? backendId = SelectedBackendId();
        if (backendId == null || !_host.Backends.TryGetValue(backendId, out BackendDefinition? definition))
            return;
        _backendId.Text = backendId;
        _backendExecutable.Text = definition.Executable;
        _backendId.Modified = false;
        _backendExecutable.Modified = false;
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

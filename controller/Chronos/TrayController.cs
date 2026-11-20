using System.Windows.Forms;

namespace Chronos;

internal sealed class TrayController : IDisposable
{
    private readonly RendererCommandService _commands;
    private readonly WinMenuToggleController _winToggle;
    private readonly Action _openShell;
    private readonly Action _exit;
    private readonly Action<string> _diagnostic;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private bool _disposed;

    public TrayController(
        RendererCommandService commands,
        WinMenuToggleController winToggle,
        Action openShell,
        Action exit,
        Action<string> diagnostic)
    {
        _commands = commands;
        _winToggle = winToggle;
        _openShell = openShell;
        _exit = exit;
        _diagnostic = diagnostic;
        _menu = new ContextMenuStrip();
        _menu.Opening += (_, _) => RebuildMenu();
        _icon = new NotifyIcon
        {
            Text = "KhaosShell",
            Icon = SystemIcons.Application,
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => _openShell();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    public bool Visible => !_disposed && _icon.Visible;

    public void SetVisible(bool visible)
    {
        if (!_disposed)
            _icon.Visible = visible;
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();
        var open = new ToolStripMenuItem("Open KhaosShell");
        open.Click += (_, _) => _openShell();
        _menu.Items.Add(open);
        _menu.Items.Add(new ToolStripSeparator());
        RendererCapabilities capabilities = _commands.Capabilities;
        AddAction("Show menu", () => _commands.ShowMenuAsync(), !capabilities.HasMenu);
        AddAction("Hide menu", () => _commands.HideMenuAsync(), !capabilities.HasMenu);
        _menu.Items.Add(new ToolStripSeparator());

        if (capabilities.Windows.Count == 0)
        {
            var none = new ToolStripMenuItem("No renderer windows") { Enabled = false };
            _menu.Items.Add(none);
        }
        else
        {
            var windows = new ToolStripMenuItem("Windows");
            foreach (RendererWindowCapability window in capabilities.Windows)
            {
                var item = new ToolStripMenuItem(window.Id);
                item.Click += (_, _) => Run(() => _commands.SetWindowVisibilityAsync(window.Id, window.Visible != true));
                windows.DropDownItems.Add(item);
            }
            _menu.Items.Add(windows);
        }

        var winItem = new ToolStripMenuItem("Windows Key Override")
        {
            Checked = _winToggle.Enabled,
            CheckOnClick = false
        };
        winItem.Click += (_, _) => _winToggle.ToggleEnabled();
        _menu.Items.Add(winItem);
        _menu.Items.Add(new ToolStripSeparator());
        var refresh = new ToolStripMenuItem("Refresh renderer state");
        refresh.Click += async (_, _) =>
        {
            try { await _commands.RefreshAsync().ConfigureAwait(true); }
            catch (Exception ex) { _diagnostic($"Renderer refresh failed: {ex.Message}"); }
        };
        _menu.Items.Add(refresh);
        var exit = new ToolStripMenuItem("Exit KhaosShell");
        exit.Click += (_, _) => _exit();
        _menu.Items.Add(exit);
    }

    private void AddAction(string text, Func<Task<AetherCommandResult>> action, bool disabled)
    {
        var item = new ToolStripMenuItem(text) { Enabled = !disabled };
        item.Click += (_, _) => Run(action);
        _menu.Items.Add(item);
    }

    private void Run(Func<Task<AetherCommandResult>> action)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                AetherCommandResult result = await action().ConfigureAwait(false);
                if (!result.Success)
                    _diagnostic(result.Error ?? "Renderer command failed.");
            }
            catch (Exception ex) { _diagnostic(ex.Message); }
        });
    }
}

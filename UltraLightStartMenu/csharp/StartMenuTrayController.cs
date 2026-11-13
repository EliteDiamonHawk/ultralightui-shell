namespace UltraLightStartMenu;

public sealed class StartMenuTrayController : IDisposable
{
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _showItem;
    private readonly ToolStripMenuItem _hideItem;

    public StartMenuTrayController(
        Action show,
        Action hide,
        Action reload,
        Action printStatus,
        Action exit)
    {
        _menu = new ContextMenuStrip();
        _showItem = new ToolStripMenuItem("Show start menu", null, (_, _) => show());
        _hideItem = new ToolStripMenuItem("Hide start menu", null, (_, _) => hide());
        _menu.Items.Add(_showItem);
        _menu.Items.Add(_hideItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Reload start menu", null, (_, _) => reload()));
        _menu.Items.Add(new ToolStripMenuItem("Print status", null, (_, _) => printStatus()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => exit()));

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "UltraLightStartMenu",
            Visible = true,
            ContextMenuStrip = _menu
        };
    }

    public void Update(bool visible)
    {
        _showItem.Enabled = !visible;
        _hideItem.Enabled = visible;
    }

    public void Dispose()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
    }
}

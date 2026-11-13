using System.Windows.Forms;

namespace UltraLightRenderer;

public sealed class RendererHostContext : ApplicationContext
{
    private readonly HostOptions _options;
    private readonly SingleInstanceLease _lease;
    private readonly Control _dispatcher = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly CommandPipeServer _commandServer;
    private readonly InputRouter _input;
    private readonly FullscreenMonitor _fullscreenMonitor;
    private readonly object _sync = new();
    private readonly Dictionary<string, RuntimeGroupHost> _groups = new(StringComparer.Ordinal);
    private bool _exiting;
    private RendererConfiguration _configuration;
    private IReadOnlyList<RendererWindowSpec> _specs;

    public RendererHostContext(HostOptions options, SingleInstanceLease lease)
    {
        _options = options;
        _lease = lease;
        _dispatcher.CreateControl();
        _configuration = RendererConfiguration.Load(options.ConfigPaths);
        _specs = _configuration.ResolveWindowSpecs();

        try
        {
            StartGroups(_specs);
            _input = new InputRouter(GetDesktopWindows);
            _fullscreenMonitor = new FullscreenMonitor(OnFullscreenChanged);
            _fullscreenMonitor.Start();
            _commandServer = new CommandPipeServer(HandleCommand);
            _commandServer.Start();
            _menu = BuildMenu();
            _tray = new NotifyIcon
            {
                Text = "UltraLightRenderer",
                Icon = SystemIcons.Application,
                ContextMenuStrip = _menu,
                Visible = true
            };
        }
        catch
        {
            StopGroups();
            _dispatcher.Dispose();
            throw;
        }
    }

    private void StartGroups(IReadOnlyList<RendererWindowSpec> specs)
    {
        foreach (string groupName in specs.Select(spec => spec.ThreadGroup).Distinct(StringComparer.Ordinal))
        {
            var group = new RuntimeGroupHost(groupName, _options, () => _specs);
            group.Start();
            _groups.Add(groupName, group);
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show start menu", null, (_, _) => ExecuteUiCommand(new(RendererCommandKind.Show)));
        menu.Items.Add("Hide start menu", null, (_, _) => ExecuteUiCommand(new(RendererCommandKind.Hide)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Pause all groups", null, (_, _) => ExecuteUiCommand(new(RendererCommandKind.Pause, All: true)));
        menu.Items.Add("Resume all groups", null, (_, _) => ExecuteUiCommand(new(RendererCommandKind.Resume, All: true)));
        menu.Items.Add("Reload all groups", null, (_, _) => ExecuteUiCommand(new(RendererCommandKind.Reload, All: true)));
        menu.Items.Add("Print status", null, (_, _) => ExecuteUiCommand(new(RendererCommandKind.Status, All: true)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExecuteUiCommand(new(RendererCommandKind.Exit)));
        return menu;
    }

    private void ExecuteUiCommand(RendererCommand command)
    {
        try
        {
            string response = HandleCommand(command);
            if (response.StartsWith("ERR ", StringComparison.Ordinal))
                MessageBox.Show(response[4..], "UltraLightRenderer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else if (command.Kind == RendererCommandKind.Status)
                MessageBox.Show(response[3..], "UltraLightRenderer status", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "UltraLightRenderer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string HandleCommand(RendererCommand command)
    {
        if (command.Kind == RendererCommandKind.Show || command.Kind == RendererCommandKind.Hide)
        {
            string? group = _specs.FirstOrDefault(spec => spec.Kind == WindowKind.StartMenu)?.ThreadGroup;
            if (group == null)
                return "ERR No start-menu window is configured.";
            if (!_groups.TryGetValue(group, out RuntimeGroupHost? host))
                return $"ERR Start-menu group '{group}' is not running.";
            if (command.Kind == RendererCommandKind.Show) host.InvokeShowMenu(); else host.InvokeHideMenu();
            return $"OK {command.Kind.ToString().ToLowerInvariant()} start menu";
        }

        if (command.Kind == RendererCommandKind.Exit)
        {
            _dispatcher.BeginInvoke((Action)ExitHost);
            return "OK exit requested";
        }

        IReadOnlyList<RuntimeGroupHost> targets;
        try { targets = ResolveTargets(command); }
        catch (Exception ex) { return $"ERR {ex.Message}"; }

        switch (command.Kind)
        {
            case RendererCommandKind.Pause:
                foreach (RuntimeGroupHost group in targets) group.InvokePause(true);
                return $"OK paused {targets.Count} group(s)";
            case RendererCommandKind.Resume:
                foreach (RuntimeGroupHost group in targets) group.InvokePause(false);
                return $"OK resumed {targets.Count} group(s)";
            case RendererCommandKind.Status:
                string status = string.Join(Environment.NewLine, targets.Select(group => group.InvokeStatus()));
                Console.WriteLine(status);
                return $"OK {status}";
            case RendererCommandKind.Reload:
                return ReloadTargets(targets, command.All);
            default:
                return "ERR Unsupported runtime command.";
        }
    }

    private IReadOnlyList<RuntimeGroupHost> ResolveTargets(RendererCommand command)
    {
        lock (_sync)
        {
            if (command.All)
                return _groups.Values.ToArray();
            if (command.Group != null)
            {
                if (command.Group.Length == 0)
                    throw new InvalidOperationException("Group name must not be empty.");
                if (!_groups.TryGetValue(command.Group, out RuntimeGroupHost? host))
                    throw new InvalidOperationException($"Unknown runtime group: {command.Group}");
                return new[] { host };
            }
            if (_groups.Count != 1)
                throw new InvalidOperationException("A group name is required when more than one group is running.");
            return _groups.Values.ToArray();
        }
    }

    private string ReloadTargets(IReadOnlyList<RuntimeGroupHost> targets, bool reloadAll)
    {
        RendererConfiguration candidate;
        IReadOnlyList<RendererWindowSpec> candidateSpecs;
        try
        {
            candidate = RendererConfiguration.Load(_options.ConfigPaths);
            candidateSpecs = candidate.ResolveWindowSpecs();
        }
        catch (Exception ex)
        {
            return $"ERR Reload rejected: {ex.Message}";
        }

        var candidateGroups = candidateSpecs.Select(spec => spec.ThreadGroup).ToHashSet(StringComparer.Ordinal);
        if (!candidateGroups.SetEquals(_groups.Keys) && !reloadAll)
            return "ERR Group membership changed; use --reload-all.";

        _configuration = candidate;
        _specs = candidateSpecs;
        if (reloadAll && !candidateGroups.SetEquals(_groups.Keys))
        {
            foreach (string groupName in candidateGroups.Except(_groups.Keys, StringComparer.Ordinal).ToArray())
            {
                var group = new RuntimeGroupHost(groupName, _options, () => _specs);
                group.Start();
                _groups.Add(groupName, group);
            }

            foreach (string groupName in _groups.Keys.Except(candidateGroups, StringComparer.Ordinal).ToArray())
            {
                _groups[groupName].Dispose();
                _groups.Remove(groupName);
            }
            targets = _groups.Values.ToArray();
        }

        foreach (RuntimeGroupHost group in targets)
            group.InvokeReload();
        return $"OK reloaded {targets.Count} group(s)";
    }

    private IReadOnlyList<WallpaperWindow> GetDesktopWindows()
    {
        RuntimeGroupHost[] groups;
        lock (_sync) groups = _groups.Values.ToArray();
        return groups.SelectMany(group => group.GetDesktopWindows()).ToArray();
    }

    private void OnFullscreenChanged(IntPtr monitor)
    {
        RuntimeGroupHost[] groups;
        lock (_sync) groups = _groups.Values.ToArray();
        foreach (RuntimeGroupHost group in groups)
        {
            try { group.InvokeFullscreenMonitor(monitor); }
            catch (Exception ex) { Console.Error.WriteLine($"[Fullscreen] {ex.Message}"); }
        }
    }

    private void ExitHost()
    {
        if (_exiting)
            return;
        _exiting = true;
        _input.Dispose();
        _fullscreenMonitor.Dispose();
        _commandServer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        StopGroups();
        _dispatcher.Dispose();
        Application.ExitThread();
    }

    private void StopGroups()
    {
        RuntimeGroupHost[] groups;
        lock (_sync)
        {
            groups = _groups.Values.ToArray();
            _groups.Clear();
        }
        foreach (RuntimeGroupHost group in groups)
            group.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_exiting)
            ExitHost();
        base.Dispose(disposing);
    }
}

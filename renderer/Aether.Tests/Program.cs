using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Aether;
using KhaosShell.Contracts;

namespace Aether.Tests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var tests = new List<(string Name, Action Test)>
        {
            (nameof(OptionsParseStartupConfigs), OptionsParseStartupConfigs),
            (nameof(ManagedStartupDoesNotForwardConfig), ManagedStartupDoesNotForwardConfig),
            (nameof(OptionsParseNamedCommands), OptionsParseNamedCommands),
            (nameof(OptionsParseAllCommands), OptionsParseAllCommands),
            (nameof(OptionsAllowOmittedGroup), OptionsAllowOmittedGroup),
            (nameof(OptionsRejectMixedCommands), OptionsRejectMixedCommands),
            (nameof(OptionsAllowNoConfig), OptionsAllowNoConfig),
            (nameof(OptionsParseWindowAndInspectionCommands), OptionsParseWindowAndInspectionCommands),
            (nameof(OptionsParseMenuCommands), OptionsParseMenuCommands),
            (nameof(OptionsParseSystemTrayCommands), OptionsParseSystemTrayCommands),
            (nameof(RendererDataUsesLocalAppData), RendererDataUsesLocalAppData),
            (nameof(ConfigurationMergesDistinctSections), ConfigurationMergesDistinctSections),
            (nameof(ConfigurationLoadsInlineJson), ConfigurationLoadsInlineJson),
            (nameof(ConfigurationRejectsDuplicateSections), ConfigurationRejectsDuplicateSections),
            (nameof(ConfigurationAppliesDefaultGroups), ConfigurationAppliesDefaultGroups),
            (nameof(ConfigurationSupportsExplicitGroups), ConfigurationSupportsExplicitGroups),
            (nameof(ConfigurationRejectsUnknownFields), ConfigurationRejectsUnknownFields),
            (nameof(ConfigurationParsesMenuIdsAndBridge), ConfigurationParsesMenuIdsAndBridge),
            (nameof(ConfigurationRejectsInvalidRuntimeCommands), ConfigurationRejectsInvalidRuntimeCommands),
            (nameof(RuntimeCommandPayloadsMapAndValidate), RuntimeCommandPayloadsMapAndValidate),
            (nameof(ConfigurationRejectsNonAsciiIds), ConfigurationRejectsNonAsciiIds),
            (nameof(GeometryPreservesNegativeCoordinates), GeometryPreservesNegativeCoordinates),
            (nameof(UrlBuilderNormalizesAndRejects), UrlBuilderNormalizesAndRejects),
            (nameof(RegistryTracksLifecycle), RegistryTracksLifecycle),
            (nameof(CommandProtocolRoundTrips), CommandProtocolRoundTrips)
            ,(nameof(CommandProtocolRejectsOversizedFrame), CommandProtocolRejectsOversizedFrame)
            ,(nameof(ControlProtocolRoundTrips), ControlProtocolRoundTrips)
            ,(nameof(ControlProtocolAcceptsUnmatchedError), ControlProtocolAcceptsUnmatchedError)
            ,(nameof(BoundedReaderPreservesFrames), BoundedReaderPreservesFrames)
            ,(nameof(BoundedReaderRejectsOversizedFrame), BoundedReaderRejectsOversizedFrame)
            ,(nameof(BackendQueueReportsOverflowPerBurst), BackendQueueReportsOverflowPerBurst)
            ,(nameof(LoggingPolicyParsesAndBounds), LoggingPolicyParsesAndBounds)
            ,(nameof(LoggingPolicyRejectsInvalid), LoggingPolicyRejectsInvalid)
            ,(nameof(LoggerUsesConsoleWithoutController), LoggerUsesConsoleWithoutController)
            ,(nameof(LoggerRedactsProperties), LoggerRedactsProperties)
            ,(nameof(LoggerBatchesThroughController), LoggerBatchesThroughController)
            ,(nameof(LoggerDisableFlushesBeforeDisabling), LoggerDisableFlushesBeforeDisabling)
        };

        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "Ultralight.dll")))
            tests.Add((nameof(UltralightDataBindingAbi), UltralightDataBindingAbi));

        int failures = 0;
        foreach ((string name, Action test) in tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
            }
        }

        Console.WriteLine($"{tests.Count - failures}/{tests.Count} unit tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void OptionsParseStartupConfigs()
    {
        HostOptions options = HostOptions.Parse(new[] { "--config", "one.json", "--config=two.json", "--verbose" });
        AssertEqual(2, options.ConfigPaths.Count);
        AssertTrue(options.Verbose);
        AssertEqual(RendererCommandKind.None, options.Command.Kind);
        AssertTrue(Aether.Program.ShouldForwardStartupConfig(options));
    }

    private static void ManagedStartupDoesNotForwardConfig() =>
        AssertThrows<OptionException>(() => HostOptions.Parse(new[]
        {
            "--config", "theme.json",
            "--control-read-handle", "101",
            "--control-write-handle", "202"
        }));

    private static void RendererDataUsesLocalAppData()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            return;

        string expected = Path.Combine(localAppData, "KhaosShell", "Aether", "ultralight.data");
        AssertEqual(expected, RendererPaths.Resolve(null).UltralightFileSystemPath);
        AssertEqual(expected, RendererPaths.Resolve("C:\\vendor\\ultralight").UltralightFileSystemPath);
    }

    private static void ConfigurationLoadsInlineJson()
    {
        using JsonDocument document = JsonDocument.Parse("{\"wallpaper\":[]}");
        RendererConfiguration configuration = RendererConfiguration.LoadJson(document.RootElement, "theme-source.json");
        AssertEqual(0, configuration.Wallpapers.Count);
        AssertEqual(0, configuration.Widgets.Count);
        AssertEqual<StartMenuDefinition?>(null, configuration.Menu);
    }

    private static void OptionsParseNamedCommands()
    {
        HostOptions pause = HostOptions.Parse(new[] { "--pause", "desktop" });
        AssertEqual(RendererCommandKind.Pause, pause.Command.Kind);
        AssertEqual("desktop", pause.Command.Group);
        AssertFalse(pause.Command.All);

        HostOptions reload = HostOptions.Parse(new[] { "--reload", "desktop" });
        AssertEqual(RendererCommandKind.Reload, reload.Command.Kind);
        AssertEqual("desktop", reload.Command.Group);
    }

    private static void OptionsParseAllCommands()
    {
        HostOptions pause = HostOptions.Parse(new[] { "--pause-all" });
        AssertTrue(pause.Command.All);
        AssertEqual(RendererCommandKind.Pause, pause.Command.Kind);

        HostOptions resume = HostOptions.Parse(new[] { "--resume-all" });
        AssertTrue(resume.Command.All);
        AssertEqual(RendererCommandKind.Resume, resume.Command.Kind);

        HostOptions status = HostOptions.Parse(new[] { "--status-all" });
        AssertTrue(status.Command.All);
        AssertEqual(RendererCommandKind.Status, status.Command.Kind);
    }

    private static void OptionsAllowOmittedGroup()
    {
        foreach (string option in new[] { "--pause", "--resume", "--status", "--reload" })
        {
            HostOptions options = HostOptions.Parse(new[] { option });
            AssertEqual(null, options.Command.Group);
            AssertFalse(options.Command.All);
        }
    }

    private static void OptionsRejectMixedCommands() =>
        AssertThrows<OptionException>(() => HostOptions.Parse(new[] { "--pause", "desktop", "--resume" }));

    private static void OptionsAllowNoConfig()
    {
        HostOptions options = HostOptions.Parse(Array.Empty<string>());
        AssertEqual(0, options.ConfigPaths.Count);
        AssertEqual(RendererCommandKind.None, options.Command.Kind);
    }

    private static void OptionsParseWindowAndInspectionCommands()
    {
        HostOptions hide = HostOptions.Parse(new[] { "--hide", "widget-1" });
        AssertEqual(RendererCommandKind.HideWindow, hide.Command.Kind);
        AssertEqual("widget-1", hide.Command.TargetId);

        HostOptions show = HostOptions.Parse(new[] { "--show=widget-1" });
        AssertEqual(RendererCommandKind.RevealWindow, show.Command.Kind);
        AssertEqual("widget-1", show.Command.TargetId);

        HostOptions list = HostOptions.Parse(new[] { "--list", "windows" });
        AssertEqual(RendererCommandKind.Show, list.Command.Kind);
        AssertEqual("windows", list.Command.Topic);

        AssertThrows<OptionException>(() => HostOptions.Parse(new[] { "--show" }));
        AssertThrows<OptionException>(() => HostOptions.Parse(new[] { "--list" }));
        AssertThrows<OptionException>(() => HostOptions.Parse(new[] { "--reveal", "widget-1" }));
    }

    private static void OptionsParseMenuCommands()
    {
        HostOptions toggle = HostOptions.Parse(new[] { "--toggle-menu" });
        AssertEqual(RendererCommandKind.ToggleMenu, toggle.Command.Kind);

        HostOptions emptyInline = HostOptions.Parse(new[] { "--toggle-menu=" });
        AssertEqual(RendererCommandKind.ToggleMenu, emptyInline.Command.Kind);

        HostOptions show = HostOptions.Parse(new[] { "--toggle-menu", "show" });
        AssertEqual(RendererCommandKind.ShowMenu, show.Command.Kind);

        HostOptions hide = HostOptions.Parse(new[] { "--toggle-menu=HIDE" });
        AssertEqual(RendererCommandKind.HideMenu, hide.Command.Kind);

        AssertThrows<OptionException>(() => HostOptions.Parse(new[] { "--toggle-menu", "empty" }));
        AssertThrows<OptionException>(() => HostOptions.Parse(new[] { "--show-menu" }));
        AssertThrows<OptionException>(() => HostOptions.Parse(new[] { "--hide-menu" }));
    }

    private static void OptionsParseSystemTrayCommands()
    {
        HostOptions show = HostOptions.Parse(new[] { "--system-tray", "show" });
        AssertEqual(SystemTrayAction.Show, show.InitialSystemTrayAction);

        HostOptions hide = HostOptions.Parse(new[] { "--system-tray=hide" });
        AssertEqual(SystemTrayAction.Hide, hide.InitialSystemTrayAction);

        HostOptions toggle = HostOptions.Parse(new[] { "--system-tray" });
        AssertEqual(SystemTrayAction.Toggle, toggle.InitialSystemTrayAction);

        AssertThrows<OptionException>(() => HostOptions.Parse(new[] { "--system-tray", "invalid" }));
        AssertThrows<OptionException>(() => HostOptions.Parse(new[] { "--system-tray", "show", "--system-tray", "hide" }));
    }

    private static void ConfigurationMergesDistinctSections()
    {
        string wallpaper = WriteTemp("{\"wallpaper\":[{\"url\":\"example.test\",\"mode\":\"span\"}]}");
        string widgets = WriteTemp("{\"widgets\":[{\"x\":0,\"y\":0,\"pos-scaled\":false,\"size-scaled\":false,\"anchor\":\"top-left\",\"width\":100,\"height\":100,\"monitor\":1,\"url\":\"example.test\"}]}");
        try
        {
            RendererConfiguration configuration = RendererConfiguration.Load(new[] { wallpaper, widgets });
            AssertEqual(1, configuration.Wallpapers.Count);
            AssertEqual(1, configuration.Widgets.Count);
            AssertEqual("https://example.test/", configuration.Wallpapers[0].Url);
        }
        finally { Delete(wallpaper); Delete(widgets); }
    }

    private static void ConfigurationRejectsDuplicateSections()
    {
        string first = WriteTemp("{\"widgets\":[]}");
        string second = WriteTemp("{\"widgets\":[]}");
        try { AssertThrows<RendererConfigurationException>(() => RendererConfiguration.Load(new[] { first, second })); }
        finally { Delete(first); Delete(second); }
    }

    private static void ConfigurationAppliesDefaultGroups()
    {
        string path = WriteTemp("{\"widgets\":[{\"x\":0,\"y\":0,\"pos-scaled\":false,\"size-scaled\":false,\"anchor\":\"top-left\",\"width\":100,\"height\":100,\"monitor\":1,\"url\":\"example.test\"}],\"menu\":{\"x\":0,\"y\":0,\"pos-scaled\":false,\"size-scaled\":false,\"anchor\":\"top-left\",\"width\":100,\"height\":100,\"monitor\":1,\"url\":\"example.test\"}}");
        try
        {
            RendererConfiguration configuration = RendererConfiguration.Load(new[] { path });
            AssertEqual("widgets", configuration.Widgets[0].ThreadGroup);
            AssertEqual("menu", configuration.Menu!.ThreadGroup);
        }
        finally { Delete(path); }
    }

    private static void ConfigurationSupportsExplicitGroups()
    {
        string path = WriteTemp("{\"wallpaper\":[{\"url\":\"example.test\",\"thread-group\":\"desktop\"}],\"widgets\":[]}");
        try
        {
            RendererConfiguration configuration = RendererConfiguration.Load(new[] { path });
            AssertEqual("desktop", configuration.Wallpapers[0].ThreadGroup);
        }
        finally { Delete(path); }
    }

    private static void ConfigurationRejectsUnknownFields()
    {
        string path = WriteTemp("{\"widgets\":[],\"unknown\":[]}");
        try { AssertThrows<RendererConfigurationException>(() => RendererConfiguration.Load(new[] { path })); }
        finally { Delete(path); }
    }

    private static void ConfigurationParsesMenuIdsAndBridge()
    {
        string path = WriteTemp("{\"menu\":{\"id\":\"main.menu\",\"x\":0,\"y\":0,\"pos-scaled\":false,\"size-scaled\":false,\"anchor\":\"top-left\",\"width\":100,\"height\":100,\"monitor\":1,\"url\":\"file:///menu.html\",\"bridge\":{\"enabled\":true,\"backend-ids\":[\"cpu\"],\"control-routes\":[\"/v1/windows/{id}/reveal\"],\"runtime-commands\":[\"toggleMenu\",\"hideWindow\"]}}}");
        try
        {
            RendererConfiguration configuration = RendererConfiguration.Load(new[] { path });
            AssertEqual("main.menu", configuration.Menu!.Id);
            AssertTrue(configuration.Menu.Bridge!.Enabled == true);
            AssertEqual("cpu", configuration.Menu.Bridge.BackendIds.Single());
            AssertEqual("toggleMenu", configuration.Menu.Bridge.RuntimeCommands[0]);
            AssertEqual("hideWindow", configuration.Menu.Bridge.RuntimeCommands[1]);
        }
        finally { Delete(path); }
    }

    private static void ConfigurationRejectsInvalidRuntimeCommands()
    {
        string unknown = WriteTemp("{\"widgets\":[],\"menu\":{\"x\":0,\"y\":0,\"pos-scaled\":false,\"size-scaled\":false,\"anchor\":\"top-left\",\"width\":100,\"height\":100,\"monitor\":1,\"url\":\"file:///menu.html\",\"bridge\":{\"runtime-commands\":[\"exit\"]}}}");
        string duplicate = WriteTemp("{\"widgets\":[],\"menu\":{\"x\":0,\"y\":0,\"pos-scaled\":false,\"size-scaled\":false,\"anchor\":\"top-left\",\"width\":100,\"height\":100,\"monitor\":1,\"url\":\"file:///menu.html\",\"bridge\":{\"runtime-commands\":[\"reload\",\"reload\"]}}}");
        try
        {
            AssertThrows<RendererConfigurationException>(() => RendererConfiguration.Load(new[] { unknown }));
            AssertThrows<RendererConfigurationException>(() => RendererConfiguration.Load(new[] { duplicate }));
        }
        finally { Delete(unknown); Delete(duplicate); }
    }

    private static void RuntimeCommandPayloadsMapAndValidate()
    {
        using JsonDocument menu = JsonDocument.Parse("{\"command\":\"toggleMenu\"}");
        AssertTrue(RendererJsBridge.TryParseRuntimeCommand(menu.RootElement, out _, out RendererCommand toggle, out _));
        AssertEqual(RendererCommandKind.ToggleMenu, toggle.Kind);

        using JsonDocument window = JsonDocument.Parse("{\"command\":\"hideWindow\",\"targetId\":\"widget-1\"}");
        AssertTrue(RendererJsBridge.TryParseRuntimeCommand(window.RootElement, out _, out RendererCommand hide, out _));
        AssertEqual(RendererCommandKind.HideWindow, hide.Kind);
        AssertEqual("widget-1", hide.TargetId);

        using JsonDocument group = JsonDocument.Parse("{\"command\":\"pause\",\"all\":true}");
        AssertTrue(RendererJsBridge.TryParseRuntimeCommand(group.RootElement, out _, out RendererCommand pause, out _));
        AssertEqual(RendererCommandKind.Pause, pause.Kind);
        AssertTrue(pause.All);

        using JsonDocument invalidTarget = JsonDocument.Parse("{\"command\":\"hideWindow\"}");
        AssertFalse(RendererJsBridge.TryParseRuntimeCommand(invalidTarget.RootElement, out _, out _, out _));
        using JsonDocument conflictingScope = JsonDocument.Parse("{\"command\":\"status\",\"group\":\"desktop\",\"all\":true}");
        AssertFalse(RendererJsBridge.TryParseRuntimeCommand(conflictingScope.RootElement, out _, out _, out _));
        using JsonDocument forbidden = JsonDocument.Parse("{\"command\":\"exit\"}");
        AssertFalse(RendererJsBridge.TryParseRuntimeCommand(forbidden.RootElement, out _, out _, out _));
    }

    private static void ConfigurationRejectsNonAsciiIds()
    {
        string path = WriteTemp("{\"widgets\":[{\"id\":\"café\",\"x\":0,\"y\":0,\"pos-scaled\":false,\"size-scaled\":false,\"anchor\":\"top-left\",\"width\":100,\"height\":100,\"monitor\":1,\"url\":\"example.test\"}]}");
        try { AssertThrows<RendererConfigurationException>(() => RendererConfiguration.Load(new[] { path })); }
        finally { Delete(path); }
    }

    private static void GeometryPreservesNegativeCoordinates()
    {
        var widget = new WidgetDefinition(10, 20, false, false, WidgetAnchor.BottomRight, 100, 50, 1, "https://example.test/", true, "widgets");
        AssertEqual(new Rectangle(-90, -30, 100, 50), WidgetGeometry.Resolve(widget, new Rectangle(-1920, -1200, 1920, 1200)));
    }

    private static void UrlBuilderNormalizesAndRejects()
    {
        AssertEqual("https://example.test/", UrlBuilder.Normalize("example.test"));
        AssertThrows<RendererConfigurationException>(() => UrlBuilder.Normalize("javascript:alert(1)"));
    }

    private static void RegistryTracksLifecycle()
    {
        var registry = new WindowRegistry();
        var window = new AppWindow(new RendererWindowSpec("one", "https://example.test/", new Rectangle(0, 0, 10, 10), new Rectangle(0, 0, 10, 10), "widgets", WindowKind.Widget), TimeSpan.FromSeconds(1));
        registry.Add(window);
        registry.MarkEmbedded(window);
        AssertEqual(AppWindowState.Embedded, window.State);
        registry.MarkIntentionalClose(window);
        registry.MarkClosed(window);
        AssertEqual(WindowCloseIntent.Intentional, window.CloseIntent);
    }

    private static void CommandProtocolRoundTrips()
    {
        string json = RendererCommandProtocol.Serialize(new RendererCommand(RendererCommandKind.Reload, "desktop"));
        AssertTrue(json.Contains("\"version\":1", StringComparison.Ordinal));
        AssertTrue(RendererCommandProtocol.TryParse(json, out RendererCommand? parsed));
        AssertEqual(RendererCommandKind.Reload, parsed!.Kind);
        AssertEqual("desktop", parsed.Group);

        json = RendererCommandProtocol.Serialize(new RendererCommand(RendererCommandKind.SystemTray, TrayAction: SystemTrayAction.Hide));
        AssertTrue(RendererCommandProtocol.TryParse(json, out parsed));
        AssertEqual(RendererCommandKind.SystemTray, parsed!.Kind);
        AssertEqual(SystemTrayAction.Hide, parsed.TrayAction);

        json = RendererCommandProtocol.Serialize(new RendererCommand(RendererCommandKind.ToggleMenu));
        AssertTrue(RendererCommandProtocol.TryParse(json, out parsed));
        AssertEqual(RendererCommandKind.ToggleMenu, parsed!.Kind);

        Guid themeId = Guid.NewGuid();
        Guid activationId = Guid.NewGuid();
        json = RendererCommandProtocol.Serialize(new RendererCommand(
            RendererCommandKind.ApplyConfig,
            ConfigPaths: new[] { "theme.json" },
            ConfigJson: "{\"wallpaper\":[]}",
            ConfigSourcePaths: new[] { "source.theme.json" },
            ThemeId: themeId,
            ThemeActivationId: activationId,
            ThemeGeneration: 7));
        AssertTrue(RendererCommandProtocol.TryParse(json, out parsed));
        AssertEqual(themeId, parsed!.ThemeId);
        AssertEqual("{\"wallpaper\":[]}", parsed.ConfigJson);
        AssertEqual("source.theme.json", parsed.ConfigSourcePaths![0]);
        AssertEqual(activationId, parsed.ThemeActivationId);
        AssertEqual(7L, parsed.ThemeGeneration);
    }

    private static void ControlProtocolRoundTrips()
    {
        string id = Guid.NewGuid().ToString("D");
        AssertTrue(ControlPipeCodec.TryParseRequest(
            $"{{\"id\":\"{id}\",\"method\":\"GET\",\"path\":\"/v1/pipes\",\"body\":null}}",
            out ControlRequest? request,
            out ControlError? error));
        AssertEqual(null, error);
        AssertEqual("GET", request!.Method);
        AssertEqual("/v1/pipes", request.Path);

        string response = ControlPipeCodec.Serialize(new ControlResponse(id, 200, new { ok = true }));
        AssertTrue(ControlPipeCodec.TryParseResponse(response, out ControlResponse? parsed));
        AssertEqual(id, parsed!.Id);
        AssertEqual(200, parsed.Status);

        string eventJson = ControlPipeCodec.SerializeEvent("window.changed", new { id = "widget-1" });
        AssertTrue(ControlPipeCodec.TryParseEvent(eventJson, out ControlEvent? controlEvent));
        AssertEqual("window.changed", controlEvent!.Name);
    }

    private static void CommandProtocolRejectsOversizedFrame()
    {
        AssertFalse(RendererCommandCodec.TryParse(
            new string('x', RendererCommandCodec.MaxFrameBytes + 1),
            out _));
    }

    private static void ControlProtocolAcceptsUnmatchedError()
    {
        string error = "{\"id\":null,\"status\":400,\"error\":{\"code\":\"bad\",\"message\":\"invalid request\"}}";
        AssertTrue(ControlPipeCodec.TryParseResponse(error, out ControlResponse? response));
        AssertEqual<string?>(null, response!.Id);
        AssertFalse(ControlPipeCodec.TryParseResponse("{\"id\":null,\"status\":200}", out _));
    }

    private static void BoundedReaderPreservesFrames()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("first\r\nsecond\n"));
        var reader = new BoundedLineReader(stream);
        BoundedLine first = reader.ReadAsync(64, CancellationToken.None).GetAwaiter().GetResult();
        BoundedLine second = reader.ReadAsync(64, CancellationToken.None).GetAwaiter().GetResult();
        BoundedLine end = reader.ReadAsync(64, CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual("first", first.Text);
        AssertEqual("second", second.Text);
        AssertTrue(end.EndOfStream);
    }

    private static void BoundedReaderRejectsOversizedFrame()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("123456\nok\n"));
        var reader = new BoundedLineReader(stream);
        BoundedLine oversized = reader.ReadAsync(5, CancellationToken.None).GetAwaiter().GetResult();
        BoundedLine next = reader.ReadAsync(5, CancellationToken.None).GetAwaiter().GetResult();
        AssertTrue(oversized.TooLarge);
        AssertEqual("ok", next.Text);
    }

    private static void BackendQueueReportsOverflowPerBurst()
    {
        var queue = new BackendEventQueue();
        int overflows = 0;
        for (int index = 0; index < 257; index++)
            queue.Enqueue(new BackendEvent("stream.data", "stream", "json", null), _ => overflows++);
        AssertEqual(1, overflows);
        queue.Drain();
        for (int index = 0; index < 257; index++)
            queue.Enqueue(new BackendEvent("stream.data", "stream", "json", null), _ => overflows++);
        AssertEqual(2, overflows);
    }

    private static void LoggingPolicyParsesAndBounds()
    {
        string json = "{\"enabled\":true,\"maxEvents\":4,\"maxBytes\":8192,\"flushIntervalMs\":500}";
        using JsonDocument document = JsonDocument.Parse(json);
        AssertTrue(RendererLogPolicy.TryParse(document.RootElement, new RendererLogPolicy(), out RendererLogPolicy policy, out string error));
        AssertEqual("", error);
        AssertTrue(policy.Enabled);
        AssertEqual(4, policy.MaxEvents);
        AssertEqual(8192, policy.MaxBytes);
        AssertEqual(500, policy.FlushIntervalMs);
    }

    private static void LoggingPolicyRejectsInvalid()
    {
        using JsonDocument document = JsonDocument.Parse("{\"maxEvents\":0}");
        AssertFalse(RendererLogPolicy.TryParse(document.RootElement, new RendererLogPolicy(), out _, out string error));
        AssertTrue(error.Contains("maxEvents", StringComparison.Ordinal));
    }

    private static void LoggerUsesConsoleWithoutController()
    {
        var output = new List<RendererLogEvent>();
        using var logger = new RendererLogger(consoleSink: output.Add);
        logger.Info("renderer.ready");
        AssertEqual(1, output.Count);
        AssertEqual("renderer.ready", output[0].EventName);
    }

    private static void LoggerRedactsProperties()
    {
        var output = new List<RendererLogEvent>();
        using var logger = new RendererLogger(consoleSink: output.Add);
        logger.Info("runtime.error", new Dictionary<string, string?>
        {
            ["group"] = "private-group",
            ["path"] = "C:\\private\\file",
            ["exceptionType"] = "InvalidOperationException"
        });
        AssertTrue(output[0].Properties["group"]!.StartsWith("sha256:", StringComparison.Ordinal));
        AssertEqual("[redacted]", output[0].Properties["path"]);
        AssertEqual("InvalidOperationException", output[0].Properties["exceptionType"]);
    }

    private static void LoggerBatchesThroughController()
    {
        var transport = new FakeLogTransport();
        using var logger = new RendererLogger(transport, _ => { });
        using JsonDocument document = JsonDocument.Parse("{\"maxEvents\":1}");
        AssertTrue(logger.TryApplyPolicy(document.RootElement, out _, out int status, out _));
        AssertEqual(200, status);
        logger.Info("command.received");
        AssertTrue(SpinWait.SpinUntil(() => transport.Requests.Count > 0, 2000));
        AssertEqual("/v1/logs", transport.Requests[0].Path);
        AssertEqual(1, transport.Requests[0].Batch.Events.Count);
    }

    private static void LoggerDisableFlushesBeforeDisabling()
    {
        var transport = new FakeLogTransport();
        using var logger = new RendererLogger(transport, _ => { });
        logger.Info("renderer.ready");
        using JsonDocument document = JsonDocument.Parse("{\"enabled\":false}");
        AssertTrue(logger.TryApplyPolicy(document.RootElement, out RendererLogPolicy policy, out int status, out _));
        AssertEqual(200, status);
        AssertFalse(policy.Enabled);
        AssertTrue(transport.Requests.Any(request => request.Path == "/v1/logs"));
    }

    private static void UltralightDataBindingAbi()
    {
        UltralightNative.ConfigureLibraryDirectory(AppContext.BaseDirectory);
        IntPtr context = UltralightNative.ulCreateDOMDataContext();
        if (context == IntPtr.Zero)
            throw new InvalidOperationException("Ultralight data context could not be created.");

        IntPtr builder = IntPtr.Zero;
        IntPtr binding = IntPtr.Zero;
        try
        {
            builder = UltralightNative.ulCreateDOMDataTypeBuilder("AetherTests");
            if (builder == IntPtr.Zero ||
                !UltralightNative.ulDOMDataTypeBuilderAddField(builder, "value", 2, 0))
            {
                throw new InvalidOperationException("Ultralight data type builder rejected the test field.");
            }

            IntPtr type = UltralightNative.ulDOMDataTypeBuilderBuild(builder);
            if (type == IntPtr.Zero)
                throw new InvalidOperationException("Ultralight data type could not be built.");

            binding = UltralightNative.ulDOMDataContextBind(
                context,
                "aetherTest",
                type,
                AbiWalkCallback,
                new IntPtr(1),
                AbiAcquireCallback,
                IntPtr.Zero,
                null);
            if (binding == IntPtr.Zero)
                throw new InvalidOperationException("Ultralight data binding could not be created.");

            _abiWalkValue = double.NaN;
            UltralightNative.ulDOMDataContextSync(context);
            AssertEqual(42.5, _abiWalkValue);
        }
        finally
        {
            if (binding != IntPtr.Zero)
                UltralightNative.ulDestroyDOMDataBinding(binding);
            if (builder != IntPtr.Zero)
                UltralightNative.ulDestroyDOMDataTypeBuilder(builder);
            UltralightNative.ulDestroyDOMDataContext(context);
        }
    }

    private static double _abiWalkValue;
    private static readonly UltralightNative.DomDataSyncWalkCallback AbiWalkCallback = AbiWalk;
    private static readonly UltralightNative.DomDataAcquireCallback AbiAcquireCallback = AbiAcquire;

    private static void AbiWalk(IntPtr record, IntPtr instance)
    {
        if (record != IntPtr.Zero && instance == new IntPtr(1))
        {
            UltralightNative.ulDOMDataRecordSetDouble(record, 0, 42.5);
            _abiWalkValue = UltralightNative.ulDOMDataRecordGetDouble(record, 0);
        }
    }

    private static IntPtr AbiAcquire(IntPtr holderState) => holderState;

    private sealed class FakeLogTransport : IRendererLogTransport
    {
        public string State { get; private set; } = "connected";
        public event Action? Disconnected;
        public List<(string Path, RendererLogBatch Batch)> Requests { get; } = new();

        public Task<ControlResponse> SendAsync(string method, string path, object? body, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (body is RendererLogBatch batch)
                Requests.Add((path, batch));
            return Task.FromResult(new ControlResponse(Guid.NewGuid().ToString("D"), 202, new { accepted = 1 }));
        }

        public void Disconnect()
        {
            State = "disconnected";
            Disconnected?.Invoke();
        }
    }

    private static string WriteTemp(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ultralight-renderer-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void AssertTrue(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    private static void AssertFalse(bool value)
    {
        if (value) throw new InvalidOperationException("Expected false.");
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

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
            (nameof(OptionsRejectMenuCommands), OptionsRejectMenuCommands),
            (nameof(OptionsParseSystemTrayCommands), OptionsParseSystemTrayCommands),
            (nameof(RendererDataUsesLocalAppData), RendererDataUsesLocalAppData),
            (nameof(ConfigurationMergesDistinctSections), ConfigurationMergesDistinctSections),
            (nameof(ConfigurationParsesNativeImageWidgets), ConfigurationParsesNativeImageWidgets),
            (nameof(ConfigurationParsesVariablesAndCues), ConfigurationParsesVariablesAndCues),
            (nameof(ConfigurationRejectsInvalidCues), ConfigurationRejectsInvalidCues),
            (nameof(ConfigurationLoadsInlineJson), ConfigurationLoadsInlineJson),
            (nameof(ConfigurationRejectsDuplicateSections), ConfigurationRejectsDuplicateSections),
            (nameof(ConfigurationAppliesDefaultGroups), ConfigurationAppliesDefaultGroups),
            (nameof(ConfigurationParsesGenericWindowPolicies), ConfigurationParsesGenericWindowPolicies),
            (nameof(ConfigurationRejectsLegacyGenericWindowShape), ConfigurationRejectsLegacyGenericWindowShape),
            (nameof(ConfigurationAppliesGenericWindowDefaults), ConfigurationAppliesGenericWindowDefaults),
            (nameof(ConfigurationParsesInitialLifecycleStates), ConfigurationParsesInitialLifecycleStates),
            (nameof(ConfigurationInspectionUsesNestedTerminology), ConfigurationInspectionUsesNestedTerminology),
            (nameof(ConfigurationSupportsExplicitGroups), ConfigurationSupportsExplicitGroups),
            (nameof(ConfigurationRejectsUnknownFields), ConfigurationRejectsUnknownFields),
            (nameof(ConfigurationRejectsMenuSection), ConfigurationRejectsMenuSection),
            (nameof(ConfigurationRejectsInvalidRuntimeCommands), ConfigurationRejectsInvalidRuntimeCommands),
            (nameof(RuntimeCommandPayloadsMapAndValidate), RuntimeCommandPayloadsMapAndValidate),
            (nameof(ConfigurationRejectsNonAsciiIds), ConfigurationRejectsNonAsciiIds),
            (nameof(GeometryPreservesNegativeCoordinates), GeometryPreservesNegativeCoordinates),
            (nameof(GeometryPreservesScaledWidgetAnchorSemantics), GeometryPreservesScaledWidgetAnchorSemantics),
            (nameof(GifCompositorAppliesDisposalAndDirtyRegion), GifCompositorAppliesDisposalAndDirtyRegion),
            (nameof(GifDecoderReadsSingleFrame), GifDecoderReadsSingleFrame),
            (nameof(GifSchedulerInvalidatesRemovedGeneration), GifSchedulerInvalidatesRemovedGeneration),
            (nameof(GifSchedulerCleansUpCompletedRegistration), GifSchedulerCleansUpCompletedRegistration),
            (nameof(GifTimingPreservesActiveStartup), GifTimingPreservesActiveStartup),
            (nameof(GifTimingPreservesPausedStartup), GifTimingPreservesPausedStartup),
            (nameof(GifTimingPreservesRemainingOnResume), GifTimingPreservesRemainingOnResume),
            (nameof(DesktopAdaptersPreserveSharedPageData), DesktopAdaptersPreserveSharedPageData),
            (nameof(UrlBuilderNormalizesAndRejects), UrlBuilderNormalizesAndRejects),
            (nameof(RegistryTracksLifecycle), RegistryTracksLifecycle),
            (nameof(CommandProtocolRoundTrips), CommandProtocolRoundTrips)
            ,(nameof(CommandProtocolRejectsOversizedFrame), CommandProtocolRejectsOversizedFrame)
            ,(nameof(ControlProtocolRoundTrips), ControlProtocolRoundTrips)
            ,(nameof(ControlProtocolAcceptsUnmatchedError), ControlProtocolAcceptsUnmatchedError)
            ,(nameof(BackendProtocolRoundTrips), BackendProtocolRoundTrips)
            ,(nameof(BackendProtocolRejectsInvalidFrames), BackendProtocolRejectsInvalidFrames)
            ,(nameof(BackendSchemaValidatesPayloads), BackendSchemaValidatesPayloads)
            ,(nameof(BoundedReaderPreservesFrames), BoundedReaderPreservesFrames)
            ,(nameof(BoundedReaderRejectsOversizedFrame), BoundedReaderRejectsOversizedFrame)
            ,(nameof(BackendQueueReportsOverflowPerBurst), BackendQueueReportsOverflowPerBurst)
            ,(nameof(LoggingPolicyParsesAndBounds), LoggingPolicyParsesAndBounds)
            ,(nameof(LoggingPolicyRejectsInvalid), LoggingPolicyRejectsInvalid)
            ,(nameof(LoggerUsesConsoleWithoutController), LoggerUsesConsoleWithoutController)
            ,(nameof(LoggerRedactsProperties), LoggerRedactsProperties)
            ,(nameof(LoggerBatchesThroughController), LoggerBatchesThroughController)
            ,(nameof(LoggerDisableFlushesBeforeDisabling), LoggerDisableFlushesBeforeDisabling)
            ,(nameof(MultipleBackendsResolveIndependentLogicalPipes), MultipleBackendsResolveIndependentLogicalPipes)
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
        AssertEqual(0, configuration.Windows.Count);
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

    private static void OptionsRejectMenuCommands()
    {
        AssertThrows<OptionException>(() => HostOptions.Parse(new[] { "--toggle-menu" }));
        AssertThrows<OptionException>(() => HostOptions.Parse(new[] { "--list", "menu" }));
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

    private static void ConfigurationParsesNativeImageWidgets()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"aether-image-widget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string asset = Path.Combine(directory, "character.gif");
        string config = Path.Combine(directory, "theme.json");
        File.WriteAllBytes(asset, Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw=="));
        File.WriteAllText(config, "{\"image-widgets\":[{\"source\":\"character.gif\",\"placement\":{\"width\":64,\"height\":64}}]}");
        try
        {
            RendererConfiguration configuration = RendererConfiguration.Load(new[] { config });
            ImageWidgetDefinition widget = configuration.ImageWidgets.Single();
            AssertEqual("image-widget-1", widget.Id);
            AssertEqual(Path.GetFullPath(asset), widget.SourcePath);
            AssertEqual("image-widgets", widget.RuntimeGroup);
            AssertEqual(ImageAssetType.Gif, widget.AssetType);
            AssertTrue(widget.ClickThrough);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void GifCompositorAppliesDisposalAndDirtyRegion()
    {
        var compositor = new GifCompositor(2, 1);
        var red = new GifFrame(new PixelRect(0, 0, 1, 1), new byte[] { 0, 0, 255, 255 },
            GifDisposalMethod.RestoreBackground, 100);
        GifCompositionResult first = compositor.Compose(red);
        AssertEqual(new PixelRect(0, 0, 1, 1), first.DirtyRegion);

        var blue = new GifFrame(new PixelRect(1, 0, 1, 1), new byte[] { 255, 0, 0, 255 },
            GifDisposalMethod.DoNotDispose, 100);
        GifCompositionResult second = compositor.Compose(blue);
        AssertEqual(new PixelRect(0, 0, 2, 1), second.DirtyRegion);
        AssertEqual((byte)0, compositor.Canvas.Span[3]);
        AssertEqual((byte)255, compositor.Canvas.Span[4 + 3]);
    }

    private static void GifDecoderReadsSingleFrame()
    {
        string path = Path.Combine(Path.GetTempPath(), $"aether-test-{Guid.NewGuid():N}.gif");
        File.WriteAllBytes(path, Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw=="));
        try
        {
            using GifAnimation animation = GifAnimation.Load(path);
            GifFrame frame = animation.DecodeFrame(0);
            AssertEqual(1, animation.Width);
            AssertEqual(1, animation.Height);
            AssertEqual(1, animation.FrameCount);
            AssertEqual(4, frame.Pixels.Length);
            AssertEqual(255, frame.Pixels.Span[3]);
            AssertEqual(1, animation.RepeatCount);
        }
        finally { Delete(path); }
    }

    private static void ConfigurationParsesVariablesAndCues()
    {
        using JsonDocument document = JsonDocument.Parse("{\"variables\":{\"theme\":\"dark\",\"count\":2,\"nested\":{\"ok\":true}},\"cues\":[\"menu.toggle\",\"next\"]}");
        RendererConfiguration configuration = RendererConfiguration.LoadJson(document.RootElement);
        AssertEqual("dark", configuration.Variables["theme"].GetString());
        AssertEqual(2, configuration.Variables["count"].GetInt32());
        AssertTrue(configuration.Variables["nested"].GetProperty("ok").GetBoolean());
        AssertEqual(2, configuration.Cues.Count);
        AssertEqual("menu.toggle", configuration.Cues[0]);
        AssertEqual("next", configuration.Cues[1]);
    }

    private static void ConfigurationRejectsInvalidCues()
    {
        using JsonDocument duplicate = JsonDocument.Parse("{\"cues\":[\"next\",\"next\"]}");
        using JsonDocument invalid = JsonDocument.Parse("{\"cues\":[\"bad cue\"]}");
        AssertThrows<RendererConfigurationException>(() => RendererConfiguration.LoadJson(duplicate.RootElement));
        AssertThrows<RendererConfigurationException>(() => RendererConfiguration.LoadJson(invalid.RootElement));
    }

    private static void ConfigurationAppliesDefaultGroups()
    {
        string path = WriteTemp("{\"widgets\":[{\"x\":0,\"y\":0,\"pos-scaled\":false,\"size-scaled\":false,\"anchor\":\"top-left\",\"width\":100,\"height\":100,\"monitor\":1,\"url\":\"example.test\"}],\"windows\":[{\"id\":\"generic\",\"geometry\":{\"position\":{\"x\":0,\"y\":0,\"unit\":\"pixels\"},\"size\":{\"width\":100,\"height\":100,\"unit\":\"pixels\"}},\"url\":\"https://example.test/\"}]}");
        try
        {
            RendererConfiguration configuration = RendererConfiguration.Load(new[] { path });
            AssertEqual("widgets", configuration.Widgets[0].RuntimeGroup);
            AssertEqual("windows", configuration.Windows[0].RuntimeGroup);
        }
        finally { Delete(path); }
    }

    private static void ConfigurationSupportsExplicitGroups()
    {
        string path = WriteTemp("{\"wallpaper\":[{\"url\":\"example.test\",\"thread-group\":\"desktop\"}],\"widgets\":[]}");
        try
        {
            RendererConfiguration configuration = RendererConfiguration.Load(new[] { path });
            AssertEqual("desktop", configuration.Wallpapers[0].RuntimeGroup);
        }
        finally { Delete(path); }
    }

    private static void ConfigurationParsesGenericWindowPolicies()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {"windows":[{"id":"panel","url":"file:///panel.html",
              "geometry":{"position":{"x":10,"y":20,"unit":"percent"},"size":{"width":40,"height":50,"unit":"percent"}},
              "surface":{"kind":"top-level","z-order":"topmost-when-focused","framed":true,"resizable":true,
                "show-in-taskbar":true,"work-area":{"enabled":true,"edge":"top","thickness":32}},
              "lifecycle":{"initial":{"visibility":"hidden","runtime":"paused"},"hidden-behavior":"pause","pause-on-fullscreen":false},
              "input":{"mouse":"native","keyboard":"none","focus":"on-show"},
              "notifications":{"focus":true,"visibility":true}}]}
            """);
        GenericWindowDefinition window = RendererConfiguration.LoadJson(document.RootElement).Windows.Single();
        AssertEqual("panel", window.Id);
        AssertEqual(WindowSurfaceKind.TopLevel, window.Surface.Kind);
        AssertEqual(WindowZOrder.TopmostWhenFocused, window.Surface.ZOrder);
        AssertTrue(window.Surface.Framed);
        AssertTrue(window.Surface.Resizable);
        AssertEqual(WindowHiddenBehavior.Pause, window.Lifecycle.HiddenBehavior);
        AssertEqual(WindowInitialVisibility.Hidden, window.Lifecycle.InitialVisibility);
        AssertEqual(WindowInitialRuntime.Paused, window.Lifecycle.InitialRuntime);
        AssertFalse(window.Lifecycle.SuspendOnFullscreen);
        AssertEqual(WindowInputMode.None, window.Input.Keyboard);
        AssertEqual(WindowFocusMode.OnShow, window.Input.Focus);
        AssertTrue(window.Notifications.Focus);
        AssertTrue(window.Surface.WorkArea!.Enabled);
        AssertEqual(32, window.Surface.WorkArea.Thickness);
    }

    private static void ConfigurationRejectsLegacyGenericWindowShape()
    {
        using JsonDocument flat = JsonDocument.Parse(
            "{\"windows\":[{\"id\":\"one\",\"url\":\"file:///one.html\",\"x\":0,\"y\":0,\"width\":100,\"height\":100}]}" );
        using JsonDocument alias = JsonDocument.Parse(
            "{\"windows\":[{\"id\":\"one\",\"url\":\"file:///one.html\",\"geometry\":{\"position\":{\"x\":0,\"y\":0,\"unit\":\"pixels\"},\"size\":{\"width\":100,\"height\":100,\"unit\":\"pixels\"}},\"surface\":{\"placement\":\"top-level\"}}]}" );
        using JsonDocument stringNumber = JsonDocument.Parse(
            "{\"windows\":[{\"id\":\"one\",\"url\":\"file:///one.html\",\"geometry\":{\"position\":{\"x\":\"0\",\"y\":0,\"unit\":\"pixels\"},\"size\":{\"width\":100,\"height\":100,\"unit\":\"pixels\"}}}]}" );

        AssertThrows<RendererConfigurationException>(() => RendererConfiguration.LoadJson(flat.RootElement));
        AssertThrows<RendererConfigurationException>(() => RendererConfiguration.LoadJson(alias.RootElement));
        AssertThrows<RendererConfigurationException>(() => RendererConfiguration.LoadJson(stringNumber.RootElement));
    }

    private static void ConfigurationAppliesGenericWindowDefaults()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {"windows":[{"id":"defaulted","url":"https://example.test/","geometry":{
              "position":{"x":0,"y":0,"unit":"pixels"},"size":{"width":320,"height":180,"unit":"pixels"}}}]}
            """);
        GenericWindowDefinition window = RendererConfiguration.LoadJson(document.RootElement).Windows.Single();
        AssertEqual("windows", window.RuntimeGroup);
        AssertEqual(WindowSurfaceKind.TopLevel, window.Surface.Kind);
        AssertEqual(WindowZOrder.Normal, window.Surface.ZOrder);
        AssertEqual(WindowInitialVisibility.Visible, window.Lifecycle.InitialVisibility);
        AssertEqual(WindowHiddenBehavior.Retain, window.Lifecycle.HiddenBehavior);
        AssertEqual(WindowInitialRuntime.Active, window.Lifecycle.InitialRuntime);
        AssertTrue(window.Lifecycle.SuspendOnFullscreen);
        AssertFalse(window.Surface.Transparent);
    }

    private static void ConfigurationInspectionUsesNestedTerminology()
    {
        var spec = new RendererWindowSpec(
            "inspection",
            "https://example.test/",
            new Rectangle(10, 20, 300, 200),
            new Rectangle(0, 0, 1920, 1080),
            "runtime",
            WindowKind.Generic,
            new WindowGeometry(10, 20, 300, 200, 1, "monitor", WidgetAnchor.TopLeft, "pixels", "pixels"),
            new WindowSurface(WindowSurfaceKind.TopLevel, "Inspection", WindowZOrder.Normal),
            new WindowLifecycle(WindowInitialVisibility.Visible, WindowHiddenBehavior.Retain, true),
            1,
            Input: new WindowInputPolicy());
        string specJson = ControlPipeCodec.SerializeJson(spec);
        AssertTrue(specJson.Contains("\"runtimeGroup\"", StringComparison.Ordinal));
        AssertTrue(specJson.Contains("\"geometry\"", StringComparison.Ordinal));
        AssertTrue(specJson.Contains("\"surface\"", StringComparison.Ordinal));
        AssertTrue(specJson.Contains("\"lifecycle\"", StringComparison.Ordinal));
        AssertFalse(specJson.Contains("\"threadGroup\"", StringComparison.Ordinal));
        AssertFalse(specJson.Contains("\"placement\"", StringComparison.Ordinal));

        var snapshot = new WindowSnapshot(
            "inspection", "https://example.test/", "runtime", WindowKind.Generic,
            new Rectangle(10, 20, 300, 200), new Rectangle(10, 20, 300, 200), 1,
            true, false, false, false, AppWindowState.Embedded, WindowSurfaceKind.TopLevel, WindowZOrder.Normal);
        string snapshotJson = ControlPipeCodec.SerializeJson(snapshot);
        AssertTrue(snapshotJson.Contains("\"runtimeGroup\"", StringComparison.Ordinal));
        AssertTrue(snapshotJson.Contains("\"surfaceKind\"", StringComparison.Ordinal));
        AssertFalse(snapshotJson.Contains("\"group\"", StringComparison.Ordinal));
        AssertFalse(snapshotJson.Contains("\"placement\"", StringComparison.Ordinal));

        RendererWindowContract contract = RendererContractMapper.ToContract(snapshot);
        string contractJson = JsonSerializer.Serialize(contract, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        AssertTrue(contractJson.Contains("\"kind\":\"generic\"", StringComparison.Ordinal));
        AssertTrue(contractJson.Contains("\"state\":\"embedded\"", StringComparison.Ordinal));
        AssertFalse(contractJson.Contains("\"kind\":0", StringComparison.Ordinal));
    }

    private static void ConfigurationParsesInitialLifecycleStates()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {"windows":[
              {"id":"paused","url":"https://example.test/","geometry":{"position":{"x":0,"y":0,"unit":"pixels"},"size":{"width":100,"height":100,"unit":"pixels"}},"lifecycle":{"initial":{"visibility":"shown","runtime":"paused"}}},
              {"id":"unloaded","url":"https://example.test/","geometry":{"position":{"x":0,"y":0,"unit":"pixels"},"size":{"width":100,"height":100,"unit":"pixels"}},"lifecycle":{"initial":{"visibility":"hidden","runtime":"unloaded"}}}
            ]}
            """);
        IReadOnlyList<GenericWindowDefinition> windows = RendererConfiguration.LoadJson(document.RootElement).Windows;
        AssertEqual(WindowInitialRuntime.Paused, windows[0].Initial.Runtime);
        AssertEqual(WindowInitialVisibility.Shown, windows[0].Initial.Visibility);
        AssertEqual(WindowInitialRuntime.Unloaded, windows[1].Initial.Runtime);

        using JsonDocument invalid = JsonDocument.Parse("""
            {"windows":[{"id":"invalid","url":"https://example.test/","geometry":{"position":{"x":0,"y":0,"unit":"pixels"},"size":{"width":100,"height":100,"unit":"pixels"}},"lifecycle":{"initial":{"visibility":"shown","runtime":"unloaded"}}}]}
            """);
        AssertThrows<RendererConfigurationException>(() => RendererConfiguration.LoadJson(invalid.RootElement));
    }

    private static void ConfigurationRejectsUnknownFields()
    {
        string path = WriteTemp("{\"widgets\":[],\"unknown\":[]}");
        try { AssertThrows<RendererConfigurationException>(() => RendererConfiguration.Load(new[] { path })); }
        finally { Delete(path); }
    }

    private static void ConfigurationRejectsMenuSection()
    {
        string path = WriteTemp("{\"menu\":{\"id\":\"main.menu\",\"x\":0,\"y\":0,\"width\":100,\"height\":100,\"url\":\"file:///menu.html\"}}");
        try { AssertThrows<RendererConfigurationException>(() => RendererConfiguration.Load(new[] { path })); }
        finally { Delete(path); }
    }

    private static void ConfigurationRejectsInvalidRuntimeCommands()
    {
        string unknown = WriteTemp("{\"windows\":[{\"id\":\"one\",\"x\":0,\"y\":0,\"width\":100,\"height\":100,\"url\":\"file:///one.html\",\"bridge\":{\"runtime-commands\":[\"exit\"]}}]}");
        string duplicate = WriteTemp("{\"windows\":[{\"id\":\"one\",\"x\":0,\"y\":0,\"width\":100,\"height\":100,\"url\":\"file:///one.html\",\"bridge\":{\"runtime-commands\":[\"reload\",\"reload\"]}}]}");
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
        AssertFalse(RendererJsBridge.TryParseRuntimeCommand(menu.RootElement, out _, out _, out _));

        using JsonDocument window = JsonDocument.Parse("{\"command\":\"hideWindow\",\"targetId\":\"widget-1\"}");
        AssertTrue(RendererJsBridge.TryParseRuntimeCommand(window.RootElement, out _, out RendererCommand hide, out _));
        AssertEqual(RendererCommandKind.HideWindow, hide.Kind);
        AssertEqual("widget-1", hide.TargetId);

        using JsonDocument unload = JsonDocument.Parse("{\"command\":\"unloadWindow\",\"targetId\":\"widget-1\"}");
        AssertTrue(RendererJsBridge.TryParseRuntimeCommand(unload.RootElement, out _, out RendererCommand unloadCommand, out _));
        AssertEqual(RendererCommandKind.UnloadWindow, unloadCommand.Kind);

        using JsonDocument reloadWindow = JsonDocument.Parse("{\"command\":\"reloadWindow\",\"targetId\":\"widget-1\"}");
        AssertTrue(RendererJsBridge.TryParseRuntimeCommand(reloadWindow.RootElement, out _, out RendererCommand reloadWindowCommand, out _));
        AssertEqual(RendererCommandKind.ReloadWindow, reloadWindowCommand.Kind);

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

    private static void GeometryPreservesScaledWidgetAnchorSemantics()
    {
        var widget = new WidgetDefinition(50, 50, true, false, WidgetAnchor.Center, 100, 50, 1, "https://example.test/", true, "widgets");
        AssertEqual(new Rectangle(910, 515, 100, 50), WidgetGeometry.Resolve(widget, new Rectangle(0, 0, 1920, 1080)));
    }

    private static void DesktopAdaptersPreserveSharedPageData()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "variables": { "theme": "dark" },
              "cues": ["next"],
              "widgets": [{
                "x": 50, "y": 50, "pos-scaled": true, "size-scaled": false,
                "anchor": "center", "width": 100, "height": 50, "monitor": 1,
                "url": "example.test"
              }]
            }
            """);

        RendererWindowSpec spec = RendererConfiguration.LoadJson(document.RootElement).ResolveWindowSpecs().Single();
        AssertEqual("dark", spec.Variables!["theme"].GetString());
        AssertEqual("next", spec.Cues!.Single());
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

        json = RendererCommandProtocol.Serialize(new RendererCommand(RendererCommandKind.HideWindow, TargetId: "clock"));
        AssertTrue(RendererCommandProtocol.TryParse(json, out parsed));
        AssertEqual(RendererCommandKind.HideWindow, parsed!.Kind);

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

        string cueJson = ControlPipeCodec.SerializeEvent("cue", new { name = "menu.toggle", data = new { source = "test" } });
        AssertTrue(ControlPipeCodec.TryParseEvent(cueJson, out controlEvent));
        AssertEqual("cue", controlEvent!.Name);
        AssertEqual("menu.toggle", controlEvent.Body!.Value.GetProperty("name").GetString());
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

    private static void BackendProtocolRoundTrips()
    {
        string id = Guid.NewGuid().ToString("D");
        BackendRequest request = new(id, "POST", "/v1/request", BackendCodec.SerializeBody(new { value = 3 }));
        AssertTrue(BackendCodec.TryParseRequest(BackendCodec.SerializeRequest(request), out BackendRequest? parsedRequest, out BackendError? requestError));
        AssertTrue(requestError == null);
        AssertEqual(id, parsedRequest!.Id);
        AssertEqual("POST", parsedRequest.Method);
        AssertEqual(3, parsedRequest.Body!.Value.GetProperty("value").GetInt32());

        BackendResponse response = BackendResponse.FromObject(id, 200, new { ok = true });
        AssertTrue(BackendCodec.TryParseResponse(BackendCodec.SerializeResponse(response), out BackendResponse? parsedResponse));
        AssertEqual(id, parsedResponse!.Id);
        AssertTrue(parsedResponse.Body!.Value.GetProperty("ok").GetBoolean());

        BackendEventFrame value = BackendEventFrame.FromObject("stream.data", "stream-1", "json", new { value = 4.5 });
        AssertTrue(BackendCodec.TryParseEvent(BackendCodec.SerializeEvent(value), out BackendEventFrame? parsedEvent));
        AssertEqual("stream.data", parsedEvent!.Name);
        AssertEqual("stream-1", parsedEvent.StreamId);
        AssertEqual(4.5, parsedEvent.Data!.Value.GetProperty("value").GetDouble());
    }

    private static void BackendProtocolRejectsInvalidFrames()
    {
        AssertFalse(BackendCodec.TryParseRequest("not-json", out _, out _));
        AssertFalse(BackendCodec.TryParseRequest(
            "{\"id\":\"not-a-uuid\",\"method\":\"GET\",\"path\":\"/v1/test\"}",
            out _,
            out BackendError? error));
        AssertEqual("request_id_invalid", error!.Code);
        AssertFalse(BackendCodec.TryParseResponse("{\"id\":null,\"status\":200}", out _));
        AssertFalse(BackendCodec.TryParseResponse(
            $"{{\"id\":\"{Guid.NewGuid():D}\",\"status\":400,\"body\":{{}},\"error\":{{\"code\":\"bad\",\"message\":\"bad\"}}}}",
            out _));
        AssertFalse(BackendCodec.TryParseEvent("{\"id\":\"not-null\",\"event\":\"stream.data\",\"streamId\":\"s\",\"type\":\"json\"}", out _));
        AssertFalse(BackendCodec.TryParseRequest(new string('x', BackendCodec.MaxFrameBytes + 1), out _, out _));
    }

    private static void BackendSchemaValidatesPayloads()
    {
        using JsonDocument document = JsonDocument.Parse("{\"kind\":\"object\",\"fields\":{\"value\":{\"kind\":\"number\"}}}");
        JsonSchemaDescriptor schema = JsonSchemaDescriptor.Parse(document.RootElement);
        AssertTrue(schema.Matches(BackendCodec.SerializeBody(new { value = 1.5 })));
        AssertFalse(schema.Matches(BackendCodec.SerializeBody(new { value = "wrong" })));
        AssertFalse(schema.Matches(BackendCodec.SerializeBody(new { value = 1.5, extra = true })));
        AssertThrows<InvalidOperationException>(() => JsonSchemaDescriptor.Parse(JsonDocument.Parse("{\"kind\":\"number\",\"extra\":true}").RootElement));
    }

    private static void MultipleBackendsResolveIndependentLogicalPipes()
    {
        var spec = new RendererWindowSpec(
            "multi-backend",
            "file:///multi-backend.html",
            new Rectangle(0, 0, 10, 10),
            new Rectangle(0, 0, 10, 10),
            "widgets",
            WindowKind.Widget,
            Bridge: new BridgePolicy(
                true,
                new[] { "minimal-a", "minimal-b" },
                Array.Empty<string>(),
                Array.Empty<string>()));
        using var bridge = new RendererJsBridge(spec, action => { action(); return true; });

        LogicalBackendPipe first = bridge.ResolveBackendPipe("minimal-a");
        LogicalBackendPipe second = bridge.ResolveBackendPipe("minimal-b");

        AssertEqual("multi-backend:minimal-a", first.PipeId);
        AssertEqual("renderer://backend/minimal-a", first.PipeName);
        AssertEqual("minimal-a", first.BackendId);
        AssertEqual("unavailable", first.State);
        AssertEqual("multi-backend:minimal-b", second.PipeId);
        AssertEqual("renderer://backend/minimal-b", second.PipeName);
        AssertEqual("minimal-b", second.BackendId);
        AssertThrows<InvalidOperationException>(() => bridge.ResolveBackendPipe("minimal-c"));
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

    private static void GifSchedulerInvalidatesRemovedGeneration()
    {
        var scheduler = new GifDeadlineScheduler();
        var client = new FakeGifDeadlineClient();

        scheduler.Schedule(client, 10);
        scheduler.Remove(client);
        scheduler.Schedule(client, 20);

        AssertTrue(scheduler.HasEntries);
        AssertEqual(20L, scheduler.NextDeadline);
        AssertEqual(0, scheduler.PresentDue(19));
        AssertEqual(0, client.PresentCount);
        AssertEqual(1, scheduler.PresentDue(20));
        AssertEqual(1, client.PresentCount);
    }

    private static void GifSchedulerCleansUpCompletedRegistration()
    {
        var scheduler = new GifDeadlineScheduler();
        var client = new FakeGifDeadlineClient { NextDeadlineToReturn = 0 };

        scheduler.Schedule(client, 10);
        AssertTrue(scheduler.HasEntries);
        AssertEqual(1, scheduler.PresentDue(10));
        AssertFalse(scheduler.HasEntries);
        AssertEqual(null, scheduler.NextDeadline);

        scheduler.Schedule(client, 20);
        AssertEqual(20L, scheduler.NextDeadline);
    }

    private static void GifTimingPreservesActiveStartup()
    {
        var timing = new GifAnimationTiming();
        timing.Start(1_000, 500);

        AssertEqual(1_500L, timing.NextDeadline);
        AssertFalse(timing.SetPaused(false, 1_100));
        AssertFalse(timing.IsPaused);
        AssertEqual(1_500L, timing.NextDeadline);
    }

    private static void GifTimingPreservesPausedStartup()
    {
        var timing = new GifAnimationTiming();
        timing.Start(1_000, 500);

        AssertTrue(timing.SetPaused(true, 1_000));
        AssertTrue(timing.IsPaused);
        AssertFalse(timing.IsActive);
        AssertTrue(timing.SetPaused(false, 5_000));
        AssertEqual(5_500L, timing.NextDeadline);
    }

    private static void GifTimingPreservesRemainingOnResume()
    {
        var timing = new GifAnimationTiming();
        timing.Start(1_000, 500);

        AssertTrue(timing.SetPaused(true, 1_200));
        AssertFalse(timing.SetPaused(true, 1_300));
        AssertTrue(timing.SetPaused(false, 5_000));
        AssertEqual(5_300L, timing.NextDeadline);
        AssertFalse(timing.SetPaused(false, 6_000));
        AssertEqual(5_300L, timing.NextDeadline);

        timing.Complete();
        AssertFalse(timing.IsActive);
        AssertEqual(0L, timing.NextDeadline);
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

    private sealed class FakeGifDeadlineClient : IGifDeadlineClient
    {
        public bool IsAnimationActive { get; set; } = true;
        public long NextDeadlineToReturn { get; set; }
        public int PresentCount { get; private set; }

        public long PresentDueFrames(long nowTicks)
        {
            PresentCount++;
            return NextDeadlineToReturn;
        }
    }

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

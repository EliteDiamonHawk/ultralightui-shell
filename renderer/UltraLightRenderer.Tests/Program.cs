using System.Drawing;
using UltraLightRenderer;

namespace UltraLightRenderer.Tests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var tests = new (string Name, Action Test)[]
        {
            (nameof(OptionsParseStartupConfigs), OptionsParseStartupConfigs),
            (nameof(OptionsParseNamedCommands), OptionsParseNamedCommands),
            (nameof(OptionsParseAllCommands), OptionsParseAllCommands),
            (nameof(OptionsAllowOmittedGroup), OptionsAllowOmittedGroup),
            (nameof(OptionsRejectMixedCommands), OptionsRejectMixedCommands),
            (nameof(ConfigurationMergesDistinctSections), ConfigurationMergesDistinctSections),
            (nameof(ConfigurationRejectsDuplicateSections), ConfigurationRejectsDuplicateSections),
            (nameof(ConfigurationAppliesDefaultGroups), ConfigurationAppliesDefaultGroups),
            (nameof(ConfigurationSupportsExplicitGroups), ConfigurationSupportsExplicitGroups),
            (nameof(ConfigurationRejectsUnknownFields), ConfigurationRejectsUnknownFields),
            (nameof(GeometryPreservesNegativeCoordinates), GeometryPreservesNegativeCoordinates),
            (nameof(UrlBuilderNormalizesAndRejects), UrlBuilderNormalizesAndRejects),
            (nameof(RegistryTracksLifecycle), RegistryTracksLifecycle),
            (nameof(CommandProtocolRoundTrips), CommandProtocolRoundTrips)
        };

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

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} unit tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void OptionsParseStartupConfigs()
    {
        HostOptions options = HostOptions.Parse(new[] { "--config", "one.json", "--config=two.json", "--verbose" });
        AssertEqual(2, options.ConfigPaths.Count);
        AssertTrue(options.Verbose);
        AssertEqual(RendererCommandKind.None, options.Command.Kind);
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
        string path = WriteTemp("{\"widgets\":[{\"x\":0,\"y\":0,\"pos-scaled\":false,\"size-scaled\":false,\"anchor\":\"top-left\",\"width\":100,\"height\":100,\"monitor\":1,\"url\":\"example.test\"}],\"start-menu\":{\"x\":0,\"y\":0,\"pos-scaled\":false,\"size-scaled\":false,\"anchor\":\"top-left\",\"width\":100,\"height\":100,\"monitor\":1,\"url\":\"example.test\"}}");
        try
        {
            RendererConfiguration configuration = RendererConfiguration.Load(new[] { path });
            AssertEqual("widgets", configuration.Widgets[0].ThreadGroup);
            AssertEqual("start-menu", configuration.StartMenu!.ThreadGroup);
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
        AssertTrue(RendererCommandProtocol.TryParse(json, out RendererCommand? parsed));
        AssertEqual(RendererCommandKind.Reload, parsed!.Kind);
        AssertEqual("desktop", parsed.Group);
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

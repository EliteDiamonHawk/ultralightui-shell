using System.Drawing;
using UltraLightStartMenu;

namespace UltraLightStartMenu.Tests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var tests = new (string Name, Action Test)[]
        {
            (nameof(ConfigurationLoadsRequiredFields), ConfigurationLoadsRequiredFields),
            (nameof(ConfigurationDefaultsOptionalFields), ConfigurationDefaultsOptionalFields),
            (nameof(ConfigurationParsesHiddenBehaviorModes), ConfigurationParsesHiddenBehaviorModes),
            (nameof(ConfigurationRejectsInvalidHiddenBehavior), ConfigurationRejectsInvalidHiddenBehavior),
            (nameof(ConfigurationSupportsDimensionAliases), ConfigurationSupportsDimensionAliases),
            (nameof(ConfigurationRejectsLegacyWidgetArray), ConfigurationRejectsLegacyWidgetArray),
            (nameof(ConfigurationRejectsUnknownFields), ConfigurationRejectsUnknownFields),
            (nameof(ConfigurationRejectsMissingFields), ConfigurationRejectsMissingFields),
            (nameof(ConfigurationRejectsInvalidAnchor), ConfigurationRejectsInvalidAnchor),
            (nameof(ConfigurationRejectsInvalidGeometry), ConfigurationRejectsInvalidGeometry),
            (nameof(ConfigurationRejectsUnsupportedUrl), ConfigurationRejectsUnsupportedUrl),
            (nameof(GeometryResolvesPixelTopLeft), GeometryResolvesPixelTopLeft),
            (nameof(GeometryScalesPositionWithoutScalingSize), GeometryScalesPositionWithoutScalingSize),
            (nameof(GeometryScalesSizeIndependently), GeometryScalesSizeIndependently),
            (nameof(GeometryResolvesNegativeMonitorCoordinates), GeometryResolvesNegativeMonitorCoordinates),
            (nameof(UrlBuilderNormalizesHost), UrlBuilderNormalizesHost),
            (nameof(UrlBuilderPreservesQueryAndFragment), UrlBuilderPreservesQueryAndFragment),
            (nameof(UrlBuilderRejectsUnsupportedScheme), UrlBuilderRejectsUnsupportedScheme),
            (nameof(OptionsRequireConfigForRun), OptionsRequireConfigForRun),
            (nameof(OptionsParseStartMenuArguments), OptionsParseStartMenuArguments),
            (nameof(OptionsParseShowAndHideCommands), OptionsParseShowAndHideCommands),
            (nameof(OptionsRejectConflictingCommands), OptionsRejectConflictingCommands),
            (nameof(OptionsRejectStartupOptionsForCommands), OptionsRejectStartupOptionsForCommands),
            (nameof(CommandProtocolParsesOnlyVisibilityCommands), CommandProtocolParsesOnlyVisibilityCommands),
            (nameof(CommandProtocolSerializesVisibilityCommands), CommandProtocolSerializesVisibilityCommands),
            (nameof(VisibilityControllerCentralizesState), VisibilityControllerCentralizesState),
            (nameof(PumpPolicyHonorsHiddenBehavior), PumpPolicyHonorsHiddenBehavior),
            (nameof(AutoHideRulesRequireExternalForeground), AutoHideRulesRequireExternalForeground),
            (nameof(RuntimeLayoutResolvesBundledAndExternalRoots), RuntimeLayoutResolvesBundledAndExternalRoots),
            (nameof(RuntimeLayoutValidatesRequiredFiles), RuntimeLayoutValidatesRequiredFiles),
            (nameof(HelpTextUsesUltraLightStartMenu), HelpTextUsesUltraLightStartMenu),
            (nameof(TopmostPlacementFlagsShowOnlyWhenRequested), TopmostPlacementFlagsShowOnlyWhenRequested)
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

        if (Environment.GetEnvironmentVariable("ULTRALIGHTSTARTMENU_RUN_INTEGRATION") == "1")
        {
            try
            {
                RuntimeSmokeTest();
                Console.WriteLine("PASS RuntimeSmokeTest");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL RuntimeSmokeTest: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("SKIP RuntimeSmokeTest (set ULTRALIGHTSTARTMENU_RUN_INTEGRATION=1 to enable).");
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} unit tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void ConfigurationLoadsRequiredFields()
    {
        StartMenuConfiguration configuration = Load("""
            {"start-menu":{"x":50,"y":50,"pos-scaled":true,"size-scaled":false,"anchor":"center","width":400,"height":200,"monitor":1,"url":"example.test"}}
            """);
        StartMenuDefinition startMenu = configuration.StartMenu;
        AssertEqual(StartMenuAnchor.Center, startMenu.Anchor);
        AssertEqual("https://example.test/", startMenu.Url);
        AssertTrue(startMenu.PosScaled);
        AssertFalse(startMenu.SizeScaled);
        AssertTrue(startMenu.Transparent);
        AssertTrue(startMenu.AutoHide);
        AssertEqual(StartMenuHiddenBehavior.None, startMenu.HiddenBehavior);
    }

    private static void ConfigurationDefaultsOptionalFields()
    {
        StartMenuConfiguration configuration = Load("""
            {"start-menu":{"x":0,"y":0,"pos-scaled":false,"size-scaled":false,"anchor":"top-left","width":100,"height":100,"monitor":1,"url":"https://example.test","transparent":false,"auto-hide":false}}
            """);
        AssertFalse(configuration.StartMenu.Transparent);
        AssertFalse(configuration.StartMenu.AutoHide);
        AssertEqual(StartMenuHiddenBehavior.None, configuration.StartMenu.HiddenBehavior);
    }

    private static void ConfigurationParsesHiddenBehaviorModes()
    {
        AssertEqual(StartMenuHiddenBehavior.None, LoadWithHiddenBehavior("none").StartMenu.HiddenBehavior);
        AssertEqual(StartMenuHiddenBehavior.Pause, LoadWithHiddenBehavior("pause").StartMenu.HiddenBehavior);
        AssertEqual(StartMenuHiddenBehavior.Pause, LoadWithHiddenBehavior("SUSPEND").StartMenu.HiddenBehavior);
        AssertEqual(StartMenuHiddenBehavior.Unload, LoadWithHiddenBehavior("UnLoAd").StartMenu.HiddenBehavior);
    }

    private static void ConfigurationRejectsInvalidHiddenBehavior() =>
        AssertThrows<StartMenuConfigurationException>(() => LoadWithHiddenBehavior("freeze"));

    private static void ConfigurationSupportsDimensionAliases()
    {
        StartMenuConfiguration configuration = Load("""
            {"start-menu":{"x":50,"y":50,"pos-scaled":true,"size-scaled":false,"anchor":"center","w":400,"h":200,"monitor":1,"url":"example.test"}}
            """);
        AssertEqual(400d, configuration.StartMenu.Width);
        AssertEqual(200d, configuration.StartMenu.Height);
    }

    private static void ConfigurationRejectsLegacyWidgetArray() =>
        AssertThrows<StartMenuConfigurationException>(() => Load("{\"widgets\":[{\"url\":\"https://example.test\"}]}"));

    private static void ConfigurationRejectsUnknownFields() =>
        AssertThrows<StartMenuConfigurationException>(() => Load("""
            {"start-menu":{"x":0,"y":0,"pos-scaled":false,"size-scaled":false,"anchor":"top-left","width":100,"height":100,"monitor":1,"url":"https://example.test","auto_hide":false}}
            """));

    private static void ConfigurationRejectsMissingFields() =>
        AssertThrows<StartMenuConfigurationException>(() => Load("{\"start-menu\":{\"url\":\"https://example.test\"}}"));

    private static void ConfigurationRejectsInvalidAnchor() =>
        AssertThrows<StartMenuConfigurationException>(() => Load("""
            {"start-menu":{"x":0,"y":0,"pos-scaled":false,"size-scaled":false,"anchor":"diagonal","width":100,"height":100,"monitor":1,"url":"https://example.test"}}
            """));

    private static void ConfigurationRejectsInvalidGeometry() =>
        AssertThrows<StartMenuConfigurationException>(() => Load("""
            {"start-menu":{"x":101,"y":0,"pos-scaled":true,"size-scaled":false,"anchor":"center","width":400,"height":200,"monitor":1,"url":"https://example.test"}}
            """));

    private static void ConfigurationRejectsUnsupportedUrl() =>
        AssertThrows<StartMenuConfigurationException>(() => Load("""
            {"start-menu":{"x":0,"y":0,"pos-scaled":false,"size-scaled":false,"anchor":"center","width":10,"height":10,"monitor":1,"url":"javascript:alert(1)"}}
            """));

    private static void GeometryResolvesPixelTopLeft()
    {
        var startMenu = new StartMenuDefinition(20, 30, false, false, StartMenuAnchor.TopLeft, 400, 200, 1, "https://example.test/", true, true);
        AssertEqual(new Rectangle(120, 230, 400, 200), StartMenuGeometry.Resolve(startMenu, new Rectangle(100, 200, 1920, 1080)));
    }

    private static void GeometryScalesPositionWithoutScalingSize()
    {
        var startMenu = new StartMenuDefinition(50, 50, true, false, StartMenuAnchor.Center, 400, 200, 1, "https://example.test/", true, true);
        AssertEqual(new Rectangle(760, 440, 400, 200), StartMenuGeometry.Resolve(startMenu, new Rectangle(0, 0, 1920, 1080)));
    }

    private static void GeometryScalesSizeIndependently()
    {
        var startMenu = new StartMenuDefinition(0, 0, false, true, StartMenuAnchor.TopLeft, 25, 25, 1, "https://example.test/", true, true);
        AssertEqual(new Rectangle(0, 0, 270, 270), StartMenuGeometry.Resolve(startMenu, new Rectangle(0, 0, 1920, 1080)));
    }

    private static void GeometryResolvesNegativeMonitorCoordinates()
    {
        var startMenu = new StartMenuDefinition(10, 20, false, false, StartMenuAnchor.BottomRight, 100, 50, 2, "https://example.test/", true, true);
        AssertEqual(new Rectangle(-90, -30, 100, 50), StartMenuGeometry.Resolve(startMenu, new Rectangle(-1920, -1200, 1920, 1200)));
    }

    private static void UrlBuilderNormalizesHost() =>
        AssertEqual("https://example.test/", UrlBuilder.Normalize("example.test"));

    private static void UrlBuilderPreservesQueryAndFragment() =>
        AssertEqual("https://example.test/start-menu?theme=dark#scene", UrlBuilder.Normalize("https://example.test/start-menu?theme=dark#scene"));

    private static void UrlBuilderRejectsUnsupportedScheme() =>
        AssertThrows<OptionException>(() => UrlBuilder.Normalize("javascript:alert(1)"));

    private static void OptionsRequireConfigForRun() =>
        AssertThrows<OptionException>(() => HostOptions.Parse(Array.Empty<string>()));

    private static void OptionsParseStartMenuArguments()
    {
        HostOptions options = HostOptions.Parse(new[]
        {
            "--config", "start-menu.json",
            "--reconcile-ms=100",
            "--startup-timeout-ms", "5000",
            "--ultralight-root", @"..\third_party\ultralight",
            "--verbose"
        });
        AssertEqual(StartMenuCommand.Run, options.Command);
        AssertEqual(100, options.ReconcileIntervalMs);
        AssertEqual(TimeSpan.FromSeconds(5), options.StartupTimeout);
        AssertTrue(options.UltralightRoot!.EndsWith(Path.Combine("third_party", "ultralight"), StringComparison.OrdinalIgnoreCase));
        AssertTrue(options.Verbose);
        AssertTrue(options.ConfigPath.EndsWith("start-menu.json", StringComparison.OrdinalIgnoreCase));
    }

    private static void OptionsParseShowAndHideCommands()
    {
        AssertEqual(StartMenuCommand.Show, HostOptions.Parse(new[] { "--show" }).Command);
        AssertEqual(StartMenuCommand.Hide, HostOptions.Parse(new[] { "--hide" }).Command);
    }

    private static void OptionsRejectConflictingCommands() =>
        AssertThrows<OptionException>(() => HostOptions.Parse(new[] { "--show", "--hide" }));

    private static void OptionsRejectStartupOptionsForCommands() =>
        AssertThrows<OptionException>(() => HostOptions.Parse(new[] { "--show", "--config", "start-menu.json" }));

    private static void CommandProtocolParsesOnlyVisibilityCommands()
    {
        AssertTrue(StartMenuCommandProtocol.TryParse("show", out StartMenuCommand show));
        AssertEqual(StartMenuCommand.Show, show);
        AssertTrue(StartMenuCommandProtocol.TryParse(" HIDE ", out StartMenuCommand hide));
        AssertEqual(StartMenuCommand.Hide, hide);
        AssertFalse(StartMenuCommandProtocol.TryParse("run", out _));
    }

    private static void CommandProtocolSerializesVisibilityCommands()
    {
        AssertEqual("show", StartMenuCommandProtocol.Serialize(StartMenuCommand.Show));
        AssertEqual("hide", StartMenuCommandProtocol.Serialize(StartMenuCommand.Hide));
        AssertThrows<ArgumentOutOfRangeException>(() => StartMenuCommandProtocol.Serialize(StartMenuCommand.Run));
    }

    private static void VisibilityControllerCentralizesState()
    {
        var placement = new RecordingPlacement();
        var visibility = new StartMenuVisibilityController(placement);
        var window = new UltralightWindow(
            new StartMenuWindowSpec("start-menu", "https://example.test", new Rectangle(0, 0, 100, 100)),
            TimeSpan.FromSeconds(1));

        visibility.SetDesiredVisible(false);
        visibility.Attach(window);
        AssertFalse(visibility.DesiredVisible);
        AssertFalse(visibility.IsVisible);
        AssertEqual(1, placement.HideCount);

        visibility.SetDesiredVisible(true);
        AssertTrue(visibility.DesiredVisible);
        AssertTrue(visibility.IsVisible);
        AssertEqual(1, placement.ShowCount);

        visibility.Detach(window);
        AssertFalse(visibility.IsVisible);
    }

    private static void AutoHideRulesRequireExternalForeground()
    {
        AssertTrue(AutoHideRules.ShouldHide(true, true, false));
        AssertFalse(AutoHideRules.ShouldHide(false, true, false));
        AssertFalse(AutoHideRules.ShouldHide(true, false, false));
        AssertFalse(AutoHideRules.ShouldHide(true, true, true));
    }

    private static void PumpPolicyHonorsHiddenBehavior()
    {
        AssertTrue(StartMenuPumpPolicy.ShouldPump(StartMenuHiddenBehavior.None, false, false));
        AssertFalse(StartMenuPumpPolicy.ShouldPump(StartMenuHiddenBehavior.Pause, false, false));
        AssertFalse(StartMenuPumpPolicy.ShouldPump(StartMenuHiddenBehavior.Unload, false, false));
        AssertTrue(StartMenuPumpPolicy.ShouldPump(StartMenuHiddenBehavior.Pause, true, false));
        AssertTrue(StartMenuPumpPolicy.ShouldPump(StartMenuHiddenBehavior.Unload, false, true));
    }

    private static void RuntimeLayoutResolvesBundledAndExternalRoots()
    {
        RuntimeLayout bundled = RuntimeLayout.Resolve(null, @"C:\app");
        AssertEqual(@"C:\app", bundled.LibraryDirectory);
        AssertEqual(Path.Combine(@"C:\app", "assets"), bundled.FileSystemPath);

        RuntimeLayout external = RuntimeLayout.Resolve(@"C:\vendor\ultralight", @"C:\app");
        AssertEqual(Path.Combine(@"C:\vendor\ultralight", "bin"), external.LibraryDirectory);
        AssertEqual(@"C:\vendor\ultralight", external.FileSystemPath);
    }

    private static void RuntimeLayoutValidatesRequiredFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ultralight-start-menu-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            RuntimeLayout layout = RuntimeLayout.Resolve(root, @"C:\app");
            AssertThrows<FileNotFoundException>(() => layout.Validate());

            foreach (string library in new[] { "AppCore.dll", "Ultralight.dll", "UltralightCore.dll", "WebCore.dll" })
                File.WriteAllText(Path.Combine(root, "bin", library), "test");

            AssertThrows<DirectoryNotFoundException>(() => layout.Validate());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static void HelpTextUsesUltraLightStartMenu() =>
        AssertTrue(HostOptions.HelpText.Contains("UltraLightStartMenu", StringComparison.Ordinal));

    private static void TopmostPlacementFlagsShowOnlyWhenRequested()
    {
        uint hiddenFlags = StartMenuWindowController.GetPlacementFlags(false);
        uint shownFlags = StartMenuWindowController.GetPlacementFlags(true);
        AssertFalse((hiddenFlags & NativeWindowApi.SWP_SHOWWINDOW) != 0);
        AssertTrue((shownFlags & NativeWindowApi.SWP_SHOWWINDOW) != 0);
        AssertEqual(new IntPtr(-1), NativeWindowApi.HWND_TOPMOST);
    }

    private static void RuntimeSmokeTest()
    {
        string stage = "runtime creation";
        try
        {
            using var runtime = new UltralightRuntime(Environment.GetEnvironmentVariable("ULTRALIGHT_TEST_ROOT"));
            stage = "window creation";
            var spec = new StartMenuWindowSpec("start-menu", "file:///probe.html", new Rectangle(0, 0, 640, 480), true, true, 1);
            UltralightWindow window = runtime.CreateWindow(spec, TimeSpan.FromSeconds(5));
            stage = "native handle validation";
            AssertTrue(window.Hwnd != IntPtr.Zero);
            stage = "topmost placement";
            var controller = new StartMenuWindowController();
            controller.Reconcile(window);
            AssertTrue(window.TopmostPlacementApplied);
            controller.Show(window);
            AssertTrue(window.IsVisible);
            controller.Hide(window);
            AssertFalse(window.IsVisible);
            stage = "runtime pump";
            runtime.Pump();
            stage = "window destruction";
            runtime.DestroyWindow(window);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{stage}: {ex.Message}", ex);
        }
    }

    private static StartMenuConfiguration Load(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ultralight-start-menu-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            return StartMenuConfiguration.Load(path);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static StartMenuConfiguration LoadWithHiddenBehavior(string behavior) => Load(
        "{\"start-menu\":{\"x\":0,\"y\":0,\"pos-scaled\":false,\"size-scaled\":false,\"anchor\":\"top-left\",\"width\":100,\"height\":100,\"monitor\":1,\"url\":\"https://example.test\",\"hidden-behavior\":\"" +
        behavior + "\"}}");

    private sealed class RecordingPlacement : IStartMenuWindowPlacement
    {
        public int ShowCount { get; private set; }
        public int HideCount { get; private set; }

        public void Reconcile(UltralightWindow window) => window.TopmostPlacementApplied = true;

        public void Show(UltralightWindow window)
        {
            ShowCount++;
            window.IsVisible = true;
        }

        public void Hide(UltralightWindow window)
        {
            HideCount++;
            window.IsVisible = false;
        }
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
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

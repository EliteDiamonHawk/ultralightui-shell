using System.Drawing;
using UltralightWall;

namespace UltralightWall.Tests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var tests = new (string Name, Action Test)[]
        {
            (nameof(UrlBuilderNormalizesHost), UrlBuilderNormalizesHost),
            (nameof(UrlBuilderPreservesQueryAndFragment), UrlBuilderPreservesQueryAndFragment),
            (nameof(UrlBuilderRejectsUnsupportedScheme), UrlBuilderRejectsUnsupportedScheme),
            (nameof(RegistryTracksStatesAndHwndOwnership), RegistryTracksStatesAndHwndOwnership),
            (nameof(RegistryTracksCloseIntent), RegistryTracksCloseIntent),
            (nameof(CloseCheckRequiresAllWindows), CloseCheckRequiresAllWindows),
            (nameof(OptionsParseMonitorUrlOverrides), OptionsParseMonitorUrlOverrides),
            (nameof(OptionsParseUltralightArguments), OptionsParseUltralightArguments),
            (nameof(RuntimeLayoutResolvesBundledAndExternalRoots), RuntimeLayoutResolvesBundledAndExternalRoots),
            (nameof(RuntimeLayoutValidatesRequiredFiles), RuntimeLayoutValidatesRequiredFiles),
            (nameof(HelpTextUsesUltralightWall), HelpTextUsesUltralightWall),
            (nameof(GeometryPreservesNegativeCoordinates), GeometryPreservesNegativeCoordinates),
            (nameof(NonClientInsetsAreDerived), NonClientInsetsAreDerived),
            (nameof(ClientBoundsExpandByMeasuredInsets), ClientBoundsExpandByMeasuredInsets),
            (nameof(BorderlessStyleTransformationIsIdempotent), BorderlessStyleTransformationIsIdempotent),
            (nameof(MonitorHostStyleClipsChildrenAndSiblings), MonitorHostStyleClipsChildrenAndSiblings),
            (nameof(SuspensionPolicyCombinesManualAndFullscreenState), SuspensionPolicyCombinesManualAndFullscreenState),
            (nameof(SuspensionPolicyDisablingAutomaticPauseRestoresVisibleWallpaper), SuspensionPolicyDisablingAutomaticPauseRestoresVisibleWallpaper),
            (nameof(KeyboardOwnershipRequiresValidatedFocus), KeyboardOwnershipRequiresValidatedFocus),
            (nameof(KeyboardOwnershipEndsOnForegroundAndSystemKeys), KeyboardOwnershipEndsOnForegroundAndSystemKeys),
            (nameof(KeyboardOwnershipStartsSafeAndCanBeReused), KeyboardOwnershipStartsSafeAndCanBeReused),
            (nameof(KeyboardPolicyUsesStrictSystemPassThrough), KeyboardPolicyUsesStrictSystemPassThrough)
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

        if (Environment.GetEnvironmentVariable("ULTRALIGHTWALL_RUN_INTEGRATION") == "1")
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
            Console.WriteLine("SKIP RuntimeSmokeTest (set ULTRALIGHTWALL_RUN_INTEGRATION=1 to enable).");
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} unit tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void UrlBuilderNormalizesHost() =>
        AssertEqual("https://example.test/", UrlBuilder.Normalize("example.test"));

    private static void UrlBuilderPreservesQueryAndFragment() =>
        AssertEqual(
            "https://example.test/wallpaper?theme=dark#scene",
            UrlBuilder.Normalize("https://example.test/wallpaper?theme=dark#scene"));

    private static void UrlBuilderRejectsUnsupportedScheme() =>
        AssertThrows<OptionException>(() => UrlBuilder.Normalize("javascript:alert(1)"));

    private static void RegistryTracksStatesAndHwndOwnership()
    {
        var registry = new WindowRegistry();
        AppWindow appWindow = NewWindow("A");
        registry.Add(appWindow);
        appWindow.Hwnd = new IntPtr(0x1234);
        registry.MarkEmbedded(appWindow);
        AssertEqual(AppWindowState.Embedded, appWindow.State);
        AssertTrue(registry.IsHwndAssignedToAnother(appWindow.Hwnd, NewWindow("other")));
        registry.MarkClosed(appWindow);
        AssertEqual(AppWindowState.Closed, appWindow.State);
        AssertEqual(WindowCloseIntent.Unexpected, appWindow.CloseIntent);
    }

    private static void RegistryTracksCloseIntent()
    {
        var registry = new WindowRegistry();
        AppWindow intentional = NewWindow("intentional");
        AppWindow unexpected = NewWindow("unexpected");
        registry.Add(intentional);
        registry.Add(unexpected);
        intentional.Hwnd = new IntPtr(1);
        unexpected.Hwnd = new IntPtr(2);
        registry.MarkEmbedded(intentional);
        registry.MarkEmbedded(unexpected);
        registry.MarkIntentionalClose(intentional);
        registry.MarkClosed(intentional);
        registry.MarkClosed(unexpected);
        AssertEqual(WindowCloseIntent.Intentional, intentional.CloseIntent);
        AssertEqual(WindowCloseIntent.Unexpected, unexpected.CloseIntent);
    }

    private static void CloseCheckRequiresAllWindows()
    {
        var registry = new WindowRegistry();
        AppWindow closed = NewWindow("closed");
        AppWindow open = NewWindow("open");
        registry.Add(closed);
        registry.Add(open);
        closed.Hwnd = new IntPtr(1);
        open.Hwnd = new IntPtr(2);
        registry.MarkEmbedded(closed);
        registry.MarkEmbedded(open);
        registry.MarkClosed(closed);
        AssertFalse(CloseCheck.AllClosed(new[] { closed, open }));
        registry.MarkFailed(open, "test failure");
        AssertTrue(CloseCheck.AllClosed(new[] { closed, open }));
    }

    private static void OptionsParseMonitorUrlOverrides()
    {
        HostOptions options = HostOptions.Parse(new[]
        {
            "--url", "https://base.example",
            "--monitor-url", "2=https://monitor.example",
            "--mode", "all"
        });
        AssertEqual("https://monitor.example/", options.MonitorUrls[1]);
        AssertEqual(WallpaperMode.All, options.Mode);
    }

    private static void OptionsParseUltralightArguments()
    {
        HostOptions options = HostOptions.Parse(new[]
        {
            "--reconcile-ms=100",
            "--startup-timeout-ms", "5000",
            "--ultralight-root=..\\third_party\\ultralight",
            "--verbose",
            "--transparent"
        });
        AssertEqual(100, options.ReconcileIntervalMs);
        AssertEqual(TimeSpan.FromSeconds(5), options.StartupTimeout);
        AssertTrue(options.UltralightRoot!.EndsWith(Path.Combine("third_party", "ultralight"), StringComparison.OrdinalIgnoreCase));
        AssertTrue(options.Verbose);
        AssertTrue(options.Transparent);
        AssertThrows<OptionException>(() => HostOptions.Parse(new[] { "--ultralight-root" }));
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
        string root = Path.Combine(Path.GetTempPath(), $"ultralight-wall-test-{Guid.NewGuid():N}");
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

    private static void HelpTextUsesUltralightWall()
    {
        AssertTrue(HostOptions.HelpText.Contains("UltralightWall", StringComparison.Ordinal));
    }

    private static void GeometryPreservesNegativeCoordinates()
    {
        Rectangle monitor = new(-1920, -1200, 1920, 1200);
        Rectangle mapped = DesktopEmbedder.ToParentClient(monitor, new Point(-2000, -1300));
        AssertEqual(new Rectangle(80, 100, 1920, 1200), mapped);
    }

    private static void NonClientInsetsAreDerived()
    {
        AssertTrue(DesktopEmbedder.TryCalculateNonClientInsets(
            new Rectangle(-8, -31, 1936, 1119),
            new Rectangle(0, 0, 1920, 1080),
            out NonClientInsets insets));
        AssertEqual(new NonClientInsets(8, 31, 8, 8), insets);
    }

    private static void ClientBoundsExpandByMeasuredInsets()
    {
        Rectangle outer = DesktopEmbedder.CalculateOuterBounds(
            new Rectangle(-1920, -1200, 1920, 1200), new NonClientInsets(8, 31, 8, 8));
        AssertEqual(new Rectangle(-1928, -1231, 1936, 1239), outer);
    }

    private static void BorderlessStyleTransformationIsIdempotent()
    {
        const long frame = 0x00C00000L | 0x00040000L | 0x00020000L | 0x00010000L | 0x00080000L;
        const long popup = unchecked((long)0x80000000);
        long transformed = DesktopEmbedder.GetBorderlessChildStyle(frame | popup | 0x8);
        AssertFalse(DesktopEmbedder.HasNativeFrame(transformed));
        AssertEqual(transformed, DesktopEmbedder.GetBorderlessChildStyle(transformed));
    }

    private static void MonitorHostStyleClipsChildrenAndSiblings()
    {
        long style = DesktopEmbedder.GetMonitorHostStyle();
        AssertTrue((style & 0x40000000L) != 0);
        AssertTrue((style & 0x02000000L) != 0);
        AssertTrue((style & 0x04000000L) != 0);
    }

    private static void SuspensionPolicyCombinesManualAndFullscreenState()
    {
        AssertFalse(WallpaperSuspensionPolicy.ShouldSuspend(false, true, false));
        AssertTrue(WallpaperSuspensionPolicy.ShouldSuspend(false, true, true));
        AssertFalse(WallpaperSuspensionPolicy.ShouldSuspend(false, false, true));
        AssertTrue(WallpaperSuspensionPolicy.ShouldSuspend(true, false, false));
    }

    private static void SuspensionPolicyDisablingAutomaticPauseRestoresVisibleWallpaper()
    {
        AssertTrue(WallpaperSuspensionPolicy.ShouldSuspend(true, false, false));
        AssertFalse(WallpaperSuspensionPolicy.ShouldSuspend(false, false, true));
        AssertTrue(WallpaperSuspensionPolicy.ShouldSuspend(false, true, true));
    }

    private static void KeyboardOwnershipRequiresValidatedFocus()
    {
        var policy = new KeyboardOwnershipPolicy();
        AssertEqual(KeyboardOwnershipState.NoBackgroundKeyboardInput, policy.State);
        policy.Arm();
        AssertFalse(policy.TryBegin(false, true, KeyboardFocusKind.RendererFallback));
        AssertFalse(policy.ShouldForward);
        AssertFalse(policy.TryBegin(true, false, KeyboardFocusKind.RendererFallback));
        AssertFalse(policy.ShouldForward);
        AssertTrue(policy.TryBegin(true, true, KeyboardFocusKind.RendererFallback));
        AssertEqual(KeyboardOwnershipState.BackgroundKeyboardInteraction, policy.State);
        AssertTrue(policy.ShouldForward);
    }

    private static void KeyboardOwnershipEndsOnForegroundAndSystemKeys()
    {
        var policy = new KeyboardOwnershipPolicy();
        policy.Arm();
        AssertTrue(policy.TryBegin(true, true, KeyboardFocusKind.RendererFallback));
        AssertTrue(policy.ShouldEndForForegroundChange(false));
        AssertFalse(policy.ShouldEndForForegroundChange(true));
        policy.End();
        AssertFalse(policy.ShouldForward);
    }

    private static void KeyboardPolicyUsesStrictSystemPassThrough()
    {
        AssertTrue(KeyboardInputPolicy.IsSystemOwnedKey(NativeMethods.WM_KEYDOWN, NativeMethods.VK_LWIN, false, false, false));
        AssertTrue(KeyboardInputPolicy.IsSystemOwnedKey(NativeMethods.WM_SYSKEYDOWN, 0x46, true, false, false));
        AssertTrue(KeyboardInputPolicy.IsSystemOwnedKey(NativeMethods.WM_KEYDOWN, 0x87, false, false, false));
        AssertTrue(KeyboardInputPolicy.IsSystemOwnedKey(NativeMethods.WM_KEYDOWN, NativeMethods.VK_CAPITAL, false, false, false));
        AssertFalse(KeyboardInputPolicy.IsSystemOwnedKey(NativeMethods.WM_KEYDOWN, 0x41, false, false, false));
        AssertFalse(KeyboardInputPolicy.EndsOwnership(NativeMethods.WM_KEYDOWN, NativeMethods.VK_CAPITAL, false, false, false));
        AssertTrue(KeyboardInputPolicy.EndsOwnership(NativeMethods.WM_KEYDOWN, NativeMethods.VK_ESCAPE, false, false, false));
        AssertTrue(KeyboardInputPolicy.EndsOwnership(NativeMethods.WM_KEYDOWN, NativeMethods.VK_ESCAPE, false, false, true));
    }

    private static void KeyboardOwnershipStartsSafeAndCanBeReused()
    {
        var policy = new KeyboardOwnershipPolicy();
        AssertFalse(policy.ShouldForward);
        policy.Arm();
        AssertTrue(policy.TryBegin(true, true, KeyboardFocusKind.RendererFallback));
        policy.End();
        AssertEqual(KeyboardOwnershipState.NoBackgroundKeyboardInput, policy.State);
        policy.Arm();
        AssertTrue(policy.TryBegin(true, true, KeyboardFocusKind.RendererFallback));
        AssertTrue(policy.ShouldForward);
    }

    private static void RuntimeSmokeTest()
    {
        string stage = "runtime creation";
        try
        {
            using var runtime = new UltralightRuntime(Environment.GetEnvironmentVariable("ULTRALIGHT_TEST_ROOT"));
            stage = "window creation";
            var spec = new WallpaperWindowSpec("virtual-screen", "file:///probe.html", new Rectangle(0, 0, 640, 480), new Rectangle(0, 0, 640, 480));
            AppWindow window = runtime.CreateWindow(spec, TimeSpan.FromSeconds(5));
            stage = "native handle validation";
            AssertTrue(window.Hwnd != IntPtr.Zero);
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

    private static AppWindow NewWindow(string id) =>
        new(new WallpaperWindowSpec(id, "https://example.test", Rectangle.Empty, Rectangle.Empty), TimeSpan.FromSeconds(30));

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

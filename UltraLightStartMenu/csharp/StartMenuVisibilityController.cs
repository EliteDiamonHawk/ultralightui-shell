namespace UltraLightStartMenu;

public sealed class StartMenuVisibilityController
{
    private readonly IStartMenuWindowPlacement _windowController;

    public StartMenuVisibilityController(IStartMenuWindowPlacement windowController)
    {
        _windowController = windowController;
    }

    public UltralightWindow? Window { get; private set; }
    public bool DesiredVisible { get; private set; } = true;
    public bool IsVisible => Window?.IsVisible ?? DesiredVisible;
    public bool TopmostPlacementApplied => Window?.TopmostPlacementApplied ?? false;

    public void SetDesiredVisible(bool visible)
    {
        DesiredVisible = visible;
        if (Window != null)
            Apply(Window);
    }

    public void Prepare(UltralightWindow window) => Apply(window);

    public void Attach(UltralightWindow window)
    {
        Window = window;
        Apply(window);
    }

    public void Detach(UltralightWindow window)
    {
        if (ReferenceEquals(Window, window))
            Window = null;
    }

    public void Reconcile()
    {
        if (Window != null)
            _windowController.Reconcile(Window);
    }

    private void Apply(UltralightWindow window)
    {
        _windowController.Reconcile(window);
        if (DesiredVisible)
            _windowController.Show(window);
        else
            _windowController.Hide(window);
    }
}

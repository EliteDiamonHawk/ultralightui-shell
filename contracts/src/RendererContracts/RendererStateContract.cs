namespace KhaosShell.Contracts;

public sealed record RendererBoundsContract(int X, int Y, int Width, int Height);

public sealed record RendererWindowContract(
    string Id,
    string Url,
    string RuntimeGroup,
    string Kind,
    RendererBoundsContract ConfiguredBounds,
    RendererBoundsContract CurrentBounds,
    int Monitor,
    bool Visible,
    bool Focused,
    bool Suspended,
    string State,
    string SurfaceKind,
    string ZOrder);

public sealed record RendererWindowListContract
{
    public IReadOnlyList<RendererWindowContract> Windows { get; init; } = Array.Empty<RendererWindowContract>();
}

public sealed record RendererConfigContract
{
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Cues { get; init; } = Array.Empty<string>();
    public IReadOnlyList<RendererWindowContract> Windows { get; init; } = Array.Empty<RendererWindowContract>();
    public Guid? ThemeId { get; init; }
    public Guid? ThemeActivationId { get; init; }
    public long? ThemeGeneration { get; init; }
    public string? RendererSessionId { get; init; }
}

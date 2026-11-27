namespace Aether;

/// <summary>
/// Owns a set of native/COM resources until construction succeeds.
/// Resources are released in reverse acquisition order when construction fails.
/// </summary>
internal sealed class NativeResourceScope : IDisposable
{
    private readonly List<IDisposable> _resources = new();
    private bool _committed;

    public T Own<T>(T resource) where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(resource);
        _resources.Add(resource);
        return resource;
    }

    public void Commit()
    {
        _committed = true;
        _resources.Clear();
    }

    public void Dispose()
    {
        if (_committed)
            return;

        for (int index = _resources.Count - 1; index >= 0; index--)
        {
            try
            {
                _resources[index].Dispose();
            }
            catch
            {
                // Continue releasing the remaining resources. The original
                // construction failure must remain the reported exception.
            }
        }

        _resources.Clear();
    }
}

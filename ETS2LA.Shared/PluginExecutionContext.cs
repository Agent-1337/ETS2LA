namespace ETS2LA.Shared;

/// <summary>Identifies the synchronous consumer of shared map interpolation code.</summary>
public static class PluginExecutionContext
{
    [ThreadStatic] private static string? currentId;
    public static string? CurrentId => currentId;
    public static Scope Enter(string id) => new(id);

    public readonly struct Scope : IDisposable
    {
        private readonly string? previous;
        internal Scope(string id) { previous = currentId; currentId = id; }
        public void Dispose() => currentId = previous;
    }
}

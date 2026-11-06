using System.Diagnostics.CodeAnalysis;

namespace DanceCommander.Core.Fs;

public interface IProviderRegistry
{
    IFileSystemProvider Resolve(FsUri uri);

    bool TryResolve(FsUri uri, [NotNullWhen(true)] out IFileSystemProvider? provider);

    IReadOnlyCollection<string> Schemes { get; }
}

public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly Dictionary<string, IFileSystemProvider> _providers;

    public ProviderRegistry(IEnumerable<IFileSystemProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.Scheme, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> Schemes => _providers.Keys;

    public IFileSystemProvider Resolve(FsUri uri) =>
        TryResolve(uri, out var provider)
            ? provider
            : throw new NotSupportedException($"No provider is registered for '{uri.Scheme}:'.");

    public bool TryResolve(FsUri uri, [NotNullWhen(true)] out IFileSystemProvider? provider) =>
        _providers.TryGetValue(uri.Scheme, out provider);
}

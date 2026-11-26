using System.Globalization;
using SharpFAI.ModApi;

namespace SharpFAI.Player.ModSupport;

internal interface IModRuntime : IAsyncDisposable
{
    string Root { get; }
    string SearchDirectory { get; }
    UiHost Target { get; }
    bool SafeMode { get; }
    int Generation { get; }
    int LoadGeneration { get; }
    IReadOnlyList<ModCatalogEntry> Catalog { get; }
    IReadOnlyList<ModNotice> Notices { get; }
    Func<CultureInfo> Culture { get; set; }
    Func<Type, object?>? ExternalService { get; set; }
    IModWindowOperations? WindowOperations { get; set; }
    Action<ModNotice>? NoticeWritten { get; set; }
    IReadOnlyList<ModRegistration<T>> Entries<T>(UiHost target);
    bool HasLoadHooks(UiHost target);
    bool IsActive(string id, string range, UiHost target);
    void Report(string id, string message);
    Task InitializeAsync();
    Task RefreshAsync();
    Task InstallAsync(string archive);
    Task AddDevelopmentAsync(string manifest);
    Task EnableAsync(string id);
    Task DisableAsync(string id);
    Task ReloadAsync(string id);
    Task RemoveAsync(string id);
}

internal interface IModRegistrationOwner
{
    string Id { get; }
    bool Stopping { get; }
    CancellationToken Lifetime { get; }
    IDisposable? Acquire();
    string Text(string key, string? fallback);
    void Log(string message);
}

internal sealed record ModDependency(string Id, string Version);
internal sealed record ModCatalogEntry(ModManifest Manifest, bool Development, bool Enabled, bool Requested, string? Unavailable,
    bool CanRemove);
internal sealed record ModNotice(string ModId, string Message);
internal sealed record ModManifest(int ManifestVersion, string Id, string Name, string Version, string Api,
    string EntryAssembly, string? EntryType, ModDependency[]? Dependencies = null,
    string[]? Platforms = null,
    Dictionary<string, Dictionary<string, string>>? Translations = null);

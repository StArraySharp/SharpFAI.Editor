using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using SharpFAI.ModApi;

using SharpFAI.Player.ModSupport;

namespace SharpFAI.Editor.Mods;

internal sealed class ModHost : IModRuntime
{
    private sealed record Preferences(HashSet<string> Roots, Dictionary<string, string> Trust, HashSet<string> Development)
    {
        public Preferences() : this([], [], []) { }
        public HashSet<string> Disabled { get; init; } = [];
        public Preferences Copy() => new([.. Roots], new(Trust), [.. Development]) { Disabled = [.. Disabled] };
    }

    private sealed class ActiveSet(ModActivation[] mods)
    {
        public ModActivation[] Mods { get; } = mods;
        public ConcurrentDictionary<(Type, UiHost), object> Projections { get; } = new();
    }

    private readonly SemaphoreSlim _operations = new(1);
    private readonly ConcurrentQueue<ModNotice> _notices = new();
    private readonly string _settingsFile, _packages, _legacyPackages, _shadow;
    private readonly bool _safeMode;
    private Preferences _preferences = new();
    private ActiveSet _active = new([]);
    private ModCatalogEntry[] _catalog = [];
    private int _generation;
    private int _loadGeneration;
    private bool _disposed;
    private FileSystemWatcher? _watcher;
    private Timer? _refreshTimer;

    public ModHost(UiHost target, string? root = null, bool safeMode = false)
    {
        if (target is not (UiHost.Editor or UiHost.Player))
            throw new ArgumentOutOfRangeException(nameof(target));
        Target = target;
        root ??= Environment.GetEnvironmentVariable("SHARPFAI_MODS_DIR");
        SearchDirectory = Path.GetFullPath(root ?? Path.Combine(AppContext.BaseDirectory, "Mods"));
        Root = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SharpFAI", "Mods"));
        _safeMode = safeMode;
        _packages = Path.Combine(SearchDirectory, "packages");
        _legacyPackages = Path.Combine(Root, "packages");
        _shadow = Path.Combine(Root, "shadow");
        _settingsFile = Path.Combine(Root, $"enabled-{target.ToString().ToLowerInvariant()}.json");
    }

    public string Root { get; }
    public string SearchDirectory { get; }
    public UiHost Target { get; }
    public bool SafeMode => _safeMode;
    public int Generation => Volatile.Read(ref _generation);
    public int LoadGeneration => Volatile.Read(ref _loadGeneration);
    public bool HasLoadHooks(UiHost target) => Entries<ChartProcessor>(target).Count != 0 ||
        Entries<AssetProcessor>(target).Count != 0 || Entries<AudioHook>(target).Count != 0 ||
        Entries<CustomEventCompiler>(target).Count != 0;
    public bool IsActive(string id, string range, UiHost target) => target is UiHost.Editor or UiHost.Export &&
        Volatile.Read(ref _active).Mods.Any(mod =>
            mod.Package.Manifest.Id == id && ModVersion.Supports(range, mod.Package.Manifest.Version));
    public IReadOnlyList<ModCatalogEntry> Catalog => Volatile.Read(ref _catalog);
    public IReadOnlyList<ModNotice> Notices => _notices.ToArray();
    public Func<CultureInfo> Culture { get; set; } = () => CultureInfo.CurrentUICulture;
    public Func<Type, object?>? ExternalService { get; set; }
    public IModWindowOperations? WindowOperations { get; set; }
    public Action<ModNotice>? NoticeWritten { get; set; }

    public IReadOnlyList<ModRegistration<T>> Entries<T>(UiHost target)
    {
        var active = Volatile.Read(ref _active);
        if (active.Mods.Length == 0) return [];
        return (ModRegistration<T>[])active.Projections.GetOrAdd((typeof(T), target), _ =>
            Order(active.Mods.SelectMany(item => item.Scope.Registrations.OfType<ModRegistration<T>>())
                .Where(entry => (entry.Descriptor.Hosts & target & ~UiHost.Player) != 0).ToArray()));
    }

    internal void Log(string id, string message)
    {
        var notice = new ModNotice(id, message);
        _notices.Enqueue(notice);
        while (_notices.Count > 80) _notices.TryDequeue(out _);
        NoticeWritten?.Invoke(notice);
    }

    public void Report(string id, string message) => Log(id, message);

    private string? Unavailable(ModPackage package)
    {
        if (Target != UiHost.Editor) return "Mods are only available in the editor.";
        if (package.Manifest.Platforms is { Length: > 0 } platforms && !platforms.Contains(RuntimeInformation.RuntimeIdentifier))
            return $"Unavailable on {RuntimeInformation.RuntimeIdentifier}.";
        return null;
    }

    public Task InitializeAsync() => RunAsync(async () =>
    {
        try { Directory.CreateDirectory(SearchDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log("SharpFAI", $"Could not create the Mod search directory '{SearchDirectory}': {error.Message}");
        }
        try
        {
            if (File.Exists(_settingsFile))
                _preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_settingsFile)) ?? new();
        }
        catch (Exception error) { Log("SharpFAI", error.GetBaseException().Message); }
        StartWatching();
        var available = Discover(_preferences);
        if (!_safeMode && Target == UiHost.Editor)
        {
            var trusted = available.Where(package => _preferences.Trust.ContainsKey(TrustKey(package))).ToArray();
            HashSet<string> accepted = [];
            foreach (string id in _preferences.Roots.Order(StringComparer.Ordinal))
            {
                try
                {
                    await ApplyAsync(trusted, [.. accepted, id], _preferences, [], [], save: false).ConfigureAwait(false);
                    accepted.Add(id);
                }
                catch (Exception error) { Log(id, error.GetBaseException().Message); }
            }
        }
        await RefreshDropInsAsync(available).ConfigureAwait(false);
    });

    public Task RefreshAsync() => RunAsync(() => RefreshDropInsAsync(Discover(_preferences)));

    private async Task RefreshDropInsAsync(IReadOnlyList<ModPackage> available)
    {
        if (!_safeMode && Target == UiHost.Editor)
        {
            // Placing a folder in the search directory authorizes its first activation. Once known,
            // the existing trust and disable records govern subsequent loads and file changes.
            var seen = _preferences.Trust.Keys.Select(key => key.Split('|', 2)[0]).ToHashSet(StringComparer.Ordinal);
            seen.UnionWith(_preferences.Disabled);
            seen.UnionWith(_preferences.Roots);
            seen.UnionWith(Volatile.Read(ref _active).Mods.Select(mod => mod.Package.Manifest.Id));
            foreach (string id in available.Where(package => package.IsDropIn && Unavailable(package) is null)
                         .Select(package => package.Manifest.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (seen.Contains(id)) continue;
                var next = _preferences.Copy();
                var roots = ActiveRoots(next).Append(id).ToArray();
                try
                {
                    var plan = Resolve(available, roots);
                    var approved = ModDependencyGraph.Dependencies(id, plan);
                    if (approved.Overlaps(next.Disabled))
                        throw new InvalidOperationException("A required Mod was disabled.");
                    approved.IntersectWith(plan.Where(package => package.IsDropIn && !seen.Contains(package.Manifest.Id))
                        .Select(package => package.Manifest.Id));
                    next.Roots.Add(id);
                    await ApplyAsync(available, roots, next, approved, [], save: true).ConfigureAwait(false);
                    seen.UnionWith(approved);
                    seen.Add(id);
                }
                catch (Exception error) { Log(id, error.GetBaseException().Message); }
            }
        }
        PublishCatalog(available);
    }

    private void StartWatching()
    {
        if (_watcher is not null || Target != UiHost.Editor || _safeMode || !Directory.Exists(SearchDirectory)) return;
        try
        {
            _refreshTimer = new Timer(state => _ = RefreshFromWatcherAsync());
            _watcher = new FileSystemWatcher(SearchDirectory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Created += Changed;
            _watcher.Changed += Changed;
            _watcher.Deleted += Changed;
            _watcher.Renamed += (_, change) => { Changed(null, change); Schedule(change.OldFullPath); };
            _watcher.Error += (_, _) => QueueRefresh();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StopWatching();
            Log("SharpFAI", error.Message);
        }

        void Changed(object? sender, FileSystemEventArgs change) => Schedule(change.FullPath);

        void Schedule(string path)
        {
            string relative = Path.GetRelativePath(SearchDirectory, path);
            string first = relative.Split(Path.DirectorySeparatorChar, 2)[0];
            if (first.StartsWith('.') || first.Equals("packages", StringComparison.OrdinalIgnoreCase) ||
                IsDataDirectory(Path.Combine(SearchDirectory, first)) ||
                relative == first && (File.Exists(path) || first.EndsWith(".json", StringComparison.OrdinalIgnoreCase))) return;
            QueueRefresh();
        }
    }

    private void QueueRefresh()
    {
        try { _refreshTimer?.Change(400, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private async Task RefreshFromWatcherAsync()
    {
        try { await RefreshAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (Exception error) { Log("SharpFAI", error.GetBaseException().Message); }
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        Interlocked.Exchange(ref _refreshTimer, null)?.Dispose();
    }

    public Task InstallAsync(string archive) => RunAsync(() =>
    {
        if (!archive.EndsWith(".sharpfaimod", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select a .sharpfaimod archive.");
        Directory.CreateDirectory(_packages);
        if (!IsRegularPath(_packages)) throw new IOException("The Mod installation directory must be a regular directory.");
        string stage = Path.Combine(_packages, "." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            ModPackage.Extract(archive, stage);
            var package = ModPackage.Read(stage, development: false);
            string destination = Path.Combine(_packages, package.Manifest.Id, package.Manifest.Version);
            string parent = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(parent);
            if (!IsRegularPath(parent)) throw new IOException("The Mod package directory must be a regular directory.");
            Directory.Move(stage, destination);
        }
        finally { DeleteUnder(stage, _packages); }
        PublishCatalog(Discover(_preferences));
        return Task.CompletedTask;
    });

    public Task AddDevelopmentAsync(string manifest) => RunAsync(() =>
    {
        string path = Path.GetFullPath(manifest);
        var package = ModPackage.Read(Path.GetDirectoryName(path)!, development: true, manifestPath: path);
        package.HashAndCopy();
        var next = _preferences.Copy();
        next.Development.Add(package.Root);
        Save(next);
        PublishCatalog(Discover(_preferences));
        return Task.CompletedTask;
    });

    public Task EnableAsync(string id) => RunAsync(async () =>
    {
        EnsureEnabled();
        var next = _preferences.Copy();
        next.Roots.Add(id);
        var packages = Discover(next);
        var roots = ActiveRoots(next).Append(id).Distinct(StringComparer.Ordinal).ToArray();
        var plan = Resolve(packages, roots);
        var required = ModDependencyGraph.Dependencies(id, plan);
        next.Disabled.ExceptWith(required);
        await ApplyAsync(packages, roots, next, required, [], save: true).ConfigureAwait(false);
    });

    public Task DisableAsync(string id) => RunAsync(() => DisableCoreAsync(id));

    public Task ReloadAsync(string id) => RunAsync(async () =>
    {
        EnsureEnabled();
        var active = Volatile.Read(ref _active).Mods;
        var affected = ModDependencyGraph.Dependents(id, active.Select(mod => mod.Package));
        var next = _preferences.Copy();
        var packages = Discover(next);
        var roots = ActiveRoots(next);
        var plan = Resolve(packages, roots);
        var trusted = new HashSet<string>(affected, StringComparer.Ordinal);
        foreach (string affectedId in affected)
            trusted.UnionWith(ModDependencyGraph.Dependencies(affectedId, plan));
        await ApplyAsync(packages, roots, next, trusted, affected, save: true).ConfigureAwait(false);
    });

    public Task RemoveAsync(string id) => RunAsync(async () =>
    {
        var packages = Discover(_preferences).Where(package => package.Manifest.Id == id && !package.IsDropIn).ToArray();
        if (packages.Length == 0) return;
        await DisableCoreAsync(id).ConfigureAwait(false);
        var next = _preferences.Copy();
        foreach (var package in packages)
        {
            next.Trust.Remove(TrustKey(package));
            if (package.Development) next.Development.Remove(package.Root);
            else DeleteUnder(package.Root, IsUnder(package.Root, _packages) ? _packages : _legacyPackages);
        }
        Save(next);
        PublishCatalog(Discover(next));
    });

    private async Task DisableCoreAsync(string id)
    {
        var active = Volatile.Read(ref _active).Mods;
        var next = _preferences.Copy();
        var available = Discover(next);
        var activePackages = active.Select(mod => mod.Package).ToArray();
        var disabled = ModDependencyGraph.Dependents(id, activePackages);
        next.Disabled.UnionWith(disabled);
        next.Disabled.Add(id);
        next.Roots.ExceptWith(disabled);
        await ApplyAsync(available, ActiveRoots(next), next, [], [], save: true, candidates: activePackages).ConfigureAwait(false);
    }

    private string[] ActiveRoots(Preferences preferences) => Volatile.Read(ref _active).Mods.Select(item => item.Package.Manifest.Id)
        .Where(preferences.Roots.Contains).ToArray();

    private IReadOnlyList<ModPackage> Resolve(IEnumerable<ModPackage> packages, IEnumerable<string> roots) =>
        ModDependencyGraph.Resolve(packages.Where(package => Unavailable(package) is null), roots);

    private async Task ApplyAsync(IReadOnlyList<ModPackage> packages, IEnumerable<string> roots, Preferences next,
        HashSet<string> explicitlyTrusted, HashSet<string> reload, bool save, IReadOnlyList<ModPackage>? candidates = null)
    {
        var plan = Resolve(candidates ?? packages, roots);
        var before = Volatile.Read(ref _active).Mods;
        var retained = before.ToDictionary(item => item.Package.Manifest.Id, StringComparer.Ordinal);
        var after = new List<ModActivation>();
        var staged = new List<ModActivation>();
        var changed = new HashSet<string>(reload, StringComparer.Ordinal);
        try
        {
            foreach (var package in plan)
            {
                string id = package.Manifest.Id;
                bool dependencyChanged = package.Manifest.Dependencies?.Any(dependency => changed.Contains(dependency.Id)) == true;
                if (!dependencyChanged && !reload.Contains(id) && retained.TryGetValue(id, out var current) &&
                    current.Package.Root == package.Root && current.Package.Manifest.Version == package.Manifest.Version)
                {
                    after.Add(current);
                    continue;
                }
                changed.Add(id);
                var candidate = new ModActivation(this, package, Path.Combine(_shadow, Guid.NewGuid().ToString("N")));
                staged.Add(candidate);
                string hash = package.HashAndCopy(candidate.Shadow);
                string key = TrustKey(package);
                if (!explicitlyTrusted.Contains(id) && next.Trust.GetValueOrDefault(key) != hash)
                    throw new InvalidOperationException($"'{package.Manifest.Name}' needs approval for this package content.");
                if (explicitlyTrusted.Contains(id)) next.Trust[key] = hash;
                candidate.ActivatingDependencies = after.SelectMany(item =>
                    item.Scope.Registrations.OfType<ModRegistration<MessageEndpoint>>()).ToArray();
                await candidate.ActivateAsync().ConfigureAwait(false);
                after.Add(candidate);
            }
            ValidateHooks(after);
            if (save) Save(next);
        }
        catch
        {
            for (int index = staged.Count - 1; index >= 0; index--)
                await RetireAsync(staged[index]).ConfigureAwait(false);
            throw;
        }
        if (!before.SequenceEqual(after))
        {
            Volatile.Write(ref _active, new(after.ToArray()));
            if (before.Except(after).Concat(after.Except(before)).Any(HasLoadHook))
                Interlocked.Increment(ref _loadGeneration);
            Interlocked.Increment(ref _generation);
        }
        PublishCatalog(packages);
        for (int i = before.Length - 1; i >= 0; i--)
            if (!after.Contains(before[i]))
                await RetireAsync(before[i]).ConfigureAwait(false);
        foreach (var mod in staged)
            await mod.NotifyActivatedAsync().ConfigureAwait(false);

        static bool HasLoadHook(ModActivation mod) => mod.Scope.Registrations.Any(entry =>
            entry is ModRegistration<ChartProcessor> or ModRegistration<AssetProcessor> or ModRegistration<AudioHook> or
                ModRegistration<CustomEventCompiler>);
    }

    private static void ValidateHooks(List<ModActivation> active)
    {
        var registrations = active.SelectMany(item => item.Scope.Registrations).ToArray();
        foreach (var group in registrations.GroupBy(registration => registration.GetType()))
            Order(group.ToArray());
        var shaders = registrations.OfType<ModRegistration<RenderProcessor>>()
            .Where(entry => entry.Value.Shader is not null).ToDictionary(entry => entry.Descriptor.Id);
        foreach (var entry in shaders.Values)
            foreach (string input in entry.Value.Shader!.Inputs ?? [])
                if (!shaders.TryGetValue(input, out var producer) || !producer.Value.Shader!.ProducesAuxiliary ||
                    producer.Value.Stage > entry.Value.Stage)
                    throw new InvalidOperationException($"Render pass '{entry.Descriptor.Id}' cannot read '{input}'.");
    }

    private static T[] Order<T>(T[] entries) where T : ModRegistration
    {
        var ordered = new List<T>(entries.Length);
        var remaining = entries.OrderBy(entry => entry.Descriptor.Order)
            .ThenBy(entry => entry.Descriptor.Id, StringComparer.Ordinal).ToList();
        var pending = remaining.Select(entry => entry.Descriptor.Id).ToHashSet(StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            int index = remaining.FindIndex(entry => entry.Descriptor.After?.Any(pending.Contains) != true);
            if (index < 0) throw new InvalidOperationException("A Mod hook ordering cycle was detected.");
            T entry = remaining[index];
            remaining.RemoveAt(index);
            pending.Remove(entry.Descriptor.Id);
            ordered.Add(entry);
        }
        return ordered.ToArray();
    }

    private async Task RetireAsync(ModActivation mod)
    {
        string shadow = mod.Shadow;
        try { await mod.StopAsync().ConfigureAwait(false); }
        catch (Exception error) { Log(mod.Package.Manifest.Id, error.GetBaseException().Message); }
        try { DeleteUnder(shadow, _shadow); }
        catch (IOException) { Log(mod.Package.Manifest.Id, $"Staged files remain in '{shadow}' until the assembly is released."); }
        catch (UnauthorizedAccessException error) { Log(mod.Package.Manifest.Id, error.Message); }
    }

    private IReadOnlyList<ModPackage> Discover(Preferences settings)
    {
        var packages = new List<ModPackage>();
        ReadInstalled(_packages, SearchDirectory);
        if (!SamePath(_packages, _legacyPackages)) ReadInstalled(_legacyPackages, null);
        foreach (string folder in settings.Development.Order(StringComparer.Ordinal))
            Read(folder, development: true);
        if (Directory.Exists(SearchDirectory))
        {
            var ignored = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            {
                _packages, _shadow, Path.Combine(Root, "data"),
            };
            ignored.UnionWith(settings.Development);
            foreach (string path in Directory.EnumerateDirectories(SearchDirectory))
                if (!Path.GetFileName(path).StartsWith('.') && !ignored.Contains(path) && IsRegularPath(path))
                    Read(path, development: false, isDropIn: true);
        }
        return packages;

        void ReadInstalled(string root, string? settingsDirectory)
        {
            if (!Directory.Exists(root) || !IsRegularPath(root)) return;
            foreach (string id in Directory.EnumerateDirectories(root))
                if (!Path.GetFileName(id).StartsWith('.') && IsRegularPath(id))
                    foreach (string version in Directory.EnumerateDirectories(id))
                        if (IsRegularPath(version)) Read(version, development: false, settingsDirectory: settingsDirectory);
        }

        void Read(string folder, bool development, bool isDropIn = false, string? settingsDirectory = null)
        {
            try { packages.Add(ModPackage.Read(folder, development, isDropIn: isDropIn, settingsDirectory: settingsDirectory)); }
            catch (FileNotFoundException) when (isDropIn) { } // A folder may still be copying.
            catch (DirectoryNotFoundException) when (isDropIn) { }
            catch (Exception error) { Log(Path.GetFileName(folder), error.GetBaseException().Message); }
        }
    }

    private bool IsDataDirectory(string path) => SamePath(path, _shadow) || SamePath(path, Path.Combine(Root, "data"));
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static bool SamePath(string first, string second) => first.Equals(second, PathComparison);

    private static bool IsRegularPath(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private void PublishCatalog(IReadOnlyList<ModPackage> packages)
    {
        var active = Volatile.Read(ref _active).Mods.Select(item => item.Package).ToDictionary(item => item.Manifest.Id);
        Volatile.Write(ref _catalog, packages.Concat(active.Values).GroupBy(package => package.Manifest.Id)
            .OrderBy(group => group.Key, StringComparer.Ordinal).Select(group =>
            {
                var package = active.GetValueOrDefault(group.Key) ?? group.OrderBy(item => Unavailable(item) is not null)
                    .ThenByDescending(item => ModVersion.Parse(item.Manifest.Version)).ThenByDescending(item => item.Development)
                    .ThenBy(item => item.IsDropIn).ThenBy(item => item.Root, StringComparer.Ordinal).First();
                return new ModCatalogEntry(package.Manifest, package.Development, active.ContainsKey(group.Key), _preferences.Roots.Contains(group.Key),
                    _safeMode ? "Safe mode." : Unavailable(package), !package.IsDropIn);
            }).ToArray());
    }

    private static string TrustKey(ModPackage package) => $"{package.Manifest.Id}|{package.Manifest.Version}|{package.Root}";

    private void Save(Preferences next)
    {
        Directory.CreateDirectory(Root);
        string temporary = _settingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(next));
            File.Move(temporary, _settingsFile, overwrite: true);
            _preferences = next;
        }
        finally { File.Delete(temporary); }
    }

    internal string GetDataDirectory(string id)
    {
        string path = Path.Combine(Root, "data", id);
        Directory.CreateDirectory(path);
        return path;
    }

    private void EnsureEnabled()
    {
        if (_safeMode) throw new InvalidOperationException("Mods are disabled in safe mode.");
        if (Target != UiHost.Editor) throw new InvalidOperationException("Mods are only available in the editor.");
    }

    private async Task RunAsync(Func<Task> operation)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await operation().ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            StopWatching();
            var active = Interlocked.Exchange(ref _active, new([])).Mods;
            for (int i = active.Length - 1; i >= 0; i--)
                await RetireAsync(active[i]).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    private static void DeleteUnder(string path, string root)
    {
        string absolute = Path.GetFullPath(path);
        if (!IsUnder(absolute, root))
            throw new InvalidOperationException("Mod cleanup path is outside the package store.");
        if (Directory.Exists(absolute))
            Directory.Delete(absolute, recursive: true);
    }

    private static bool IsUnder(string path, string root) => Path.GetFullPath(path)
        .StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, PathComparison);
}

internal sealed class ModActivation : IModRegistrationOwner
{
    private sealed class LoadContext(string entry) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(entry);
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == typeof(ISharpFaiMod).Assembly.GetName().Name) return typeof(ISharpFaiMod).Assembly;
            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path is null && assemblyName.Name is { } name)
            {
                string adjacent = ModPackage.Resolve(Path.GetDirectoryName(entry)!, name + ".dll");
                if (File.Exists(adjacent)) path = adjacent;
            }
            return path is null ? null : LoadManaged(path);
        }
        internal Assembly LoadManaged(string path)
        {
            using var library = File.OpenRead(path);
            string symbols = Path.ChangeExtension(path, ".pdb");
            if (!File.Exists(symbols)) return LoadFromStream(library);
            using var pdb = File.OpenRead(symbols);
            return LoadFromStream(library, pdb);
        }
        protected override nint LoadUnmanagedDll(string name)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? 0 : LoadUnmanagedDllFromPath(path);
        }
    }

    private sealed class Lease(ModActivation owner) : IDisposable
    {
        private ModActivation? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }

    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private LoadContext? _context;
    private ISharpFaiMod? _instance;
    private int _leases;
    private volatile bool _stopping;

    internal ModActivation(ModHost host, ModPackage package, string shadow)
    {
        Package = package;
        Shadow = shadow;
        Scope = new(host, this);
        Token = _lifetime.Token;
    }
    internal ModPackage Package { get; }
    internal string Shadow { get; }
    internal ModScope Scope { get; }
    internal CancellationToken Token { get; }
    internal bool Stopping => _stopping;
    string IModRegistrationOwner.Id => Package.Manifest.Id;
    bool IModRegistrationOwner.Stopping => _stopping;
    CancellationToken IModRegistrationOwner.Lifetime => Token;
    IDisposable? IModRegistrationOwner.Acquire() => Acquire();
    string IModRegistrationOwner.Text(string key, string? fallback) => Scope.Text(key, fallback);
    void IModRegistrationOwner.Log(string message) => Scope.Log(message);
    internal IReadOnlyList<ModRegistration<MessageEndpoint>> ActivatingDependencies { get; set; } = [];

    internal async Task ActivateAsync()
    {
        // Use the verified staged copy for both manifest and entry assembly.
        var copy = ModPackage.Read(Shadow, Package.Development);
        if (JsonSerializer.Serialize(copy.Manifest) != JsonSerializer.Serialize(Package.Manifest))
            throw new IOException("The Mod manifest changed during activation. Refresh the catalog and retry.");
        string entry = ModPackage.Resolve(Shadow, copy.Manifest.EntryAssembly);
        _context = new(entry);
        var assembly = _context.LoadManaged(entry);
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) when (!string.IsNullOrWhiteSpace(copy.Manifest.EntryType))
        {
            types = error.Types.OfType<Type>().ToArray();
        }
        Type? type = string.IsNullOrWhiteSpace(copy.Manifest.EntryType)
            ? types.SingleOrDefault(candidate => !candidate.IsAbstract && typeof(ISharpFaiMod).IsAssignableFrom(candidate))
            : assembly.GetType(copy.Manifest.EntryType, throwOnError: true)!;
        if (type is not null)
        {
            _instance = Activator.CreateInstance(type) as ISharpFaiMod ??
                throw new InvalidDataException("The entry type must implement ISharpFaiMod.");
            if (_instance is IModSettings settings) Scope.RegisterSettings(settings);
            await _instance.ActivateAsync(Scope, Token).ConfigureAwait(false);
        }
        int menus = Scope.RegisterMenuItems(types, _instance);
        if (_instance is null && menus == 0)
            throw new InvalidDataException("The Mod must define a static [MenuItem] method or an ISharpFaiMod entry type.");
        Scope.Validate();
        Scope.IsActivating = false;
        ActivatingDependencies = [];
    }

    internal IDisposable? Acquire()
    {
        lock (_gate)
        {
            if (_stopping) return null;
            _leases++;
            return new Lease(this);
        }
    }

    internal async ValueTask NotifyActivatedAsync()
    {
        if (_instance is not IModActivationObserver observer) return;
        try { await observer.OnActivatedAsync(Token).ConfigureAwait(false); }
        catch (Exception error) { Scope.Log(error.GetBaseException().Message); }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_leases == 0 && _stopping) _drained.TrySetResult();
        }
    }

    internal async Task StopAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            if (_leases == 0) _drained.TrySetResult();
        }
        try { _lifetime.Cancel(); }
        catch (AggregateException error) { Scope.Log(error.GetBaseException().Message); }
        await _drained.Task.ConfigureAwait(false);
        try
        {
            if (_instance is not null) await _instance.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _instance = null;
            ActivatingDependencies = [];
            _context?.Unload();
            _context = null;
            _lifetime.Dispose();
        }
    }
}

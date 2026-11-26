namespace SharpFAI.Editor.Mods;

internal static class ModDependencyGraph
{
    internal static IReadOnlyList<ModPackage> Resolve(IEnumerable<ModPackage> available, IEnumerable<string> roots)
    {
        var versions = available.GroupBy(package => package.Manifest.Id).ToDictionary(group => group.Key,
            group => group.OrderByDescending(package => ModVersion.Parse(package.Manifest.Version))
                .ThenByDescending(package => package.Development)
                .ThenBy(package => package.IsDropIn)
                .ThenBy(package => package.Root, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var selected = new Dictionary<string, ModPackage>(StringComparer.Ordinal);
        var constraints = roots.Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, id => new List<string> { "*" }, StringComparer.Ordinal);
        string? failure = null;
        if (!Choose(constraints))
            throw new InvalidOperationException(failure ?? "No compatible Mod dependency graph.");
        return Order(selected);

        bool Choose(Dictionary<string, List<string>> requested)
        {
            foreach (var pair in selected)
                if (requested[pair.Key].Any(range => !ModVersion.Supports(range, pair.Value.Manifest.Version)))
                {
                    failure = $"Mod '{pair.Key}' cannot satisfy {string.Join(", ", requested[pair.Key])}.";
                    return false;
                }

            string? id = requested.Keys.Where(key => !selected.ContainsKey(key)).Order(StringComparer.Ordinal).FirstOrDefault();
            if (id is null)
            {
                try { Order(selected); return true; }
                catch (InvalidOperationException error) { failure = error.Message; return false; }
            }
            if (!versions.TryGetValue(id, out var candidates) ||
                !candidates.Any(candidate => requested[id].All(range => ModVersion.Supports(range, candidate.Manifest.Version))))
            {
                failure = $"Missing compatible Mod '{id}' ({string.Join(", ", requested[id])}).";
                return false;
            }
            foreach (var candidate in candidates)
            {
                if (requested[id].Any(range => !ModVersion.Supports(range, candidate.Manifest.Version)))
                    continue;
                selected.Add(id, candidate);
                var next = requested.ToDictionary(pair => pair.Key, pair => new List<string>(pair.Value), StringComparer.Ordinal);
                foreach (var dependency in candidate.Manifest.Dependencies ?? [])
                {
                    if (!next.TryGetValue(dependency.Id, out var ranges))
                        next.Add(dependency.Id, ranges = []);
                    ranges.Add(dependency.Version);
                }
                if (Choose(next)) return true;
                selected.Remove(id);
            }
            return false;
        }
    }

    private static List<ModPackage> Order(Dictionary<string, ModPackage> selected)
    {
        var ordered = new List<ModPackage>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in selected.Keys.Order(StringComparer.Ordinal)) Visit(id);
        return ordered;

        void Visit(string id)
        {
            if (done.Contains(id)) return;
            if (!visiting.Add(id))
                throw new InvalidOperationException($"Mod dependency cycle at '{id}'.");
            foreach (var dependency in (selected[id].Manifest.Dependencies ?? []).OrderBy(dependency => dependency.Id, StringComparer.Ordinal))
                Visit(dependency.Id);
            visiting.Remove(id);
            done.Add(id);
            ordered.Add(selected[id]);
        }
    }

    internal static HashSet<string> Dependents(string id, IEnumerable<ModPackage> packages)
    {
        var affected = new HashSet<string>(StringComparer.Ordinal) { id };
        foreach (var package in packages)
            if (package.Manifest.Dependencies?.Any(dependency => affected.Contains(dependency.Id)) == true)
                affected.Add(package.Manifest.Id);
        return affected;
    }

    internal static HashSet<string> Dependencies(string id, IReadOnlyList<ModPackage> packages)
    {
        var affected = new HashSet<string>(StringComparer.Ordinal) { id };
        for (int i = packages.Count - 1; i >= 0; i--)
            if (affected.Contains(packages[i].Manifest.Id))
                foreach (var dependency in packages[i].Manifest.Dependencies ?? [])
                    affected.Add(dependency.Id);
        return affected;
    }
}

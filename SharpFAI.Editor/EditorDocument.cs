using Newtonsoft.Json.Linq;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Serialization;

namespace SharpFAI.Editor;

internal sealed class EditorDocument
{
    private readonly Level _level;
    private readonly JArray _angles;
    private readonly JArray _actions;
    private readonly JArray _decorations;
    private readonly Dictionary<int, List<JObject>> _eventsByFloor = [];
    private readonly List<Edit> _edits = [];
    private int _revision;
    private int _savedRevision;
    private const string TimeSignatureKey = "sharpFaiTimeSignatures";
    private readonly record struct Edit(Action Redo, Action Undo, bool Events, JObject? Effect = null,
        object? Group = null, bool SettingsOnly = false, bool EditorOnly = false);
    private int _lastChartChange;
    private readonly Dictionary<JObject, int> _effectChanges = new(ReferenceEqualityComparer.Instance);
    private int _lastFullPlaybackChange;

    public EditorDocument(string? path = null)
    {
        Path = path is null ? null : System.IO.Path.GetFullPath(path);
        _level = Path is null ? Level.CreateNewLevel() : new Level(Path);
        _angles = EnsureArray("angleData");
        _actions = EnsureArray("actions");
        _decorations = EnsureArray("decorations");
        if (_level.root["settings"] is not JObject)
            _level.root["settings"] = new JObject();
        Settings = (JObject)_level.root["settings"]!;
        TimeSignatures = EditorTimeSignatures.Read(_level.root[TimeSignatureKey]);
        foreach (JObject action in _actions.OfType<JObject>())
        {
            if (Type(action) == "SetFilterAdvanced" && action["filterProperties"] is JValue { Type: JTokenType.String } encoded &&
                CameraFilterEvent.ParseFilterProperties(encoded) is { } properties)
                action["filterProperties"] = properties;
        }
        IndexEvents();
    }

    public string? Path { get; private set; }
    public bool Modified => _savedRevision != _revision;
    public bool CanUndo => _revision > 0;
    public bool CanRedo => _revision < _edits.Count;
    public int ChangeVersion { get; private set; }
    public int PlaybackVersion { get; private set; }
    public JObject Settings { get; }
    public EditorTimeSignatures TimeSignatures { get; private set; }
    public JArray Decorations => _decorations;
    public int LastFloor => _angles.Count;
    public double IncomingAngle(int floor)
    {
        if (_angles.Count == 0)
            return 0;
        int index = Math.Clamp(floor - 1, 0, _angles.Count - 1);
        double angle = 0;
        int spins = 0;
        for (; index >= 0; index--)
        {
            angle = _angles[index]!.Value<double>();
            if (angle != 999)
                return (angle + spins * 180) % 360;
            spins++;
        }
        return spins * 180 % 360;
    }
    public IReadOnlyList<JObject> Events(int floor) =>
        _eventsByFloor.TryGetValue(floor, out List<JObject>? list) ? list : [];

    public static int Floor(JObject value) => value.Value<int?>("floor") ?? 0;
    public static string Type(JObject value) => value.Value<string>("eventType") ?? "";

    public void SetTimeSignature(double beat, TimeSignature? signature)
    {
        var before = TimeSignatures;
        var after = before.WithChange(beat, signature);
        if (before.Changes.SequenceEqual(after.Changes))
            return;
        JToken? previous = _level.root[TimeSignatureKey]?.DeepClone();
        JToken? next = after.Write();
        Apply(new(() => Write(after, next), () => Write(before, previous), Events: false, EditorOnly: true));

        void Write(EditorTimeSignatures value, JToken? data)
        {
            TimeSignatures = value;
            if (data is null)
                _level.root.Remove(TimeSignatureKey);
            else
                _level.root[TimeSignatureKey] = data.DeepClone();
        }
    }

    public void Set(JObject target, string name, JToken? value, object? group = null)
    {
        JToken? before = target[name]?.DeepClone();
        if (JToken.DeepEquals(before, value))
            return;
        bool isEffect = name is not ("floor" or "eventType") &&
            (ReferenceEquals(target.Parent, _actions) || ReferenceEquals(target.Parent, _decorations)) &&
            LevelRenderer.SupportsEditorEffect(Type(target));
        Apply(new(() => Write(value), () => Write(before), name == "floor" && ReferenceEquals(target.Parent, _actions),
            isEffect ? target : null, group, SettingsOnly: ReferenceEquals(target, Settings)));

        void Write(JToken? token)
        {
            if (token is null)
                target.Remove(name);
            else
                target[name] = token.DeepClone();
        }
    }

    public void InsertTiles(int floor, double[] angles) => InsertTiles(floor, angles, [], []);

    public sealed record TileClipboard(double[] Angles, JObject[] Actions, JObject[] Decorations,
        int SourceFirst = 1, int SourceLastFloor = 0);

    public TileClipboard CopyTiles(int first, int last)
    {
        first = Math.Max(1, first);
        return new(_angles.Skip(first - 1).Take(last - first + 1).Values<double>().ToArray(),
            CopyRange(_actions, first, last), CopyRange(_decorations, first, last), first, LastFloor);
    }

    public IEnumerable<JObject> ActionsOfType(string type) =>
        _actions.OfType<JObject>().Where(item => Type(item) == type);

    private static JObject[] CopyRange(JArray source, int first, int last) => source.OfType<JObject>()
        .Where(item => Floor(item) >= first && Floor(item) <= last).Select(item =>
        {
            JObject copy = (JObject)item.DeepClone();
            copy["floor"] = Floor(item) - first;
            return copy;
        }).ToArray();

    public void PasteTiles(int after, TileClipboard clipboard, bool includeDecorations)
    {
        // A whole-tile paste copies the source's events as-is onto newly inserted floors.
        if (clipboard.Angles.Length > 0)
            InsertTiles(after, clipboard.Angles, clipboard.Actions, includeDecorations ? clipboard.Decorations : [], clipboard);
    }

    private void InsertTiles(int floor, double[] angles, JObject[] actions, JObject[] decorations, TileClipboard? clipboard = null)
    {
        if (floor < 0 || floor > LastFloor || angles.Length == 0) return;
        int oldLast = LastFloor;
        var references = ReferenceChanges(oldLast, oldLast + angles.Length,
            target => target > floor ? target + angles.Length : target);
        var moved = AnchorsAfter(floor);
        JValue[] values = angles.Select(angle => new JValue(angle)).ToArray();
        var events = actions.Select(AtDestination).ToArray();
        var objects = decorations.Select(AtDestination).ToArray();
        Apply(new(
            () =>
            {
                for (int i = 0; i < values.Length; i++) _angles.Insert(floor + i, values[i]);
                MoveAnchors(moved, values.Length);
                WriteReferences(references, true);
                foreach (var item in events) _actions.Add(item);
                foreach (var item in objects) _decorations.Add(item);
            },
            () =>
            {
                for (int i = values.Length - 1; i >= 0; i--) _angles.RemoveAt(floor + i);
                foreach (var item in events) item.Remove();
                foreach (var item in objects) item.Remove();
                MoveAnchors(moved, 0);
                WriteReferences(references, false);
            },
            true));

        JObject AtDestination(JObject item)
        {
            JObject copy = (JObject)item.DeepClone();
            copy["floor"] = floor + 1 + Floor(item);
            if (clipboard is not null)
                foreach (string key in new[] { "startTile", "endTile" })
                    if (copy[key] is JArray reference && reference.Count >= 2 &&
                        reference[1]!.Value<string>() is "Start" or "End")
                    {
                        bool end = reference[1]!.Value<string>() == "End";
                        int target = reference[0]!.Value<int>() + (end ? clipboard.SourceLastFloor : 0);
                        int relative = target - clipboard.SourceFirst;
                        target = relative >= 0 && relative < clipboard.Angles.Length
                            ? floor + 1 + relative : target > floor ? target + angles.Length : target;
                        reference[0] = target - (end ? oldLast + angles.Length : 0);
                    }
            return copy;
        }
    }

    public void DeleteTiles(int first, int last)
    {
        if (first < 1 || last > LastFloor || first > last)
            return;
        int count = last - first + 1;
        int newLast = LastFloor - count;
        var references = ReferenceChanges(LastFloor, newLast,
            target => target > last ? target - count : target >= first ? Math.Min(first, newLast) : target);
        JToken[] angles = _angles.Skip(first - 1).Take(count).ToArray();
        var moved = AnchorsAfter(last);
        var removed = _actions.Select((token, index) => (token, index))
            .Where(entry => entry.token is JObject item && Floor(item) >= first && Floor(item) <= last).ToArray();
        var attached = _decorations.OfType<JObject>().Where(item => Floor(item) >= first && Floor(item) <= last)
            .Select(item => (Item: item, Floor: Floor(item))).ToArray();
        Apply(new(
            () =>
            {
                for (int i = last - 1; i >= first - 1; i--) _angles.RemoveAt(i);
                foreach (var (item, _) in removed)
                    item.Remove();
                foreach (var (item, _) in attached)
                    item["floor"] = first - 1;
                MoveAnchors(moved, -count);
                WriteReferences(references, true);
            },
            () =>
            {
                for (int i = 0; i < count; i++) _angles.Insert(first - 1 + i, angles[i]);
                foreach (var (item, index) in removed)
                    _actions.Insert(index, item);
                MoveAnchors(attached, 0);
                MoveAnchors(moved, 0);
                WriteReferences(references, false);
            }, true));
    }

    public void TransformTiles(int first, int last, Func<double, double> transform, TrackTransform? eventTransform = null)
    {
        int start = Math.Max(0, first - 1);
        var changes = _angles.Skip(start).Take(last - start).OfType<JValue>()
            .Select(item => (Item: item, Angle: item.Value<double>())).Where(entry => entry.Angle != 999)
            .Select(entry => (entry.Item, Original: entry.Angle, Updated: (transform(entry.Angle) % 360 + 360) % 360))
            .Where(entry => entry.Original != entry.Updated).ToArray();
        var eventChanges = eventTransform is { } operation
            ? TrackEventTransforms(first, last, operation).ToArray() : [];
        if (changes.Length != 0 || eventChanges.Length != 0)
            Apply(new(() => Write(true), () => Write(false), false));

        void Write(bool redo)
        {
            foreach (var (item, original, updated) in changes)
                item.Value = redo ? updated : original;
            foreach (var (item, key, original, updated) in eventChanges)
                item[key] = redo ? updated : original;
        }
    }

    private IEnumerable<(JObject Item, string Key, JArray Original, JArray Updated)> TrackEventTransforms(
        int first, int last, TrackTransform transform)
    {
        for (int floor = first; floor <= last; floor++)
        foreach (JObject item in Events(floor))
        {
            string? key = Type(item) switch
            {
                "MoveTrack" or "PositionTrack" => "positionOffset",
                "MoveCamera" => "position",
                _ => null,
            };
            if (key is null || item[key] is not JArray { Count: 2 } vector ||
                !TryReadAxis(vector[0], out double? x) || !TryReadAxis(vector[1], out double? y))
                continue;
            var next = transform.Position(x, y);
            if (next != (x, y))
                yield return (item, key, vector, new JArray(new JValue(next.X), new JValue(next.Y)));
        }

        static bool TryReadAxis(JToken? token, out double? value)
        {
            value = null;
            if (token?.Type == JTokenType.Null)
                return true;
            if (token?.Type is not (JTokenType.Float or JTokenType.Integer))
                return false;
            value = token.Value<double>();
            return double.IsFinite(value.Value);
        }
    }

    public bool CanAddEvent(int floor, string type) =>
        EditorCatalog.ExclusiveEventSlot(type) is not { } slot ||
        !Events(floor).Any(item => EditorCatalog.ExclusiveEventSlot(Type(item)) == slot);

    public void PasteEvents(int floor, IEnumerable<JObject> items, bool decoration)
    {
        var occupied = decoration ? null : Events(floor).Select(item => EditorCatalog.ExclusiveEventSlot(Type(item)))
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        List<JObject> copies = [];
        foreach (JObject item in items)
        {
            if (occupied is not null && EditorCatalog.ExclusiveEventSlot(Type(item)) is { } slot && !occupied.Add(slot))
                continue;
            JObject copy = (JObject)item.DeepClone();
            copy["floor"] = floor;
            copies.Add(copy);
        }
        if (copies.Count == 0)
            return;
        JArray list = decoration ? _decorations : _actions;
        Apply(new(
            () => { foreach (var item in copies) list.Add(item); },
            () => { foreach (var item in copies) item.Remove(); }, !decoration));
    }

    public bool Add(JObject value, bool decoration)
    {
        if (!decoration && !CanAddEvent(Floor(value), Type(value)))
            return false;
        JArray list = decoration ? _decorations : _actions;
        Apply(new(() => list.Add(value), value.Remove, !decoration));
        return true;
    }

    public void Remove(JObject value)
    {
        if (value.Parent is not JArray list)
            return;
        int index = list.IndexOf(value);
        Apply(new(value.Remove, () => list.Insert(index, value), list == _actions));
    }

    public void MoveDecoration(JObject value, JObject target)
    {
        int from = _decorations.IndexOf(value);
        int to = _decorations.IndexOf(target);
        if (from < 0 || to < 0 || from == to)
            return;
        Apply(new(
            () => { value.Remove(); _decorations.Insert(to, value); },
            () => { value.Remove(); _decorations.Insert(from, value); }, false));
    }

    public void RemoveDecorations(IEnumerable<JObject> selection)
        => RemoveItems(selection, decoration: true);

    public void RemoveItems(IEnumerable<JObject> selection, bool decoration)
    {
        JArray list = decoration ? _decorations : _actions;
        var entries = selection.Where(item => ReferenceEquals(item.Parent, list))
            .Select(item => (Item: item, Index: list.IndexOf(item))).OrderBy(entry => entry.Index).ToArray();
        if (entries.Length == 0)
            return;
        Apply(new(
            () => { foreach (var (item, _) in entries) item.Remove(); },
            () => { foreach (var (item, index) in entries) list.Insert(index, item); },
            !decoration));
    }

    public void PasteDecorations(IEnumerable<JObject> selection)
    {
        JObject[] copies = selection.Select(item => (JObject)item.DeepClone()).ToArray();
        if (copies.Length != 0)
            Apply(new(
                () => { foreach (var item in copies) _decorations.Add(item); },
                () => { foreach (var item in copies) item.Remove(); }, false));
    }

    public void SetDecorationLocks(IEnumerable<JObject> selection, bool locked)
    {
        var items = selection.Select(item => (Item: item, Previous: item["locked"]?.DeepClone())).ToArray();
        if (items.Length == 0)
            return;
        Apply(new(
            () => { foreach (var (item, _) in items) Write(item, locked ? new JValue(true) : null); },
            () => { foreach (var (item, previous) in items) Write(item, previous); }, false));

        static void Write(JObject item, JToken? token)
        {
            if (token is null) item.Remove("locked");
            else item["locked"] = token.DeepClone();
        }
    }

    public void Undo()
    {
        if (!CanUndo)
            return;
        Edit edit = _edits[--_revision];
        edit.Undo();
        Refresh(edit);
    }

    public void Redo()
    {
        if (!CanRedo)
            return;
        Edit edit = _edits[_revision++];
        edit.Redo();
        Refresh(edit);
    }

    public void Save(string path)
    {
        string destination = System.IO.Path.GetFullPath(path);
        EditorFileStorage.WriteChart(destination, _level.root, overwrite: true);
        Path = destination;
        _savedRevision = _revision;
    }

    public JObject CaptureBackup() => (JObject)_level.root.DeepClone();

    public string PlaybackPath => Path ?? System.IO.Path.Combine(Environment.CurrentDirectory, "untitled.adofai");
    public Level SnapshotForPlayback() => _level.CreateSnapshot(PlaybackPath);

    public bool HasOnlyEffectChangesSince(int version) => version >= _lastFullPlaybackChange;
    public bool CanReusePlaybackChart(int version) => version >= _lastChartChange;

    public LevelRenderer.EditorEffectPatch[] EffectChangesSince(int version)
    {
        if (version < _lastFullPlaybackChange)
            return [];
        return _effectChanges.Where(change => change.Value > version).Select(change =>
        {
            bool decoration = ReferenceEquals(change.Key.Parent, _decorations);
            return new LevelRenderer.EditorEffectPatch((decoration ? _decorations : _actions).IndexOf(change.Key),
                decoration, (JObject)change.Key.DeepClone());
        }).ToArray();
    }

    public void ForgetEffectChangesThrough(int version)
    {
        if (version == PlaybackVersion)
            _effectChanges.Clear();
        else
            foreach (var item in _effectChanges.Where(change => change.Value <= version).Select(change => change.Key).ToArray())
                _effectChanges.Remove(item);
    }

    public string ImportDecorationImage(string sourcePath)
    {
        string folder = System.IO.Path.GetDirectoryName(PlaybackPath)!;
        string relative = System.IO.Path.GetRelativePath(folder, sourcePath);
        if (IsLocalAsset(relative))
            return relative;

        string name = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
        string extension = System.IO.Path.GetExtension(sourcePath);
        for (int suffix = 1; ; suffix++)
        {
            string filename = (suffix == 1 ? name : $"{name}-{suffix}") + extension;
            string destination = System.IO.Path.Combine(folder, filename);
            try
            {
                File.Copy(sourcePath, destination, overwrite: false);
                return filename;
            }
            catch (IOException) when (File.Exists(destination))
            {
                // An existing asset may be used by another event. Choose a fresh name.
            }
        }
    }

    public static bool IsLocalAsset(string relative) => !System.IO.Path.IsPathRooted(relative) &&
        relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal) &&
        !relative.StartsWith("..\\", StringComparison.Ordinal);

    private JArray EnsureArray(string name)
    {
        if (_level.root[name] is JArray array)
            return array;
        array = [];
        _level.root[name] = array;
        return array;
    }

    public void CancelEditGroup(object group)
    {
        if (_revision == _edits.Count && _revision > 0 && ReferenceEquals(_edits[_revision - 1].Group, group) &&
            _savedRevision != _revision)
        {
            Undo();
            _edits.RemoveAt(_revision);
        }
    }

    private void Apply(Edit edit)
    {
        edit.Redo();
        if (edit.Group is not null && _revision == _edits.Count && _revision > 0 &&
            _savedRevision != _revision && ReferenceEquals(_edits[_revision - 1].Group, edit.Group))
        {
            var previous = _edits[_revision - 1];
            _edits[_revision - 1] = edit with { Undo = previous.Undo, Events = previous.Events || edit.Events,
                SettingsOnly = previous.SettingsOnly && edit.SettingsOnly };
            Refresh(edit);
            return;
        }
        if (_revision < _edits.Count)
        {
            _edits.RemoveRange(_revision, _edits.Count - _revision);
            if (_savedRevision > _revision)
                _savedRevision = -1;
        }
        _edits.Add(edit);
        _revision++;
        Refresh(edit);
    }

    private void Refresh(Edit edit)
    {
        ChangeVersion++;
        if (edit.EditorOnly) return;
        PlaybackVersion = ChangeVersion;
        if (!edit.SettingsOnly) _lastChartChange = ChangeVersion;
        if (edit.Effect is { } effect)
            _effectChanges[effect] = ChangeVersion;
        else
        {
            _lastFullPlaybackChange = ChangeVersion;
            _effectChanges.Clear();
        }
        if (edit.Events)
            IndexEvents();
    }

    private readonly record struct TileReference(JArray Value, int Before, int After);

    private TileReference[] ReferenceChanges(int oldLast, int newLast, Func<int, int> relocate) =>
        _actions.OfType<JObject>().Concat(_decorations.OfType<JObject>())
            .SelectMany(item => new[] { item["startTile"], item["endTile"] }).OfType<JArray>()
            .Where(value => value.Count >= 2 && value[1]!.Value<string>() is "Start" or "End")
            .Select(value =>
            {
                int before = value[0]!.Value<int>();
                bool end = value[1]!.Value<string>() == "End";
                return new TileReference(value, before, relocate(before + (end ? oldLast : 0)) - (end ? newLast : 0));
            }).ToArray();

    private static void WriteReferences(TileReference[] references, bool redo)
    {
        foreach (var item in references) item.Value[0] = redo ? item.After : item.Before;
    }

    public void PositionTiles(int first, int last, double x, double y)
    {
        if (first < 0 || last > LastFloor || first > last || !double.IsFinite(x) || !double.IsFinite(y) ||
            Math.Abs(x) + Math.Abs(y) < 1e-9) return;
        bool single = first == last;
        var changes = new List<(JObject Item, JObject? Before, JObject After)>();
        Add(first, x, y, single);
        if (!single && last < LastFloor) Add(last + 1, -x, -y, false);
        Apply(new(
            () =>
            {
                foreach (var change in changes)
                {
                    if (change.Before is null) _actions.Add(change.Item);
                    Replace(change.Item, change.After);
                }
            },
            () =>
            {
                foreach (var change in changes)
                    if (change.Before is null) change.Item.Remove();
                    else Replace(change.Item, change.Before);
            }, true));

        void Add(int floor, double dx, double dy, bool justThis)
        {
            JObject? existing = Events(floor).LastOrDefault(item => Type(item) == "PositionTrack" &&
                (item.Value<bool?>("justThisTile") ?? false) == justThis);
            JObject item = existing ?? new JObject { ["floor"] = floor, ["eventType"] = "PositionTrack" };
            JObject? before = existing is null ? null : (JObject)item.DeepClone();
            JObject after = (JObject)item.DeepClone();
            JArray? offset = after["disabled"] is JObject flags && flags.Value<bool?>("positionOffset") == true
                ? null : after["positionOffset"] as JArray;
            after["positionOffset"] = new JArray((offset?.ElementAtOrDefault(0)?.Value<double?>() ?? 0) + dx,
                (offset?.ElementAtOrDefault(1)?.Value<double?>() ?? 0) + dy);
            after["justThisTile"] = justThis;
            if (after["disabled"] is JObject disabled) disabled["positionOffset"] = false;
            changes.Add((item, before, after));
        }

        static void Replace(JObject target, JObject value)
        {
            target.RemoveAll();
            foreach (JProperty property in value.Properties()) target.Add(property.Name, property.Value.DeepClone());
        }
    }

    public bool CanRotateExit(int floor) => floor >= 0 && floor < LastFloor && _angles[floor]!.Value<double>() != 999;

    public void RotateExit(int floor, double angle)
    {
        if (!CanRotateExit(floor) || !double.IsFinite(angle)) return;
        TransformTiles(floor + 1, floor + 1, _ => angle);
    }

    private (JObject Item, int Floor)[] AnchorsAfter(int floor) => _actions.OfType<JObject>()
        .Concat(_decorations.OfType<JObject>()).Select(item => (Item: item, Floor: Floor(item)))
        .Where(item => item.Floor > floor).ToArray();

    private static void MoveAnchors((JObject Item, int Floor)[] anchors, int offset)
    {
        foreach (var (item, floor) in anchors)
            item["floor"] = floor + offset;
    }

    private void IndexEvents()
    {
        _eventsByFloor.Clear();
        foreach (JObject item in _actions.OfType<JObject>())
        {
            int floor = Floor(item);
            if (!_eventsByFloor.TryGetValue(floor, out List<JObject>? events))
                _eventsByFloor[floor] = events = [];
            events.Add(item);
        }
    }

}

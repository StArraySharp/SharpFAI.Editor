using Newtonsoft.Json;
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
        object? Group = null, bool SettingsOnly = false, bool EditorOnly = false,
        ValueChange? Value = null, TileInsertion? Insertion = null,
        LevelRenderer.EditorEventAppend? EventAppend = null, bool DecorationList = false);
    private sealed record ValueChange(JObject Target, string Name, JToken? Before, JToken? After);
    private sealed record TileInsertion(int Floor, double[] Angles, bool MovesEvents);
    private readonly record struct PlaybackChange(int Version, TileInsertion? Insertion,
        LevelRenderer.EditorEventAppend? EventAppend);
    private int _lastChartChange;
    private int _lastNonInsertionChange;
    private int _lastNonAppendChange;
    private int _lastUnpatchableChange;
    private readonly List<PlaybackChange> _playbackPatches = [];
    private FloorAnchorIndex? _floorNumbers;
    private TileReferenceIndex? _tileReferences;
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
        _floorNumbers = IndexFloorNumbers();
        _tileReferences = IndexTileReferences();
    }

    public string? Path { get; private set; }
    public bool Modified => _savedRevision != _revision;
    public bool CanUndo => _revision > 0;
    public bool CanRedo => _revision < _edits.Count;
    public int ChangeVersion { get; private set; }
    public int PlaybackVersion { get; private set; }
    public int DecorationListVersion { get; private set; }
    public JObject Settings { get; }
    public EditorTimeSignatures TimeSignatures { get; private set; }
    public JArray Decorations => _decorations;
    public int LastFloor => _angles.Count;
    public bool IsLargeChart => LastFloor >= 10_000 || _actions.Count + _decorations.Count >= 10_000;
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
        JToken? after = value?.DeepClone();
        Apply(new(() => Write(after), () => Write(before), name == "floor" && ReferenceEquals(target.Parent, _actions),
            isEffect ? target : null, group, SettingsOnly: ReferenceEquals(target, Settings),
            Value: new(target, name, before, after), DecorationList: ReferenceEquals(target.Parent, _decorations)));

        void Write(JToken? token)
        {
            if (name is "startTile" or "endTile" || name == "relativeTo" && Type(target) == "PositionTrack" || name == "eventType")
                _tileReferences = null;
            if (token is null)
                target.Remove(name);
            else
                target[name] = token.DeepClone();
        }
    }

    public bool CanInsertTiles(int floor, ReadOnlySpan<double> angles) =>
        floor >= 0 && floor <= LastFloor && !angles.IsEmpty && (floor != 0 || angles[0] != 999);

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
        InsertTiles(after, clipboard.Angles, clipboard.Actions, includeDecorations ? clipboard.Decorations : [], clipboard);
    }

    private void InsertTiles(int floor, double[] angles, JObject[] actions, JObject[] decorations, TileClipboard? clipboard = null)
    {
        if (!CanInsertTiles(floor, angles)) return;
        int oldLast = LastFloor;
        var references = ReferenceChanges(oldLast, oldLast + angles.Length,
            target => target > floor ? target + angles.Length : target, insertionFloor: floor);
        var anchors = _floorNumbers ??= IndexFloorNumbers();
        int[] movedFloors = anchors.After(floor);
        JValue[] values = angles.Select(angle => new JValue(angle)).ToArray();
        var events = actions.Select(AtDestination).ToArray();
        var objects = decorations.Select(AtDestination).ToArray();
        Apply(new(
            () =>
            {
                InsertAngles(_angles, floor, values);
                ShiftFloorAnchors(movedFloors, values.Length);
                WriteReferences(references, true);
                _tileReferences?.RecordReferenceMoves(references);
                foreach (var item in events) AddAction(item);
                foreach (var item in objects) _decorations.Add(item);
            },
            () =>
            {
                for (int i = values.Length - 1; i >= 0; i--) _angles.RemoveAt(floor + i);
                foreach (var item in events) RemoveAction(item);
                foreach (var item in objects) item.Remove();
                ShiftFloorAnchors(movedFloors.Select(value => value + values.Length).ToArray(), -values.Length);
                WriteReferences(references, false);
            },
            false, Insertion: clipboard is null
                ? new(floor, (double[])angles.Clone(), movedFloors.Length != 0 || references.Length != 0) : null,
            DecorationList: movedFloors.Length != 0 || decorations.Length != 0));

        JObject AtDestination(JObject item)
        {
            JObject copy = (JObject)item.DeepClone();
            copy["floor"] = floor + 1 + Floor(item);
            if (clipboard is not null)
                foreach (string key in TileReferenceKeys(copy))
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
            }, true, DecorationList: true));
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
            () => { foreach (var item in copies) item.Remove(); }, !decoration, DecorationList: decoration));
    }

    public bool Add(JObject value, bool decoration)
    {
        if (!decoration && !CanAddEvent(Floor(value), Type(value)))
            return false;
        if (value.Parent is not null)
            value = (JObject)value.DeepClone();
        JArray list = decoration ? _decorations : _actions;
        var append = new LevelRenderer.EditorEventAppend(list.Count, decoration, (JObject)value.DeepClone());
        Apply(new(
            () =>
            {
                if (decoration) list.Add(value);
                else AddAction(value);
                if (_floorNumbers is { } numbers) IndexFloorNumber(numbers, value, Floor(value));
                _tileReferences?.AddRange(ReadTileReferences([value]));
            },
            () =>
            {
                if (decoration) value.Remove();
                else RemoveAction(value);
                if (_floorNumbers is { } numbers && value["floor"] is JValue number &&
                    numbers.TryGetValue(Floor(value), out var group))
                {
                    int index = group.FindLastIndex(candidate => ReferenceEquals(candidate, number));
                    if (index >= 0) group.RemoveAt(index);
                    if (group.Count == 0) numbers.Remove(Floor(value));
                }
                _tileReferences?.Remove(value);
            },
            false, EventAppend: append, DecorationList: decoration));
        return true;
    }

    public void Remove(JObject value)
    {
        if (value.Parent is not JArray list)
            return;
        int index = list.IndexOf(value);
        Apply(new(value.Remove, () => list.Insert(index, value), list == _actions, DecorationList: list == _decorations));
    }

    public void MoveDecoration(JObject value, JObject target)
    {
        int from = _decorations.IndexOf(value);
        int to = _decorations.IndexOf(target);
        if (from < 0 || to < 0 || from == to)
            return;
        Apply(new(
            () => { value.Remove(); _decorations.Insert(to, value); },
            () => { value.Remove(); _decorations.Insert(from, value); }, false, DecorationList: true));
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
            !decoration, DecorationList: decoration));
    }

    public void PasteDecorations(IEnumerable<JObject> selection)
    {
        JObject[] copies = selection.Select(item => (JObject)item.DeepClone()).ToArray();
        if (copies.Length != 0)
            Apply(new(
                () => { foreach (var item in copies) _decorations.Add(item); },
                () => { foreach (var item in copies) item.Remove(); }, false, DecorationList: true));
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
        Refresh(edit, undo: true);
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
        string sourceDirectory = System.IO.Path.GetDirectoryName(PlaybackPath)!;
        string destinationDirectory = System.IO.Path.GetDirectoryName(destination)!;
        if (string.Equals(sourceDirectory, destinationDirectory, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            EditorFileStorage.WriteChart(destination, _level.root, overwrite: true);
        else
            EditorFileStorage.SaveChartWithAssets(destination, _level, sourceDirectory);
        Path = destination;
        _savedRevision = _revision;
    }

    internal IEnumerable<(string Text, bool Last)> BackupChunks()
    {
        const int ChunkSize = 32 * 1024;
        using var text = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        using var json = new JsonTextWriter(text);
        var buffer = text.GetStringBuilder();
        json.WriteStartObject();
        foreach (var property in _level.root.Properties())
        {
            json.WritePropertyName(property.Name);
            if (property.Value is JArray items)
            {
                json.WriteStartArray();
                foreach (JToken item in items)
                {
                    item.WriteTo(json);
                    if (buffer.Length >= ChunkSize) yield return (TakeChunk(), false);
                }
                json.WriteEndArray();
            }
            else property.Value.WriteTo(json);
            if (buffer.Length >= ChunkSize) yield return (TakeChunk(), false);
        }
        json.WriteEndObject();
        yield return (TakeChunk(), true);

        string TakeChunk()
        {
            string result = buffer.ToString();
            buffer.Clear();
            return result;
        }
    }

    internal bool HasChangesToBackUp()
    {
        if (!Modified) return false;
        if (_savedRevision < 0) return true;
        var values = new Dictionary<(JObject Target, string Name), (JToken? Before, JToken? After)>();
        for (int i = Math.Min(_revision, _savedRevision); i < Math.Max(_revision, _savedRevision); i++)
        {
            if (_edits[i].Value is not { } change) return true;
            var key = (change.Target, change.Name);
            values[key] = values.TryGetValue(key, out var existing)
                ? (existing.Before, change.After) : (change.Before, change.After);
        }
        return values.Values.Any(value => !JToken.DeepEquals(value.Before, value.After));
    }

    public string PlaybackPath => Path ?? System.IO.Path.Combine(Environment.CurrentDirectory, "untitled.adofai");
    public Level SnapshotForPlayback() => _level.CreateSnapshot(PlaybackPath);

    public bool HasOnlyEffectChangesSince(int version) => version >= _lastFullPlaybackChange;
    public bool CanReusePlaybackChart(int version) => version >= _lastChartChange;

    public bool HasOnlyTileInsertionsSince(int version) =>
        version >= _lastNonInsertionChange && version < _lastChartChange;

    public bool HasOnlyEventAppendsSince(int version) =>
        version >= _lastNonAppendChange && version < _lastChartChange;

    public LevelRenderer.EditorChartPatch? PlaybackPatchSince(int version)
    {
        if (version < _lastUnpatchableChange || CanReusePlaybackChart(version)) return null;
        var patches = _playbackPatches.Where(patch => patch.Version > version).ToArray();
        if (patches.Length == 0) return null;
        bool tileInsertions = Array.TrueForAll(patches, patch => patch.Insertion is not null);
        bool preservesBindings = tileInsertions && Array.TrueForAll(patches, patch => !patch.Insertion!.MovesEvents);
        var appends = Array.TrueForAll(patches, patch => patch.EventAppend.HasValue)
            ? Array.ConvertAll(patches, patch => patch.EventAppend!.Value) : null;
        return new(ApplyPatch, tileInsertions, preservesBindings, appends);

        void ApplyPatch(JObject root)
        {
            for (int index = 0; index < patches.Length; index++)
            {
                var patch = patches[index];
                if (patch.Insertion is not { } insert)
                {
                    var append = patch.EventAppend!.Value;
                    ((JArray)root[append.Decoration ? "decorations" : "actions"]!).Add(append.Value.DeepClone());
                    continue;
                }
                // Sequential inputs extend the same run of tiles. Relocate the chart once for that run.
                var angles = new List<double>(insert.Angles);
                bool movesEvents = insert.MovesEvents;
                while (index + 1 < patches.Length && patches[index + 1].Insertion is { } next &&
                       next.Floor == insert.Floor + angles.Count)
                {
                    angles.AddRange(next.Angles);
                    movesEvents |= next.MovesEvents;
                    index++;
                }
                ApplyTileInsertion(root, insert.Floor, angles, movesEvents);
            }
        }
    }

    private static void ApplyTileInsertion(JObject root, int floor, List<double> angles, bool movesEvents)
    {
        var path = (JArray)root["angleData"]!;
        if (movesEvents)
        {
            var actions = (JArray)root["actions"]!;
            var decorations = (JArray)root["decorations"]!;
            var references = ReadTileReferences(actions.OfType<JObject>().Concat(decorations.OfType<JObject>()));
            WriteReferences(ReferenceChanges(references, path.Count, path.Count + angles.Count,
                target => target > floor ? target + angles.Count : target), true);
            MoveAnchors(AnchorsAfter(actions, floor), angles.Count);
            MoveAnchors(AnchorsAfter(decorations, floor), angles.Count);
        }
        InsertAngles(path, floor, angles.Select(angle => new JValue(angle)));
    }

    private static void InsertAngles(JArray path, int floor, IEnumerable<JValue> angles)
    {
        if (floor == path.Count)
        {
            foreach (var angle in angles) path.Add(angle);
        }
        else
        {
            foreach (var angle in angles) path.Insert(floor++, angle);
        }
    }

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
        _playbackPatches.RemoveAll(patch => patch.Version <= version);
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
                SettingsOnly = previous.SettingsOnly && edit.SettingsOnly,
                Value = previous.Value is { } first && edit.Value is { } last &&
                    ReferenceEquals(first.Target, last.Target) && first.Name == last.Name
                    ? last with { Before = first.Before } : null };
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

    private void Refresh(Edit edit, bool undo = false)
    {
        ChangeVersion++;
        if (edit.DecorationList) DecorationListVersion++;
        if (edit.EditorOnly) return;
        PlaybackVersion = ChangeVersion;
        if (!edit.SettingsOnly)
        {
            _lastChartChange = ChangeVersion;
            if (undo || edit.Insertion is null)
                _lastNonInsertionChange = ChangeVersion;
            if (undo || edit.EventAppend is null)
                _lastNonAppendChange = ChangeVersion;
            if (!undo && (edit.EventAppend is not null || edit.Insertion is not null))
                _playbackPatches.Add(new(ChangeVersion, edit.Insertion, edit.EventAppend));
            else
            {
                _lastUnpatchableChange = ChangeVersion;
                _playbackPatches.Clear();
            }
        }
        if (edit.Effect is { } effect)
            _effectChanges[effect] = ChangeVersion;
        else
        {
            _lastFullPlaybackChange = ChangeVersion;
            _effectChanges.Clear();
        }
        if (edit.Events)
            IndexEvents();
        if (edit.Value is null && edit.EventAppend is null && edit.Insertion is null)
        {
            _tileReferences = null;
            _floorNumbers = null;
        }
        else if (edit.Value?.Name == "floor")
            _floorNumbers = null;
    }

    private readonly record struct TileReference(JObject Owner, string Key, int Before, int After);

    private TileReference[] ReferenceChanges(int oldLast, int newLast, Func<int, int> relocate, int? insertionFloor = null) =>
        ReferenceChanges((_tileReferences ??= IndexTileReferences()).Candidates(insertionFloor), oldLast, newLast, relocate);

    private TileReferenceIndex IndexTileReferences()
    {
        var index = new TileReferenceIndex();
        index.AddRange(ReadTileReferences(_actions.OfType<JObject>().Concat(_decorations.OfType<JObject>())));
        return index;
    }

    private sealed class TileReferenceIndex
    {
        private readonly List<(JObject Owner, string Key)> _start = [];
        private readonly List<(JObject Owner, string Key)> _end = [];
        // A conservative bound stays valid if an edit is undone or a reference is removed.
        private int _maxStart = int.MinValue;

        public IEnumerable<(JObject Owner, string Key)> Candidates(int? floor) =>
            floor >= _maxStart ? _end : _end.Concat(_start);

        public void AddRange(IEnumerable<(JObject Owner, string Key)> references)
        {
            foreach (var (owner, key) in references)
            {
                var value = (JArray)owner[key]!;
                if (value[1]!.Value<string>() == "End")
                    _end.Add((owner, key));
                else
                {
                    _start.Add((owner, key));
                    int floor = (value[0] as JValue)?.Value switch
                    {
                        int number => number,
                        long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
                        _ => int.MaxValue,
                    };
                    _maxStart = Math.Max(_maxStart, floor);
                }
            }
        }

        public void Remove(JObject owner)
        {
            _start.RemoveAll(reference => ReferenceEquals(reference.Owner, owner));
            _end.RemoveAll(reference => ReferenceEquals(reference.Owner, owner));
        }

        public void RecordReferenceMoves(TileReference[] moves)
        {
            foreach (var move in moves)
                if (move.After > _maxStart && ((JArray)move.Owner[move.Key]!)[1]!.Value<string>() == "Start")
                    _maxStart = move.After;
        }
    }

    private static IEnumerable<(JObject Owner, string Key)> ReadTileReferences(IEnumerable<JObject> actions)
    {
        foreach (JObject item in actions)
        {
            if (IsAbsoluteReference(item["startTile"])) yield return (item, "startTile");
            if (IsAbsoluteReference(item["endTile"])) yield return (item, "endTile");
            if (IsAbsoluteReference(item["relativeTo"]) && Type(item) == "PositionTrack") yield return (item, "relativeTo");
        }
    }

    private static string[] TileReferenceKeys(JObject item) => Type(item) == "PositionTrack"
        ? ["startTile", "endTile", "relativeTo"] : ["startTile", "endTile"];

    private static bool IsAbsoluteReference(JToken? token) =>
        token is JArray { Count: >= 2 } value && value[1] is JValue { Value: "Start" or "End" };

    private static TileReference[] ReferenceChanges(IEnumerable<(JObject Owner, string Key)> references,
        int oldLast, int newLast, Func<int, int> relocate)
    {
        var result = new List<TileReference>();
        foreach (var (owner, key) in references)
        {
            var value = (JArray)owner[key]!;
            int before = value[0]!.Value<int>();
            bool fromEnd = value[1]!.Value<string>() == "End";
            int after = relocate(before + (fromEnd ? oldLast : 0)) - (fromEnd ? newLast : 0);
            if (before != after) result.Add(new(owner, key, before, after));
        }
        return result.ToArray();
    }

    private static void WriteReferences(TileReference[] references, bool redo)
    {
        foreach (var item in references)
            ((JArray)item.Owner[item.Key]!)[0] = redo ? item.After : item.Before;
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

    public bool IsMidspinAt(int floor) => floor >= 0 && floor < LastFloor && _angles[floor]!.Value<double>() == 999;

    public bool IsFreeAngleBacktrack(int floor, double angle)
    {
        if (floor <= 0 || floor > LastFloor || IsMidspinAt(floor))
            return false;
        double backwards = IncomingAngle(floor) + 180;
        return MatchesBackwards(angle) && (floor == LastFloor || !MatchesBackwards(_angles[floor]!.Value<double>()));

        bool MatchesBackwards(double direction) => Math.Abs(Math.IEEERemainder(direction - backwards, 360)) < .0001;
    }

    private (JObject Item, int Floor)[] AnchorsAfter(int floor) => _actions.OfType<JObject>()
        .Concat(_decorations.OfType<JObject>()).Select(item => (Item: item, Floor: Floor(item)))
        .Where(item => item.Floor > floor).ToArray();

    private static IEnumerable<(JObject Item, int Floor)> AnchorsAfter(JArray items, int floor) =>
        items.OfType<JObject>().Select(item => (Item: item, Floor: Floor(item))).Where(item => item.Floor > floor);

    private static void MoveAnchors(IEnumerable<(JObject Item, int Floor)> anchors, int offset)
    {
        foreach (var (item, floor) in anchors)
            if (item["floor"] is JValue { Type: JTokenType.Integer } number)
                number.Value = floor + offset;
            else
                item["floor"] = floor + offset;
    }

    private FloorAnchorIndex IndexFloorNumbers()
    {
        var numbers = new FloorAnchorIndex();
        foreach (var (floor, items) in _eventsByFloor)
            foreach (var item in items)
                IndexFloorNumber(numbers, item, floor);
        foreach (var item in _decorations.OfType<JObject>())
            IndexFloorNumber(numbers, item, Floor(item));
        return numbers;
    }

    private static void IndexFloorNumber(FloorAnchorIndex index, JObject item, int floor)
    {
        if (item["floor"] is not JValue number)
            return;
        if (!index.TryGetValue(floor, out var group))
            index.Add(floor, group = []);
        group.Add(number);
    }

    private void ShiftFloorAnchors(int[] floors, int offset)
    {
        if (floors.Length == 0) return;
        var index = _floorNumbers ??= IndexFloorNumbers();
        // Move from the far end so a destination never overwrites a bucket still to be moved.
        int indexInFloors = offset > 0 ? floors.Length - 1 : 0;
        int step = offset > 0 ? -1 : 1;
        for (; (uint)indexInFloors < (uint)floors.Length; indexInFloors += step)
        {
            int previous = floors[indexInFloors];
            int floor = previous + offset;
            index.TryGetValue(previous, out var numbers);
            index.Remove(previous);
            index.Add(floor, numbers!);
            object value = floor;
            foreach (var number in numbers!) number.Value = value;
            if (_eventsByFloor.Remove(previous, out var events)) _eventsByFloor.Add(floor, events);
        }
    }

    private sealed class FloorAnchorIndex
    {
        private readonly Dictionary<int, List<JValue>> _numbers = [];
        private readonly SortedSet<int> _floors = [];

        public bool TryGetValue(int floor, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out List<JValue>? group) =>
            _numbers.TryGetValue(floor, out group);

        public void Add(int floor, List<JValue> group)
        {
            _numbers.Add(floor, group);
            _floors.Add(floor);
        }

        public void Remove(int floor)
        {
            _numbers.Remove(floor);
            _floors.Remove(floor);
        }

        public int[] After(int floor) =>
            _floors.Count == 0 || _floors.Max <= floor ? [] : _floors.GetViewBetween(floor + 1, int.MaxValue).ToArray();
    }

    private void AddAction(JObject item)
    {
        _actions.Add(item);
        IndexAction(item);
    }

    private void RemoveAction(JObject item)
    {
        item.Remove();
        int floor = Floor(item);
        var events = _eventsByFloor[floor];
        events.Remove(item);
        if (events.Count == 0) _eventsByFloor.Remove(floor);
    }

    private void IndexAction(JObject item)
    {
        int floor = Floor(item);
        if (!_eventsByFloor.TryGetValue(floor, out List<JObject>? events))
            _eventsByFloor[floor] = events = [];
        events.Add(item);
    }

    private void IndexEvents()
    {
        _eventsByFloor.Clear();
        foreach (JObject item in _actions.OfType<JObject>())
            IndexAction(item);
    }

}

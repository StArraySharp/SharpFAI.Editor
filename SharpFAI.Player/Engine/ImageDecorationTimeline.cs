using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using SharpFAI.Framework;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Player.Engine.Play;
using SharpFAI.Player.Engine.Filters;
using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

/// <summary>Decoration masking kinds, matching the game's <c>MaskingType</c>.</summary>
internal enum DecorationMasking
{
    None = 0,
    Mask = 1,
    VisibleInsideMask = 2,
    VisibleOutsideMask = 3,
}

/// <summary>Decoration blend modes, matching the game's <c>DecorationBlendMode</c>.</summary>
internal enum DecorationBlendMode
{
    None = 0,
    Screen = 1,
    LinearDodge = 2,
    Overlay = 3,
    SoftLight = 4,
    Difference = 5,
    Multiply = 6,
}

internal readonly record struct DecorationMaskRange(bool Enabled, int? FrontDepth, int? BackDepth)
{
    public DecorationMaskRange WithChanges(bool? enabled, int? front, int? back)
    {
        var range = this with { Enabled = enabled ?? Enabled };
        if (!front.HasValue && !back.HasValue)
            return range;

        // A back-only update reads the legacy front order as -order - 1.
        int first = front ?? (FrontDepth ?? 0) - 2;
        int last = back ?? BackDepth ?? -1;
        return new(range.Enabled, Math.Min(first, last), Math.Max(first, last));
    }

    // The Floor sorting layer sits between Bg at depth 0 and Default at depth -1.
    public static long DepthKey(int depth, bool syncFloorDepth) => syncFloorDepth ? -1 : 2L * depth;

    public bool Includes(long depthKey) => !Enabled ||
        (FrontDepth is { } front && BackDepth is { } back && 2L * front <= depthKey && depthKey <= 2L * back);
}

internal sealed class ImageDecorationTimeline
{
    private const string DefaultImage = "\0default-decoration";
    private static readonly Lazy<ImageDecoder.RgbaImage> DefaultSprite = new(() =>
    {
        var pixels = ImageDecoder.Decode(EmbeddedAssets.Read("DefaultNullDecoration.png") ??
            throw new InvalidOperationException("Missing default decoration sprite."));
        ImageDecoder.FlipVertically(pixels.Pixels, pixels.Width, pixels.Height);
        return pixels;
    });

    internal readonly record struct Pose(string Image, Vector2 Position, Vector2 Pivot, Vector2 Scale, Vector2 TextureScale,
        Vector2 Parallax, Vector2 ParallaxOffset, Vector4 Tint, float Rotation, float Opacity, int Depth, bool Visible,
        bool Smoothing, string RelativeTo, DecorationBlendMode Blend = DecorationBlendMode.None,
        bool SyncFloorDepth = false, DecorationMasking Masking = DecorationMasking.None,
        string MaskingTarget = "NO TAG", DecorationMaskRange MaskRange = default);

    internal readonly record struct Axes(float? X, float? Y)
    {
        public Axes(JArray? values) : this(Number(values, 0), Number(values, 1)) { }
        public Vector2? Read(Vector2 fallback) => X.HasValue || Y.HasValue
            ? new Vector2(X ?? fallback.X, Y ?? fallback.Y) : null;
    }

    // Every decoration matched by one scheduled action shares its decoded fields and easing.
    internal sealed class Motion(MoveImageDecoration item, TrackTiming timing, double beat = 0)
    {
        public TrackTiming Timing { get; private set; } = timing;
        public int SourceIndex { get; } = item.SourceIndex;
        public double Beat { get; } = beat;
        public readonly Axes Position = new(item.Enabled("positionOffset", item.Position));
        public readonly Axes Pivot = new(item.Enabled("pivotOffset", item.Pivot));
        public readonly Axes Scale = new(item.Enabled("scale", item.Scale));
        public readonly Axes Parallax = new(item.Enabled("parallax", item.Parallax));
        public readonly Axes ParallaxOffset = new(item.Enabled("parallaxOffset", item.ParallaxOffset));
        public readonly string? RelativeTo = item.Enabled("relativeTo", item.RelativeTo);
        public readonly string? Image = item.Enabled("decorationImage", item.ImageSpecified) ? item.Image ?? "" : null;
        public readonly Vector2? OriginalSize = Read(item.Enabled("originalSize", item.OriginalSize), Vector2.Zero);
        public readonly float? Rotation = item.Enabled("rotationOffset", item.Rotation);
        public readonly Vector4? Tint = FloorTrackStateResolver.ParseColor(item.Enabled("color", item.Color));
        public readonly bool HasColor = item.Enabled("color", item.Color) is not null;
        public readonly float? Opacity = item.Enabled("opacity", item.Opacity);
        public readonly int? Depth = item.Enabled("depth", item.Depth);
        public readonly string? MaskingType = item.Enabled("maskingType", item.MaskingType);
        public readonly string? MaskingTarget = item.Enabled("maskingTarget", item.MaskingTarget);
        public readonly bool? UseMaskingDepth = item.Enabled("useMaskingDepth", item.UseMaskingDepth);
        public readonly int? FrontDepth = item.Enabled("maskingFrontDepth", item.MaskingFrontDepth);
        public readonly int? BackDepth = item.Enabled("maskingBackDepth", item.MaskingBackDepth);
        public readonly bool? Visible = item.Enabled("visible", item.Visible);
        public readonly bool? Smoothing = item.Enabled("imageSmoothing", item.Smoothing);
        public readonly bool ChangesText = item is SetTextDecoration;
        private double _sampleTime = double.NaN;
        private float _progress;

        public float Progress(double time)
        {
            if (time != _sampleTime)
            {
                _progress = Timing.Progress(time);
                _sampleTime = time;
            }
            return _progress;
        }

        public Motion TriggerAt(double time)
        {
            var motion = (Motion)MemberwiseClone();
            motion.Timing = Timing with { Start = time };
            motion._sampleTime = double.NaN;
            return motion;
        }
    }

    internal sealed class Item(AddImageDecoration source, Pose initial, Vector2 tile, int index, float tileSize = 150f)
    {
        private readonly record struct Change(Motion Motion, double PrefixEnd, Pose From, Pose Target,
            bool HasPositionX, bool HasPositionY, bool HasPivotX, bool HasPivotY, bool HasScaleX, bool HasScaleY,
            bool HasParallaxX, bool HasParallaxY, bool HasParallaxOffsetX, bool HasParallaxOffsetY,
            bool HasRotation, bool HasColor, bool HasOpacity, bool HasVisible, bool HasSmoothing,
            bool HasRelativeTo, bool HasImage, bool HasDepth);
        private readonly List<Change> _changes = [];
        private int _editIndex = -1;
        private int ChangeCount => _editIndex >= 0 ? _editIndex : _changes.Count;
        private Change[]? _authoredChanges;
        private Pose _cachedPose;
        private double _cachedAt = double.NaN;
        private double _cachedUntil;

        public Pose Initial { get; private set; } = initial;
        public int SourceIndex => source.SourceIndex;
        public int DecorationIndex => source.DecorationIndex;
        public int Index { get; } = index;
        public int Floor => source.Floor;
        public bool StickToFloor => source.StickToFloor == true;
        public bool LockRotation => source.LockRotation == true;
        public bool LockScale => source.LockScale == true;
        public float ScaleMultiplier => source.ScaleMultiplier ?? 1f;
        public bool IsText => source is AddTextDecoration;
        public string? Font => (source as AddTextDecoration)?.Font;
        public Vector2 Tile { get; private set; } = tile;
        public string[] Tags { get; } = Tags(source.Tag).Distinct(StringComparer.Ordinal).ToArray();
        public DecorationHitbox? Hitbox { get; } = DecorationHitbox.From(source);
        public FilterTimeline? Filters { get; set; }
        public int TimingVersion { get; private set; }

        public void Add(MoveImageDecoration item, TrackTiming timing, IReadOnlyDictionary<string, ImageDecoder.RgbaImage> images)
            => Add(new Motion(item, timing), images);

        public void Add(Motion move, IReadOnlyDictionary<string, ImageDecoder.RgbaImage> images)
        {
            // The game completes the previous tween when a new event takes over a channel.
            int count = ChangeCount;
            Pose current = count == 0 ? Initial : _changes[count - 1].Target;
            string? placement = move.RelativeTo;
            string targetPlacement = placement is null or "LastPosition" ? current.RelativeTo : placement;
            Vector2 anchor = placement == "LastPosition" && (move.Position.X.HasValue || move.Position.Y.HasValue)
                ? At(move.Timing.Start).Position : Initial.Position;
            float positionUnits = targetPlacement is "Camera" or "CameraAspect" ? tileSize / 100f : 1f;
            Vector2? scale = move.Scale.Read(current.Scale * 100f);
            bool hasImage = (!IsText || move.ChangesText) && move.Image is not null;
            string image = hasImage ? move.Image! : current.Image;
            var target = current with
            {
                Position = new Vector2(move.Position.X is { } x ? anchor.X + x * positionUnits : current.Position.X,
                    move.Position.Y is { } y ? anchor.Y + y * positionUnits : current.Position.Y),
                Pivot = move.Pivot.Read(current.Pivot) ?? current.Pivot,
                Rotation = move.Rotation is { } rotation ? Initial.Rotation + rotation : current.Rotation,
                Scale = scale is { } percent ? percent / 100f : current.Scale,
                Tint = move.Tint ?? current.Tint,
                Opacity = move.Opacity is { } opacity ? Math.Clamp(opacity / 100f, 0f, 1f) : current.Opacity,
                Depth = move.Depth ?? current.Depth,
                Masking = !IsText && move.MaskingType is { } masking ? ParseMasking(masking) : current.Masking,
                MaskingTarget = !IsText && move.MaskingTarget is { } maskTag ? MaskTag(maskTag) : current.MaskingTarget,
                MaskRange = current.MaskRange.WithChanges(move.UseMaskingDepth, move.FrontDepth, move.BackDepth),
                TextureScale = IsText ? current.TextureScale :
                    ResolveTextureScale(images, image, move.OriginalSize, current.TextureScale),
                Visible = move.Visible ?? current.Visible,
                Smoothing = move.Smoothing ?? current.Smoothing,
                Parallax = move.Parallax.Read(current.Parallax) ?? current.Parallax,
                ParallaxOffset = move.ParallaxOffset.Read(current.ParallaxOffset) ?? current.ParallaxOffset,
                RelativeTo = targetPlacement,
                Image = image,
            };
            double end = move.Timing.Start + move.Timing.Duration;
            double prefixEnd = count == 0 ? end : Math.Max(end, _changes[count - 1].PrefixEnd);
            var change = new Change(move, prefixEnd, current, target,
                move.Position.X.HasValue, move.Position.Y.HasValue,
                move.Pivot.X.HasValue, move.Pivot.Y.HasValue,
                move.Scale.X.HasValue, move.Scale.Y.HasValue,
                move.Parallax.X.HasValue, move.Parallax.Y.HasValue,
                move.ParallaxOffset.X.HasValue, move.ParallaxOffset.Y.HasValue,
                move.Rotation.HasValue,
                move.HasColor,
                move.Opacity.HasValue,
                move.Visible.HasValue,
                move.Smoothing.HasValue,
                placement is not (null or "LastPosition"),
                hasImage,
                move.Depth.HasValue);
            if (_editIndex >= 0)
                _changes[_editIndex++] = change;
            else
                _changes.Add(change);
            _cachedAt = double.NaN;
        }

        public void InsertTriggered(Motion motion, IReadOnlyDictionary<string, ImageDecoder.RgbaImage> images)
        {
            TimingVersion++;
            _authoredChanges ??= _changes.ToArray();
            int insert = _changes.Count;
            while (insert > 0 && _changes[insert - 1].Motion.Timing.Start > motion.Timing.Start)
                insert--;
            if (insert == _changes.Count)
            {
                Add(motion, images);
                return;
            }
            var subsequent = _changes.GetRange(insert, _changes.Count - insert);
            _changes.RemoveRange(insert, subsequent.Count);
            _cachedAt = double.NaN;
            Add(motion, images);
            foreach (var next in subsequent)
                Add(next.Motion, images);
        }

        public void ResetTriggers()
        {
            if (_authoredChanges is not { } authored)
                return;
            _changes.Clear();
            _changes.AddRange(authored);
            _authoredChanges = null;
            _cachedAt = double.NaN;
            TimingVersion++;
        }

        public void EditInitial(AddImageDecoration replacement, IReadOnlyDictionary<string, ImageDecoder.RgbaImage> images)
        {
            Motion[] motions = _changes.Select(change => change.Motion).ToArray();
            source = replacement;
            Initial = InitialPose(replacement, Initial.Image, images, tileSize);
            Tile = Read(replacement.Tile, Vector2.One) ?? Vector2.One;
            _changes.Clear();
            _cachedAt = double.NaN;
            TimingVersion++;
            foreach (var motion in motions)
                Add(motion, images);
        }

        public bool EditMotions(IReadOnlyDictionary<int, MoveImageDecoration> replacements,
            IReadOnlyDictionary<string, ImageDecoder.RgbaImage> images, Dictionary<Motion, Motion> compiled)
        {
            int first = _changes.FindIndex(change => replacements.ContainsKey(change.Motion.SourceIndex));
            if (first < 0)
                return false;
            int lastReplacement = _changes.FindLastIndex(change => replacements.ContainsKey(change.Motion.SourceIndex));
            _cachedAt = double.NaN;
            TimingVersion++;
            _editIndex = first;
            try
            {
                for (int index = first; index < _changes.Count; index++)
                {
                    var change = _changes[index];
                    var previous = change.Motion;
                    if (replacements.TryGetValue(previous.SourceIndex, out var item))
                    {
                        if (!compiled.TryGetValue(previous, out var motion))
                        {
                            var timing = previous.Timing with
                            {
                                Duration = Math.Max(0, item.Enabled("duration", item.Duration) ?? 0) * previous.Beat,
                                Ease = item.Enabled("ease", item.Ease) ?? SharpFAI.Serialization.EventEnums.Ease.Linear,
                            };
                            compiled.Add(previous, motion = new Motion(item, timing, previous.Beat));
                        }
                        Add(motion, images);
                    }
                    else
                        Add(previous, images);
                    // Reuse the untouched suffix after the inherited state converges.
                    if (index >= lastReplacement && _changes[index] == change)
                        break;
                }
            }
            finally
            {
                _editIndex = -1;
                _cachedAt = double.NaN;
            }
            return true;
        }

        public Pose At(double time) => At(time, out _);

        // A deadline equal to time indicates an in-progress tween.
        internal Pose At(double time, out double next)
        {
            int count = ChangeCount;
            if (count == 0)
            {
                next = double.PositiveInfinity;
                return Initial;
            }
            double start = _changes[0].Motion.Timing.Start;
            if (time < start)
            {
                next = start;
                return Initial;
            }
            if (time >= _changes[count - 1].PrefixEnd)
            {
                next = double.PositiveInfinity;
                return _changes[count - 1].Target;
            }
            if (time == _cachedAt || (time > _cachedAt && time < _cachedUntil))
            {
                next = _cachedUntil;
                return _cachedPose;
            }

            // Skip the history whose tweens have all completed; its last target is a
            // complete pose. A prefix maximum also accounts for overlapping durations.
            int first = 0;
            int end = count;
            while (first < end)
            {
                int middle = first + (end - first) / 2;
                if (_changes[middle].PrefixEnd <= time)
                    first = middle + 1;
                else
                    end = middle;
            }

            // Evaluate each authored channel independently so a second event can change opacity
            // while an earlier position tween is still running.
            Pose result = first == 0 ? Initial : _changes[first - 1].Target;
            double until = double.PositiveInfinity;
            for (int i = first; i < count; i++)
            {
                var change = _changes[i];
                var timing = change.Motion.Timing;
                if (timing.Start > time)
                {
                    until = Math.Min(until, timing.Start);
                    break;
                }
                if (time < timing.Start + timing.Duration)
                    until = time;
                float p = change.Motion.Progress(time);
                var from = change.From;
                var to = change.Target;
                result = result with
                {
                    Position = new Vector2(
                        change.HasPositionX ? Mix(from.Position.X, to.Position.X, p) : result.Position.X,
                        change.HasPositionY ? Mix(from.Position.Y, to.Position.Y, p) : result.Position.Y),
                    Pivot = new Vector2(
                        change.HasPivotX ? Mix(from.Pivot.X, to.Pivot.X, p) : result.Pivot.X,
                        change.HasPivotY ? Mix(from.Pivot.Y, to.Pivot.Y, p) : result.Pivot.Y),
                    Scale = new Vector2(
                        change.HasScaleX ? Mix(from.Scale.X, to.Scale.X, p) : result.Scale.X,
                        change.HasScaleY ? Mix(from.Scale.Y, to.Scale.Y, p) : result.Scale.Y),
                    Parallax = new Vector2(
                        change.HasParallaxX ? Mix(from.Parallax.X, to.Parallax.X, p) : result.Parallax.X,
                        change.HasParallaxY ? Mix(from.Parallax.Y, to.Parallax.Y, p) : result.Parallax.Y),
                    ParallaxOffset = new Vector2(
                        change.HasParallaxOffsetX ? Mix(from.ParallaxOffset.X, to.ParallaxOffset.X, p) : result.ParallaxOffset.X,
                        change.HasParallaxOffsetY ? Mix(from.ParallaxOffset.Y, to.ParallaxOffset.Y, p) : result.ParallaxOffset.Y),
                    Rotation = change.HasRotation ? Mix(from.Rotation, to.Rotation, p) : result.Rotation,
                    Tint = change.HasColor ? Vector4.Lerp(from.Tint, to.Tint, p) : result.Tint,
                    Opacity = change.HasOpacity ? Mix(from.Opacity, to.Opacity, p) : result.Opacity,
                    Depth = change.HasDepth ? to.Depth : result.Depth,
                    Masking = to.Masking,
                    MaskingTarget = to.MaskingTarget,
                    MaskRange = to.MaskRange,
                    TextureScale = to.TextureScale,
                    Visible = change.HasVisible ? to.Visible : result.Visible,
                    Smoothing = change.HasSmoothing ? to.Smoothing : result.Smoothing,
                    RelativeTo = change.HasRelativeTo ? to.RelativeTo : result.RelativeTo,
                    Image = change.HasImage ? to.Image : result.Image,
                };
            }
            _cachedAt = time;
            _cachedUntil = until;
            _cachedPose = result;
            next = until;
            return result;
        }

        private static float Mix(float from, float to, float p) => from + (to - from) * p;
    }

    public List<Item> Items { get; } = [];
    public Dictionary<string, ImageDecoder.RgbaImage> Images { get; } = new(StringComparer.Ordinal);
    public bool LegacyMasks { get; private init; }
    public HashSet<Item> ChangedItems { get; } = [];
    private Dictionary<int, HashSet<Item>>? _editorMoveTargets;
    public IReadOnlyList<Item> Hitboxes { get; private set; } = [];
    private readonly HashSet<Item> _triggeredItems = [];
    private readonly HashSet<FilterTimeline> _triggeredFilters = [];
    private readonly Dictionary<string, List<TriggerAction>> _collisionActions = new(StringComparer.Ordinal);
    private readonly Dictionary<(int Floor, int Outcome), List<TriggerAction>> _conditionalActions = [];
    private int[] _conditionFloors = [];
    private readonly record struct TriggerAction(Action<double> Apply, double Offset);

    public bool HasConditions => _conditionalActions.Count > 0;

    private static Pose InitialPose(AddImageDecoration item, string image,
        IReadOnlyDictionary<string, ImageDecoder.RgbaImage> images, float tileSize)
    {
        Vector2 parallax = Read(item.Parallax, Vector2.Zero) ?? Vector2.Zero;
        Vector2 pivot = Read(item.Pivot, Vector2.Zero) ?? Vector2.Zero;
        if (item.RelativeTo is "Camera" or "CameraAspect")
            pivot *= 100f / tileSize;
        return new Pose(image, Read(item.Position, Vector2.Zero) ?? Vector2.Zero, pivot,
            (Read(item.Scale, new Vector2(100f)) ?? new Vector2(100f)) / 100f,
            // Text raster pixels already use Player world units.
            item is AddTextDecoration ? Vector2.One :
                ResolveTextureScale(images, image, Read(item.OriginalSize, Vector2.Zero), Vector2.One),
            parallax,
            parallax == Vector2.Zero ? Vector2.Zero : Read(item.ParallaxOffset, Vector2.Zero) ?? Vector2.Zero,
            FloorTrackStateResolver.ParseColor(item.Color) ?? Vector4.One, item.Rotation ?? 0f,
            Math.Clamp((item.Opacity ?? 100f) / 100f, 0f, 1f), item.Depth ?? -1, item.Visible ?? true,
            item.Smoothing ?? true, item.RelativeTo ?? "Global", ParseBlendMode(item.BlendMode),
            item.SyncFloorDepth == true, ParseMasking(item.MaskingType), MaskTag(item.MaskingTarget),
            default(DecorationMaskRange).WithChanges(item.UseMaskingDepth, item.MaskingFrontDepth, item.MaskingBackDepth));
    }

    internal Item? EditableDecoration(int sourceIndex, bool decoration) => Items.FirstOrDefault(item =>
        item.SourceIndex == sourceIndex && (item.DecorationIndex >= 0) == decoration);

    public void EditInitial(AddImageDecoration replacement, bool decoration)
    {
        if (EditableDecoration(replacement.SourceIndex, decoration) is not { } item)
            return;
        item.EditInitial(replacement, Images);
        ChangedItems.Add(item);
    }

    public bool CanEditMotion(MoveImageDecoration before, MoveImageDecoration after) =>
        _editorMoveTargets?.ContainsKey(before.SourceIndex) == true &&
        before.IsActive == after.IsActive && before.Floor == after.Floor &&
        before.Enabled("tag", before.Tag) == after.Enabled("tag", after.Tag) &&
        before.Enabled("eventTag", before.EventTag) == after.Enabled("eventTag", after.EventTag) &&
        before.Enabled("angleOffset", before.AngleOffset) == after.Enabled("angleOffset", after.AngleOffset) &&
        (after.Enabled("decorationImage", after.Image) is not { Length: > 0 } image || Images.ContainsKey(image));

    public void EditMotions(IReadOnlyDictionary<int, MoveImageDecoration> replacements)
    {
        HashSet<Item> affected = [];
        foreach (int index in replacements.Keys)
            affected.UnionWith(_editorMoveTargets![index]);
        Dictionary<Motion, Motion> compiled = [];
        foreach (var item in affected)
            if (item.EditMotions(replacements, Images, compiled))
                ChangedItems.Add(item);
    }

    public void TriggerCondition(int floor, HitMargin margin, double time, bool failed = false, bool checkpoint = false)
    {
        int condition = Array.BinarySearch(_conditionFloors, floor);
        if (condition < 0)
            condition = ~condition - 1;
        if (condition < 0)
            return;
        if (checkpoint)
        {
            for (; condition >= 0; condition--)
            {
                if (!_conditionalActions.TryGetValue((_conditionFloors[condition], 8), out var checkpointActions))
                    continue;
                Trigger(checkpointActions, time);
                break;
            }
            return;
        }
        int outcome = margin switch
        {
            HitMargin.Perfect or HitMargin.Auto => 0,
            HitMargin.EarlyPerfect => 1,
            HitMargin.LatePerfect => 2,
            HitMargin.VeryEarly => 3,
            HitMargin.VeryLate => 4,
            HitMargin.TooEarly => 5,
            HitMargin.TooLate or HitMargin.Miss => 6,
            _ => -1,
        };
        if (_conditionalActions.TryGetValue((_conditionFloors[condition], outcome), out var actions))
            Trigger(actions, time);
        if (failed && _conditionalActions.TryGetValue((_conditionFloors[condition], 7), out var loss))
            Trigger(loss, time);
    }

    public void TriggerCollision(string[] tags, double time)
    {
        HashSet<TriggerAction>? seen = tags.Length > 1 ? [] : null;
        foreach (string tag in tags)
            if (_collisionActions.TryGetValue(tag, out var actions))
                Trigger(actions, time, seen);
    }

    private static void Trigger(List<TriggerAction> actions, double time, HashSet<TriggerAction>? seen = null)
    {
        foreach (var action in actions)
            if (seen is null || seen.Add(action))
                action.Apply(time + action.Offset);
    }

    public void ResetTriggers()
    {
        foreach (var item in _triggeredItems)
        {
            item.ResetTriggers();
            ChangedItems.Add(item);
        }
        _triggeredItems.Clear();
        foreach (var filter in _triggeredFilters)
            filter.ResetTriggers();
        _triggeredFilters.Clear();
    }

    public void ReleaseFilters()
    {
        foreach (var item in Items)
            item.Filters?.Dispose();
    }

    public static ImageDecorationTimeline? Build(IReadOnlyList<PlaybackEvent> events, IReadOnlyList<Floor> floors,
        double[] times, double[]? bpms, string? directory, List<string> warnings, CancellationToken token,
        bool legacyMasks = false, float tileSize = 150f, bool editable = false,
        IReadOnlyDictionary<string, ImageDecoder.RgbaImage>? cachedImages = null)
    {
        if (!events.Any(item => item is AddImageDecoration))
            return null;

        var timeline = new ImageDecorationTimeline
        {
            LegacyMasks = legacyMasks,
            _editorMoveTargets = editable ? [] : null,
        };
        var tagged = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
        var textImages = new Dictionary<(string? Font, string Value), string>();
        var pendingTexts = new Dictionary<(string? Font, string Value), string>();
        // Declared up front so the loops below and the local functions can share it.
        var missingImages = new HashSet<string>(StringComparer.Ordinal);

        // Decode every referenced sprite first and in parallel: building the poses needs the pixel
        // sizes, and PNG decoding of large artwork dominates load time on decoration-heavy charts.
        // MoveDecorations can swap in artwork the AddDecoration pass never mentions, so collect
        // those too before the worker pool starts.
        List<string> pendingImages = [];
        var seenImages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in events.OfType<AddImageDecoration>())
        {
            if (!item.IsActive)
                continue;
            if (item is AddTextDecoration text)
                QueueText(text.Font, text.Text);
            else if (item.Image is { Length: > 0 } image)
                QueueImage(image);
        }
        foreach (var item in events.OfType<MoveImageDecoration>())
        {
            if (item.Enabled("decorationImage", item.Image) is { Length: > 0 } image)
                QueueImage(image);
        }
        DecodePendingImages(pendingImages, directory, timeline.Images, warnings, token);
        RasterizePendingTexts(pendingTexts, timeline.Images, cachedImages, warnings, token);

        foreach (var item in events.OfType<AddImageDecoration>())
        {
            token.ThrowIfCancellationRequested();
            if (!item.IsActive)
                continue;
            string image = (item is AddTextDecoration text ? LoadText(text.Font, text.Text) : item.Image) ?? "";
            if (image.Length == 0 && item is not AddTextDecoration)
            {
                image = DefaultImage;
                if (!timeline.Images.ContainsKey(image))
                    timeline.Images.Add(image, DefaultSprite.Value);
            }
            var pose = InitialPose(item, image, timeline.Images, tileSize);
            var decoration = new Item(item, pose, Read(item.Tile, Vector2.One) ?? Vector2.One,
                timeline.Items.Count, tileSize);
            timeline.Items.Add(decoration);
            foreach (string tag in decoration.Tags)
            {
                if (!tagged.TryGetValue(tag, out var decorations))
                    tagged.Add(tag, decorations = []);
                decorations.Add(decoration);
            }
        }
        timeline.Hitboxes = timeline.Items.Where(item => item.Hitbox is not null).ToArray();
        var collisionTags = timeline.Hitboxes.SelectMany(item => item.Hitbox!.EventTags).ToHashSet(StringComparer.Ordinal);
        var conditions = events.OfType<ConditionalDecorationEvent>().Where(item => item.IsActive)
            .GroupBy(item => item.Floor).ToDictionary(group => group.Key, group => group.Last().Tags());
        timeline._conditionFloors = conditions.Keys.Order().ToArray();
        var boundConditions = new HashSet<TimedEvent>();
        double baseBpm = floors.Count > 0 ? bpms?[0] ?? floors[0].bpm : 0;
        var moveSchedule = TimedEventSchedule.Build<MoveImageDecoration>(events, floors, times, bpms, preserveChartOrder: true);
        var matching = new HashSet<Item>();
        foreach (var entry in moveSchedule)
        {
            token.ThrowIfCancellationRequested();
            var item = entry.Event;
            var (outcomes, collision) = TriggerTags(item);
            if (outcomes.Length != 0 && !boundConditions.Add(item))
                continue;
            matching.Clear();
            foreach (string tag in Tags(item.Enabled("tag", item.Tag)))
            {
                if (tagged.TryGetValue(tag, out var decorations))
                    matching.UnionWith(decorations);
            }
            if (item is SetTextDecoration text)
            {
                foreach (var decoration in matching.Where(entry => entry.IsText))
                {
                    if (LoadText(decoration.Font, text.Text) is not { } key)
                        continue;
                    // Changing the text changes its bounds without stretching the glyphs.
                    Register(decoration, new Motion(new SetTextDecoration { Image = key }, entry.Timing));
                }
                continue;
            }

            if (HasNonText(matching) && item.Enabled("decorationImage", item.Image) is { Length: > 0 } image)
                LoadImage(image);
            if (timeline._editorMoveTargets is { } editorTargets && item.SourceIndex >= 0 &&
                outcomes.Length == 0 && collision.Length == 0)
            {
                if (!editorTargets.TryGetValue(item.SourceIndex, out var targets))
                    editorTargets.Add(item.SourceIndex, targets = []);
                targets.UnionWith(matching);
            }
            if (matching.Count == 0)
                continue;
            double bpm = bpms?[entry.Anchor] ?? floors[entry.Anchor].bpm;
            var motion = new Motion(item, entry.Timing, bpm > 1e-6 ? 60.0 / bpm : .5);
            foreach (var decoration in matching)
                Register(decoration, motion);

            void Register(Item decoration, Motion move)
            {
                if (outcomes.Length == 0 && collision.Length == 0)
                {
                    decoration.Add(move, timeline.Images);
                    return;
                }
                RegisterTrigger(item.Floor, entry.Anchor, entry.AngleOffset, outcomes, collision, start =>
                {
                    decoration.InsertTriggered(move.TriggerAt(start), timeline.Images);
                    timeline._triggeredItems.Add(decoration);
                    timeline.ChangedItems.Add(decoration);
                });
            }
        }

        if (GeneratedFilterRegistry.Factories.Count != 0 &&
            events.OfType<CameraFilterEvent>().Any(IsDecorationFilter))
        {
            Dictionary<Item, List<ScheduledEvent<CameraFilterEvent>>> byDecoration = [];
            var filterOrder = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var filter in events.OfType<CameraFilterEvent>().Where(IsDecorationFilter))
                if (filter.Filter is { Length: > 0 } name)
                    filterOrder.TryAdd(name, filterOrder.Count);
            foreach (var entry in TimedEventSchedule.Build<CameraFilterEvent>(events, floors, times, bpms,
                         preserveChartOrder: true))
            {
                if (!IsDecorationFilter(entry.Event))
                    continue;
                var (outcomes, collision) = TriggerTags(entry.Event);
                if (outcomes.Length != 0 && !boundConditions.Add(entry.Event))
                    continue;
                matching.Clear();
                foreach (string tag in Tags(entry.Event.Enabled("targetTag", entry.Event.TargetTag)))
                    if (tagged.TryGetValue(tag, out var items))
                        matching.UnionWith(items);
                foreach (var item in matching)
                {
                    if (item.IsText)
                        continue;
                    if (!byDecoration.TryGetValue(item, out var schedule))
                        byDecoration.Add(item, schedule = []);
                    if (outcomes.Length == 0 && collision.Length == 0)
                        schedule.Add(entry);
                    else
                        RegisterTrigger(entry.Event.Floor, entry.Anchor, entry.AngleOffset, outcomes, collision, start =>
                        {
                            var filters = item.Filters!;
                            filters.InsertTriggered(entry with { Timing = entry.Timing with { Start = start } });
                            timeline._triggeredFilters.Add(filters);
                        });
                }
            }
            foreach (var (item, schedule) in byDecoration)
                item.Filters = FilterTimeline.ForDecoration(schedule, filterOrder, legacyMasks);
        }

        return timeline.Items.Count == 0 ? null : timeline;

        void QueueImage(string image)
        {
            if (!seenImages.Add(image))
                return;
            if (cachedImages?.TryGetValue(image, out var pixels) == true)
                timeline.Images.Add(image, pixels);
            else
                pendingImages.Add(image);
        }

        void QueueText(string? font, JToken? tokenText)
        {
            string value = TextDecorationImage.ReadText(tokenText);
            if (value.Length == 0)
                return;
            var textKey = (font, value);
            if (textImages.ContainsKey(textKey) || pendingTexts.ContainsKey(textKey))
                return;
            pendingTexts.Add(textKey, TextImageKey(font, value));
        }

        (int[] Outcomes, string[] Collision) TriggerTags(TimedEvent item)
        {
            if (item.Enabled("eventTag", item.EventTag) is not { Length: > 0 } eventTag)
                return ([], []);
            int[] outcomes = conditions.TryGetValue(item.Floor, out var tags)
                ? Enumerable.Range(0, tags.Length).Where(index => tags[index] == eventTag).ToArray() : [];
            string[] collision = collisionTags.Count == 0 ? [] :
                Tags(eventTag).Where(collisionTags.Contains).Distinct().ToArray();
            return (outcomes, collision);
        }

        void RegisterTrigger(int floor, int anchor, double offset, int[] outcomes, string[] collision, Action<double> apply)
        {
            double targetBpm = bpms?[anchor] ?? floors[anchor].bpm;
            var action = new TriggerAction(apply,
                baseBpm > 0 ? Math.Max(0, offset * 60.0 * targetBpm / (baseBpm * baseBpm)) : 0);
            foreach (int outcome in outcomes)
            {
                var key = (floor, outcome);
                if (!timeline._conditionalActions.TryGetValue(key, out var actions))
                    timeline._conditionalActions.Add(key, actions = []);
                actions.Add(action);
            }
            foreach (string tag in collision)
            {
                if (!timeline._collisionActions.TryGetValue(tag, out var actions))
                    timeline._collisionActions.Add(tag, actions = []);
                actions.Add(action);
            }
        }

        static bool IsDecorationFilter(CameraFilterEvent item) => item.IsActive &&
            item.EventType == SharpFAI.Serialization.EventType.SetFilterAdvanced &&
            item.Enabled("targetType", item.TargetType) is "Decoration" or "1";

        static bool HasNonText(HashSet<Item> matching)
        {
            foreach (var decoration in matching)
            {
                if (!decoration.IsText)
                    return true;
            }
            return false;
        }

        string? LoadText(string? font, JToken? tokenText)
        {
            string value = TextDecorationImage.ReadText(tokenText);
            if (textImages.TryGetValue((font, value), out var key))
                return key;
            key = TextImageKey(font, value);
            if (!timeline.Images.ContainsKey(key))
                timeline.Images.Add(key, cachedImages?.TryGetValue(key, out var pixels) == true
                    ? pixels : TextDecorationImage.Rasterize(value, font));
            textImages.Add((font, value), key);
            return key;
        }

        static string TextImageKey(string? font, string value) => "\0text:" +
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes((font ?? "") + "\0" + value)));

        // The MoveDecorations pass may swap in an image the first pass never saw.
        void LoadImage(string name)
        {
            if (directory is null || timeline.Images.ContainsKey(name) || !missingImages.Add(name))
                return;
            try
            {
                string fullPath = Path.GetFullPath(Path.Combine(directory, name));
                string relative = Path.GetRelativePath(directory, fullPath);
                if (Path.IsPathRooted(relative) || relative == ".." ||
                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    return;
                var pixels = ImageDecoder.Decode(File.ReadAllBytes(fullPath));
                ImageDecoder.FlipVertically(pixels.Pixels, pixels.Width, pixels.Height);
                timeline.Images[name] = pixels;
            }            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or
                SixLabors.ImageSharp.ImageFormatException or NotSupportedException)
            {
                warnings.Add($"无法读取装饰贴图 {name}：{error.Message}");
            }
        }
    }

    /// <summary>
    /// Decode the queued decoration images in parallel. Large artwork dominates load time on
    /// decoration-heavy levels, and PNG decoding is independent per file.
    /// </summary>
    private static void RasterizePendingTexts(
        Dictionary<(string? Font, string Value), string> pending,
        Dictionary<string, ImageDecoder.RgbaImage> images,
        IReadOnlyDictionary<string, ImageDecoder.RgbaImage>? cachedImages,
        List<string> warnings,
        CancellationToken token)
    {
        if (pending.Count == 0)
            return;

        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, ImageDecoder.RgbaImage>(
            StringComparer.Ordinal);
        var failures = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(
            StringComparer.Ordinal);
        Parallel.ForEach(pending, new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
        }, entry =>
        {
            string key = entry.Value;
            if (cachedImages?.TryGetValue(key, out var cached) == true)
            {
                results[key] = cached;
                return;
            }

            try
            {
                results[key] = TextDecorationImage.Rasterize(entry.Key.Value, entry.Key.Font);
            }
            catch (Exception error) when (error is not OutOfMemoryException and not OperationCanceledException)
            {
                failures[key] = error.Message;
            }
        });

        foreach (var (key, pixels) in results)
            images[key] = pixels;
        foreach (var (key, message) in failures)
            warnings.Add($"无法栅格化文字装饰 {key}：{message}");
    }

    /// <summary>
    /// Decode the queued decoration images in parallel. Large artwork dominates load time on
    /// decoration-heavy levels, and PNG decoding is independent per file.
    /// </summary>
    private static void DecodePendingImages(List<string> pending, string? directory,
        Dictionary<string, ImageDecoder.RgbaImage> images, List<string> warnings, CancellationToken token)
    {
        if (pending.Count == 0 || string.IsNullOrEmpty(directory))
            return;

        // A single file is the common case (a MoveDecorations swap); avoid the scheduler
        // round-trip that Parallel.ForEach adds for one item.
        if (pending.Count == 1)
        {
            Decode(pending[0]);
            return;
        }
        bool Decode(string name)
        {
            try
            {
                string fullPath = Path.GetFullPath(Path.Combine(directory, name));
                string relative = Path.GetRelativePath(directory, fullPath);
                if (Path.IsPathRooted(relative) || relative == ".." ||
                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    return false;
                var pixels = ImageDecoder.Decode(File.ReadAllBytes(fullPath));
                ImageDecoder.FlipVertically(pixels.Pixels, pixels.Width, pixels.Height);
                images[name] = pixels;
                return true;
            }
            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or
                SixLabors.ImageSharp.ImageFormatException or NotSupportedException)
            {
                warnings.Add($"无法读取装饰贴图 {name}：{error.Message}");
                return false;
            }
        }

        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, ImageDecoder.RgbaImage>(
            StringComparer.Ordinal);
        var failures = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        Parallel.ForEach(pending, new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
        }, name =>
        {
            try
            {
                string fullPath = Path.GetFullPath(Path.Combine(directory, name));
                string relative = Path.GetRelativePath(directory, fullPath);
                if (Path.IsPathRooted(relative) || relative == ".." ||
                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    return;
                var pixels = ImageDecoder.Decode(File.ReadAllBytes(fullPath));
                ImageDecoder.FlipVertically(pixels.Pixels, pixels.Width, pixels.Height);
                results[name] = pixels;
            }
            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or
                SixLabors.ImageSharp.ImageFormatException or NotSupportedException)
            {
                failures[name] = error.Message;
            }
        });

        foreach (var (name, pixels) in results)
            images[name] = pixels;
        foreach (var (name, message) in failures)
            warnings.Add($"无法读取装饰贴图 {name}：{message}");
    }

    private static string[] Tags(string? tags) => string.IsNullOrWhiteSpace(tags) || tags == "NO TAG"
        ? ["NO TAG"] : tags.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string MaskTag(string? tag) => string.IsNullOrWhiteSpace(tag) ? "NO TAG" : tag.Trim();

    private static DecorationMasking ParseMasking(string? value) => value switch
    {
        "Mask" => DecorationMasking.Mask,
        "VisibleInsideMask" => DecorationMasking.VisibleInsideMask,
        "VisibleOutsideMask" => DecorationMasking.VisibleOutsideMask,
        _ => DecorationMasking.None,
    };

    private static DecorationBlendMode ParseBlendMode(string? value) => value switch
    {
        "Screen" => DecorationBlendMode.Screen,
        "LinearDodge" => DecorationBlendMode.LinearDodge,
        "Overlay" => DecorationBlendMode.Overlay,
        "SoftLight" => DecorationBlendMode.SoftLight,
        "Difference" => DecorationBlendMode.Difference,
        "Multiply" => DecorationBlendMode.Multiply,
        _ => DecorationBlendMode.None,
    };

    private static Vector2 ResolveTextureScale(IReadOnlyDictionary<string, ImageDecoder.RgbaImage> images,
        string name, Vector2? original, Vector2 current)
    {
        if (original is { X: > 0, Y: > 0 } size &&
            images.TryGetValue(name, out var texture) && texture.Width > 0 && texture.Height > 0)
            return size / new Vector2(texture.Width, texture.Height);
        return current;
    }

    private static Vector2? Read(JArray? value, Vector2 fallback) => new Axes(value).Read(fallback);

    private static float? Number(JArray? array, int index)
    {
        if (array is null || index >= array.Count || array[index].Type is not (JTokenType.Integer or JTokenType.Float))
            return null;
        float value = array[index].Value<float>();
        return float.IsFinite(value) ? value : null;
    }
}

using OpenTK.Mathematics;

namespace SharpFAI.Player.Engine;

/// <summary>Updates authored poses independently from camera-dependent visibility.</summary>
internal sealed class DecorationFrameState
{
    internal readonly record struct Drawable(ImageDecorationTimeline.Item Item,
        ImageDecorationTimeline.Pose Pose, Vector2 Pixels);

    private readonly record struct Pending(ImageDecorationTimeline.Item Item, int Version);

    private readonly List<Drawable> _drawables = [];
    private PriorityQueue<Pending, double> _pending = new();
    private readonly List<Pending> _tweens = [];
    private readonly Dictionary<string, (int X, int Y)> _tiling = new(StringComparer.Ordinal);
    private string?[] _assignedImages = [];
    private int[] _drawSlots = [];
    private ImageDecorationTimeline? _timeline;
    private bool _collectEditorQuads;
    private double _time = double.NegativeInfinity;

    public (bool X, bool Y) Repeat(string image)
    {
        var counts = _tiling.GetValueOrDefault(image);
        return (counts.X > 0, counts.Y > 0);
    }

    public IReadOnlyList<Drawable> Update(ImageDecorationTimeline timeline, double time, bool collectEditorQuads)
    {
        if (!ReferenceEquals(timeline, _timeline) || time < _time || collectEditorQuads != _collectEditorQuads)
        {
            _timeline = timeline;
            _collectEditorQuads = collectEditorQuads;
            _drawables.Clear();
            _pending.Clear();
            _tweens.Clear();
            _tiling.Clear();
            if (timeline.LegacyMasks)
            {
                if (_assignedImages.Length < timeline.Items.Count)
                    _assignedImages = new string?[timeline.Items.Count];
                else
                    Array.Clear(_assignedImages, 0, timeline.Items.Count);
            }
            if (_drawSlots.Length < timeline.Items.Count)
                _drawSlots = new int[timeline.Items.Count];
            Array.Fill(_drawSlots, -1, 0, timeline.Items.Count);
            foreach (var item in timeline.Items)
                if (Sample(item, timeline, time))
                    _tweens.Add(new Pending(item, item.TimingVersion));
        }
        else if (time != _time || timeline.ChangedItems.Count != 0)
        {
            int count = 0;
            for (int i = 0; i < _tweens.Count; i++)
            {
                var pending = _tweens[i];
                if (pending.Version == pending.Item.TimingVersion && Sample(pending.Item, timeline, time))
                    _tweens[count++] = pending;
            }
            _tweens.RemoveRange(count, _tweens.Count - count);

            while (_pending.TryPeek(out var pending, out double next) && next <= time)
            {
                _pending.Dequeue();
                if (pending.Version == pending.Item.TimingVersion && Sample(pending.Item, timeline, time))
                    _tweens.Add(pending);
            }
            foreach (var item in timeline.ChangedItems)
                if (Sample(item, timeline, time))
                    _tweens.Add(new Pending(item, item.TimingVersion));

            if (_pending.Count > timeline.Items.Count + 4096)
                _pending = new(_pending.UnorderedItems.Where(entry => entry.Element.Version == entry.Element.Item.TimingVersion));
        }
        timeline.ChangedItems.Clear();
        _time = time;
        return _drawables;
    }

    private bool Sample(ImageDecorationTimeline.Item item, ImageDecorationTimeline timeline, double time)
    {
        var pose = item.At(time, out double next);
        if (timeline.LegacyMasks && !item.IsText && (item.Tile.X != 1f || item.Tile.Y != 1f) &&
            !string.Equals(_assignedImages[item.Index], pose.Image, StringComparison.Ordinal))
        {
            CountTiling(_assignedImages[item.Index], item.Tile, -1);
            CountTiling(pose.Image, item.Tile, 1);
            _assignedImages[item.Index] = pose.Image;
        }
        bool mask = pose.Masking == DecorationMasking.Mask;
        if (pose.Visible && (!(pose.Opacity <= 0) || (mask && timeline.LegacyMasks)) &&
            (mask || _collectEditorQuads || pose.Tint.W != 0f) &&
            timeline.Images.TryGetValue(pose.Image, out var image))
        {
            var drawable = new Drawable(item, pose, new Vector2(image.Width, image.Height));
            int slot = _drawSlots[item.Index];
            if (slot < 0)
            {
                _drawSlots[item.Index] = _drawables.Count;
                _drawables.Add(drawable);
            }
            else
                _drawables[slot] = drawable;
        }
        else
            Remove(item.Index);

        if (next > time && double.IsFinite(next))
            _pending.Enqueue(new Pending(item, item.TimingVersion), next);
        return next <= time;
    }

    private void CountTiling(string? image, Vector2 tile, int change)
    {
        if (string.IsNullOrEmpty(image))
            return;
        var counts = _tiling.GetValueOrDefault(image);
        if (tile.X != 1f)
            counts.X += change;
        if (tile.Y != 1f)
            counts.Y += change;
        if (counts == default)
            _tiling.Remove(image);
        else
            _tiling[image] = counts;
    }

    private void Remove(int index)
    {
        int slot = _drawSlots[index];
        if (slot < 0)
            return;
        var last = _drawables[^1];
        _drawables[slot] = last;
        _drawSlots[last.Item.Index] = slot;
        _drawables.RemoveAt(_drawables.Count - 1);
        _drawSlots[index] = -1;
    }
}

using System.Collections;

namespace SharpFAI.Player.Engine;

/// <summary>Track states with a single stored value for each uniform page in a very large chart.</summary>
internal sealed class FloorTrackStateTable : IReadOnlyList<FloorTrackState>
{
    private const int PageSize = 128;
    private readonly FloorTrackState[]? _dense;
    private readonly FloorTrackState[]?[]? _pages;

    public FloorTrackStateTable(int count)
    {
        Count = count;
        if (count >= 250_000)
            _pages = new FloorTrackState[(count - 1) / PageSize + 1][];
        else
            _dense = new FloorTrackState[count];
    }

    public FloorTrackStateTable(FloorTrackState[] states)
    {
        _dense = states;
        Count = states.Length;
    }

    public int Count { get; }

    public FloorTrackState this[int index]
    {
        get
        {
            if (_dense is not null) return _dense[index];
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            var page = _pages![index / PageSize];
            return page is null ? default : page[page.Length == 1 ? 0 : index % PageSize];
        }
        set
        {
            if (_dense is not null)
            {
                _dense[index] = value;
                return;
            }
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            int start = index / PageSize;
            int offset = index % PageSize;
            var page = _pages![start];
            if (page is null || page.Length == 1)
            {
                var shared = page is null ? default : page[0];
                if (shared.Equals(value)) return;
                page = new FloorTrackState[Math.Min(PageSize, Count - start * PageSize)];
                Array.Fill(page, shared);
                _pages[start] = page;
            }
            page[offset] = value;
        }
    }

    // The resolver writes in order and reads only already resolved floors.
    public void Initialize(int index, FloorTrackState value)
    {
        if (_pages is not null && index % PageSize == 0)
            _pages[index / PageSize] = [value];
        else
            this[index] = value;
    }

    public void CompactCompletedPage(int index)
    {
        if (_pages is null || ((index + 1) % PageSize != 0 && index + 1 != Count)) return;
        int slot = index / PageSize;
        if (_pages[slot] is not { Length: > 1 } page) return;
        var first = page[0];
        for (int i = 1; i < page.Length; i++)
            if (!first.Equals(page[i])) return;
        _pages[slot] = [first];
    }

    public IEnumerator<FloorTrackState> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

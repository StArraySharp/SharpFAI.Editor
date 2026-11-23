using System.Runtime.InteropServices;

namespace SharpFAI.Editor;

internal sealed class FloorHitCycle
{
    private readonly List<int> _previous = [];
    private int _index;

    public int Pick(List<int> hits)
    {
        if (hits.Count == 0)
        {
            Reset();
            return -1;
        }
        if (CollectionsMarshal.AsSpan(_previous).SequenceEqual(CollectionsMarshal.AsSpan(hits)))
            _index = (_index + 1) % hits.Count;
        else
        {
            _previous.Clear();
            _previous.AddRange(hits);
            _index = 0;
        }
        return hits[_index];
    }

    public void Reset()
    {
        _previous.Clear();
        _index = 0;
    }
}

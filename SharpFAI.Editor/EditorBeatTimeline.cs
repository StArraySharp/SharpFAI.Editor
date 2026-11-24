using Newtonsoft.Json.Linq;

namespace SharpFAI.Editor;

/// <summary>Converts between music seconds and quarter notes on the editor preview clock.</summary>
internal sealed class EditorBeatClock
{
    private readonly record struct Tempo(double Time, double Beat, double BeatsPerSecond);
    private readonly Tempo[] _tempos;
    private readonly Tempo _preroll;

    public EditorBeatClock(ReadOnlySpan<double> floorTimes, ReadOnlySpan<double> floorBpms, double startingBpm)
    {
        int first = floorTimes.Length > 1 ? 1 : 0;
        double start = !floorTimes.IsEmpty && double.IsFinite(floorTimes[first]) ? floorTimes[first] : 0;
        _preroll = new Tempo(start, 0, Rate(startingBpm, 2));
        var current = _preroll;
        List<Tempo> tempos = [current];
        for (int floor = first; floor < Math.Min(floorTimes.Length, floorBpms.Length); floor++)
        {
            double time = floorTimes[floor];
            double rate = Rate(floorBpms[floor], current.BeatsPerSecond);
            if (!double.IsFinite(time) || time < current.Time || rate == current.BeatsPerSecond)
                continue;
            current = new Tempo(time, current.Beat + (time - current.Time) * current.BeatsPerSecond, rate);
            // Midspins may land at the same instant. The last tile sets the outgoing speed.
            if (time == tempos[^1].Time)
                tempos[^1] = current;
            else
                tempos.Add(current);
        }
        _tempos = tempos.ToArray();
    }

    public double BeatAt(double time)
    {
        var tempo = time < _preroll.Time ? _preroll : _tempos[Find(time, byTime: true)];
        return tempo.Beat + (time - tempo.Time) * tempo.BeatsPerSecond;
    }

    public double TimeAt(double beat)
    {
        var tempo = beat < 0 ? _preroll : _tempos[Find(beat, byTime: false)];
        return tempo.Time + (beat - tempo.Beat) / tempo.BeatsPerSecond;
    }

    private int Find(double target, bool byTime)
    {
        int first = 0, last = _tempos.Length - 1;
        while (first < last)
        {
            int middle = first + (last - first + 1) / 2;
            if ((byTime ? _tempos[middle].Time : _tempos[middle].Beat) <= target)
                first = middle;
            else
                last = middle - 1;
        }
        return first;
    }

    private static double Rate(double bpm, double fallback) =>
        double.IsFinite(bpm) && bpm > 1e-6 ? bpm / 60 : fallback;
}

internal readonly record struct TimeSignature(int Numerator, int Denominator)
{
    public static readonly TimeSignature Common = new(4, 4);
    public const int MaxNumerator = 64;
    public const int MaxDenominator = 64;
    public double BeatLength => 4d / Denominator;
    public double MeasureLength => Numerator * BeatLength;
    public bool IsValid => Numerator is > 0 and <= MaxNumerator && Denominator is > 0 and <= MaxDenominator &&
        (Denominator & (Denominator - 1)) == 0;
    public override string ToString() => $"{Numerator}/{Denominator}";
}

internal sealed class EditorTimeSignatures
{
    internal readonly record struct Change(double Beat, TimeSignature Signature, double Measure);
    private const double Epsilon = 1e-7;
    private readonly Change[] _changes;

    private EditorTimeSignatures(SortedDictionary<double, TimeSignature> source)
    {
        source.TryAdd(0, TimeSignature.Common);
        List<Change> changes = [];
        foreach (var (beat, signature) in source)
        {
            double measure = 1;
            if (changes.Count > 0)
            {
                var previous = changes[^1];
                measure = previous.Measure + Math.Max(1,
                    Math.Ceiling((beat - previous.Beat) / previous.Signature.MeasureLength - Epsilon));
            }
            changes.Add(new Change(beat, signature, measure));
        }
        _changes = changes.ToArray();
    }

    public IReadOnlyList<Change> Changes => _changes;

    public static EditorTimeSignatures Read(JToken? data)
    {
        SortedDictionary<double, TimeSignature> changes = [];
        if (data is JArray entries)
        {
            foreach (var item in entries.OfType<JObject>())
            {
                if (item["beat"] is not JValue { Type: JTokenType.Float or JTokenType.Integer } beatValue ||
                    !double.TryParse(beatValue.ToString(Newtonsoft.Json.Formatting.None),
                        System.Globalization.CultureInfo.InvariantCulture, out double beat) ||
                    !double.IsFinite(beat) || beat < 0 ||
                    !TryReadInt(item["numerator"], out int numerator) ||
                    !TryReadInt(item["denominator"], out int denominator))
                    continue;
                TimeSignature signature = new(numerator, denominator);
                if (signature.IsValid)
                    changes[beat] = signature;
            }
        }
        return new EditorTimeSignatures(changes);

        static bool TryReadInt(JToken? item, out int value) =>
            int.TryParse(item is JValue { Type: JTokenType.Integer } number
                ? number.ToString(Newtonsoft.Json.Formatting.None) : null, out value);
    }

    public EditorTimeSignatures WithChange(double beat, TimeSignature? signature)
    {
        if (!double.IsFinite(beat) || beat < 0 || signature is { IsValid: false })
            return this;
        SortedDictionary<double, TimeSignature> changes = [];
        foreach (var change in _changes)
            if (change.Beat != beat)
                changes.Add(change.Beat, change.Signature);
        if (signature is { } value)
            changes.Add(beat, value);
        return new EditorTimeSignatures(changes);
    }

    public JArray? Write() => _changes.Length == 1 && _changes[0].Signature == TimeSignature.Common ? null :
        new JArray(_changes.Select(change => new JObject
        {
            ["beat"] = change.Beat,
            ["numerator"] = change.Signature.Numerator,
            ["denominator"] = change.Signature.Denominator,
        }));

    public int IndexAt(double beat)
    {
        int first = 0, last = _changes.Length - 1;
        while (first < last)
        {
            int middle = first + (last - first + 1) / 2;
            if (_changes[middle].Beat <= beat + Epsilon)
                first = middle;
            else
                last = middle - 1;
        }
        return first;
    }

    public double Snap(double beat, bool previousOnly = false)
    {
        int index = IndexAt(beat);
        var change = _changes[index];
        double length = change.Signature.BeatLength;
        double offset = (beat - change.Beat) / length;
        double snapped = change.Beat + Math.Max(0, previousOnly ? Math.Floor(offset + Epsilon) : Math.Round(offset)) * length;
        return index + 1 < _changes.Length ? Math.Min(snapped, _changes[index + 1].Beat) : snapped;
    }

    public double? SnapWithin(double beat, double minimum, double maximum)
    {
        minimum = Math.Max(0, minimum);
        if (maximum < minimum)
            return null;

        double snapped = Snap(Math.Clamp(beat, minimum, maximum));
        if (snapped < minimum - Epsilon)
        {
            int index = IndexAt(minimum);
            snapped += _changes[index].Signature.BeatLength;
            if (index + 1 < _changes.Length)
                snapped = Math.Min(snapped, _changes[index + 1].Beat);
        }
        if (snapped > maximum + Epsilon)
            snapped = Snap(maximum, previousOnly: true);
        return snapped >= minimum - Epsilon ? snapped : null;
    }

    public (double Measure, int Beat) PositionAt(double beat)
    {
        var change = _changes[IndexAt(beat)];
        double beats = Math.Max(0, Math.Floor((beat - change.Beat) / change.Signature.BeatLength + Epsilon));
        return (change.Measure + Math.Floor(beats / change.Signature.Numerator),
            1 + (int)(beats % change.Signature.Numerator));
    }

    public double MeasureAt(double beat) => PositionAt(beat).Measure;
}

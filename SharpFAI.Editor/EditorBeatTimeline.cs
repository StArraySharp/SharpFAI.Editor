using System.Globalization;
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
        _preroll = new Tempo(FirstBeatTime(floorTimes), 0, Rate(startingBpm, 2));
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

    public EditorBeatClock(ReadOnlySpan<double> floorTimes, double levelBpm, EditorTimeSignatures? timelineMeters = null)
    {
        _preroll = new Tempo(FirstBeatTime(floorTimes), 0, Rate(levelBpm, 2));
        var current = _preroll;
        List<Tempo> tempos = [current];
        if (timelineMeters is not null)
        {
            foreach (var change in timelineMeters.Changes)
            {
                double rate = Rate(change.TimelineBpm ?? levelBpm, _preroll.BeatsPerSecond);
                if (rate == current.BeatsPerSecond) continue;
                double time = current.Time + (change.Beat - current.Beat) / current.BeatsPerSecond;
                current = new Tempo(time, change.Beat, rate);
                if (change.Beat == 0) tempos[0] = current;
                else tempos.Add(current);
            }
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
        EditorTimeSignatures.IsValidBpm(bpm) ? bpm / 60 : fallback;

    private static double FirstBeatTime(ReadOnlySpan<double> floorTimes)
    {
        int first = floorTimes.Length > 1 ? 1 : 0;
        return !floorTimes.IsEmpty && double.IsFinite(floorTimes[first]) ? floorTimes[first] : 0;
    }
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
    internal readonly record struct Change(double Beat, TimeSignature Signature, double Measure, double? TimelineBpm);
    private const double Epsilon = 1e-7;
    private readonly Change[] _changes;

    private EditorTimeSignatures(SortedDictionary<double, (TimeSignature Signature, double? TimelineBpm)> source)
    {
        source.TryAdd(0, (TimeSignature.Common, null));
        List<Change> changes = [];
        foreach (var (beat, (signature, timelineBpm)) in source)
        {
            double measure = 1;
            if (changes.Count > 0)
            {
                var previous = changes[^1];
                measure = previous.Measure + Math.Max(1,
                    Math.Ceiling((beat - previous.Beat) / previous.Signature.MeasureLength - Epsilon));
            }
            changes.Add(new Change(beat, signature, measure, timelineBpm));
        }
        _changes = changes.ToArray();
    }

    public IReadOnlyList<Change> Changes => _changes;

    public static bool IsValidBpm(double bpm) => double.IsFinite(bpm) && bpm > 1e-6;

    public static EditorTimeSignatures Read(JToken? data)
    {
        SortedDictionary<double, (TimeSignature, double?)> changes = [];
        if (data is JArray entries)
        {
            foreach (var item in entries.OfType<JObject>())
            {
                if (!TryReadDouble(item["beat"], out double beat) ||
                    !double.IsFinite(beat) || beat < 0 ||
                    !TryReadInt(item["numerator"], out int numerator) ||
                    !TryReadInt(item["denominator"], out int denominator))
                    continue;
                TimeSignature signature = new(numerator, denominator);
                if (signature.IsValid)
                    changes[beat] = (signature, TryReadDouble(item["bpm"], out double bpm) && IsValidBpm(bpm) ? bpm : null);
            }
        }
        return new EditorTimeSignatures(changes);

        static bool TryReadInt(JToken? item, out int value) =>
            int.TryParse(item is JValue { Type: JTokenType.Integer } number
                ? number.ToString(Newtonsoft.Json.Formatting.None) : null, out value);

        static bool TryReadDouble(JToken? item, out double value) =>
            double.TryParse(item is JValue { Type: JTokenType.Float or JTokenType.Integer } number
                ? number.ToString(Newtonsoft.Json.Formatting.None) : null, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public EditorTimeSignatures WithChange(double beat, TimeSignature? signature, double? timelineBpm = null)
    {
        if (!double.IsFinite(beat) || beat < 0 || signature is { IsValid: false } ||
            (timelineBpm is double bpm && !IsValidBpm(bpm)))
            return this;
        SortedDictionary<double, (TimeSignature, double?)> changes = [];
        foreach (var change in _changes)
            if (change.Beat != beat)
                changes.Add(change.Beat, (change.Signature, change.TimelineBpm));
        if (signature is { } value)
            changes.Add(beat, (value, timelineBpm));
        return new EditorTimeSignatures(changes);
    }

    public JArray? Write() => _changes is [{ Signature: var first, TimelineBpm: null }] && first == TimeSignature.Common
        ? null : new JArray(_changes.Select(change =>
        {
            JObject value = new()
            {
                ["beat"] = change.Beat,
                ["numerator"] = change.Signature.Numerator,
                ["denominator"] = change.Signature.Denominator,
            };
            if (change.TimelineBpm is double bpm) value["bpm"] = bpm;
            return value;
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

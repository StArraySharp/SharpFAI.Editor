using OpenTK.Mathematics;

namespace SharpFAI.Player.Engine;

public sealed unsafe partial class LevelRenderer
{
    private void UpdateOfflineTrail(double time)
    {
        double cursor = _offlineTrailTime;
        if (double.IsNaN(cursor) || time < cursor || time - cursor > PlanetTrail.Lifetime)
        {
            _trail.Clear();
            _offlineTrailFloor = -1;
            cursor = Math.Max(0.0, time - PlanetTrail.Lifetime);
            RecordAt(FloorAtTime(_entryTimes, cursor), cursor);
        }

        // A video frame can cross several tiles, including complete revolutions.
        // Sample each occupied tile interval before handing off to the next planet.
        int floor = _offlineTrailFloor;
        while (cursor < time)
        {
            double boundary = floor + 1 < _entryTimes.Length ? _entryTimes[floor + 1] : double.PositiveInfinity;
            bool crossesFloor = boundary <= time;
            double end = crossesFloor ? Math.BitDecrement(boundary) : time;
            if (end > cursor)
            {
                double duration = double.IsPositiveInfinity(boundary) ? LastFloorWindow(floor)
                    : boundary - (floor == 0 ? 0.0 : _entryTimes[floor]);
                double degrees = duration > 1e-9 ? Math.Abs(_sweepDelta[floor]) * ((end - cursor) / duration) : 0.0;
                int steps = (int)Math.Clamp(Math.Ceiling(degrees / TrailAngleStepDegrees), 1.0, 48.0);
                for (int step = 1; step < steps; step++)
                    RecordAt(floor, cursor + (end - cursor) * (step / (double)steps));
                RecordAt(floor, end);
            }

            if (!crossesFloor)
                break;

            cursor = Math.Max(cursor, boundary);
            // Co-timed midspins have no interval to sample, but the successor has the resolved roles.
            do { floor++; }
            while (floor + 1 < _entryTimes.Length && _entryTimes[floor + 1] <= cursor);
            RecordAt(floor, cursor);
        }
        _offlineTrailTime = time;

        void RecordAt(int index, double sampleTime)
        {
            PlanetPose pose = EvaluatePlanetPose(index, sampleTime, manual: false);
            RecordTrailPose(index, pose.Pivot, pose.Orbit, pose.Third, sampleTime,
                ref _offlineTrailFloor, preserveHistory: true);
        }
    }

    private void RecordTrailPose(int floor, Vector2 pivot, Vector2 orbit, Vector2 third, double time,
        ref int previousFloor, bool preserveHistory)
    {
        PlanetTrackFloor track = _planetTrack[floor];
        if (previousFloor != floor && (uint)previousFloor < (uint)_planetTrack.Length)
        {
            int joined = ActivePlanets(track) & ~ActivePlanets(_planetTrack[previousFloor]);
            for (int id = 0; id < 3; id++)
                if ((joined & (1 << id)) != 0)
                    _trail.ResetPlanet(id);
        }

        _trail.RecordPlanet(track.Pivot, pivot, PlanetTint(track.Pivot), time, preserveHistory);
        _trail.RecordPlanet(track.Orbit, orbit, PlanetTint(track.Orbit), time, preserveHistory);
        if (track.Count == 3)
            _trail.RecordPlanet(track.Third, third, PlanetTint(track.Third), time, preserveHistory);
        previousFloor = floor;
    }

    private static int ActivePlanets(PlanetTrackFloor track) =>
        (1 << track.Pivot) | (1 << track.Orbit) | (track.Count == 3 ? 1 << track.Third : 0);
}

using SharpISOBMFF;
using SharpMP4.Readers;

namespace SharpFAI.Player;

internal static class Mp4Container
{
    public readonly record struct Video(int Width, int Height, double Aspect, double Duration,
        double Start, double InputStart, double FrameRate);

    private static readonly uint VideoHandler = IsoStream.FromFourCC("vide");
    private static readonly uint AudioHandler = IsoStream.FromFourCC("soun");

    public static Video? ReadVideo(string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is not (".mp4" or ".fmp4" or ".m4v" or ".mov"))
            return null;

        using var file = File.OpenRead(path);
        using var input = new IsoStream(file);
        var container = new Container();
        container.Read(input);
        var movie = container.Children.OfType<MovieBox>().FirstOrDefault();
        var movieHeader = movie?.Children.OfType<MovieHeaderBox>().FirstOrDefault();
        if (movie is null || movieHeader is null || movieHeader.Timescale == 0 || container.IsTruncated)
            return null;

        var tracks = movie.Children.OfType<TrackBox>().Select(track => new TrackInfo(track)).ToArray();
        var video = tracks.FirstOrDefault(track => track.Handler == VideoHandler && track.Visual is not null);
        if (video?.Visual is not { Width: > 0, Height: > 0 } sample ||
            video.Header is not { Timescale: > 0 } header || video.TrackHeader is null)
            return null;

        double start = video.Start(movieHeader.Timescale);
        double inputStart = tracks.Where(track => track.Handler == AudioHandler)
            .Aggregate(start, (earliest, track) => Math.Min(earliest, track.Start(movieHeader.Timescale)));
        double duration = (double)header.Duration / header.Timescale;
        if (video.Edit is { MediaTime: { Length: 1 }, EditDuration: { Length: 1 } } edit)
        {
            double editedDuration = (double)edit.EditDuration[0] / movieHeader.Timescale;
            duration = Math.Min(duration, editedDuration);
        }
        if (duration <= 0)
            duration = (double)movieHeader.Duration / movieHeader.Timescale;

        uint delta = video.Table?.Children.OfType<TimeToSampleBox>().FirstOrDefault()?.SampleDelta
            ?.FirstOrDefault() ?? 0;
        double rate = delta == 0 ? 30 : (double)header.Timescale / delta;
        if (container.Children.OfType<MovieFragmentBox>().Any())
        {
            using var reader = new VideoReader();
            reader.Parse(container);
            var timings = reader.GetSampleTimings(video.TrackHeader.TrackID);
            if (timings.Length == 0)
                return null;
            long firstPts = timings.Min(sample => sample.PTS);
            start = video.Start(movieHeader.Timescale, firstPts);
            double length = (double)(timings.Max(sample => sample.PTS + sample.Duration) - firstPts) / header.Timescale;
            if (length > 0)
            {
                duration = length;
                rate = timings.Length / length;
            }
            inputStart = start;
            foreach (var audio in tracks.Where(track => track.Handler == AudioHandler))
            {
                if (audio.TrackHeader is { } audioHeader && reader.ReadSample(audioHeader.TrackID) is { } first)
                    inputStart = Math.Min(inputStart, audio.Start(movieHeader.Timescale, first.PTS));
            }
        }

        double aspect = (double)sample.Width / sample.Height;
        if (sample.Pasp is { HSpacing: > 0, VSpacing: > 0 } ratio)
            aspect *= (double)ratio.HSpacing / ratio.VSpacing;

        int rotation = Rotation(video.TrackHeader.Matrix);
        return rotation is 90 or 270
            ? new(sample.Height, sample.Width, 1 / aspect, duration, start, inputStart, rate)
            : new(sample.Width, sample.Height, aspect, duration, start, inputStart, rate);
    }

    private static int Rotation(int[] matrix)
    {
        if (matrix is not { Length: 9 })
            return 0;
        double angle = Math.Atan2(matrix[1], matrix[0]) * 180 / Math.PI;
        return ((int)Math.Round(angle / 90) * 90 + 360) % 360;
    }

    private sealed class TrackInfo(TrackBox track)
    {
        private MediaBox? Media { get; } = track.Children.OfType<MediaBox>().FirstOrDefault();
        public uint Handler => Media?.Children.OfType<HandlerBox>().FirstOrDefault()?.HandlerType ?? 0;
        public TrackHeaderBox? TrackHeader => track.Children.OfType<TrackHeaderBox>().FirstOrDefault();
        public MediaHeaderBox? Header => Media?.Children.OfType<MediaHeaderBox>().FirstOrDefault();
        public SampleTableBox? Table => Media?.Children.OfType<MediaInformationBox>().FirstOrDefault()
            ?.Children.OfType<SampleTableBox>().FirstOrDefault();
        public EditListBox? Edit => track.Children.OfType<EditBox>().FirstOrDefault()
            ?.Children.OfType<EditListBox>().FirstOrDefault();
        public VisualSampleEntry? Visual => Table?.Children.OfType<SampleDescriptionBox>().FirstOrDefault()
            ?.Children.OfType<VisualSampleEntry>().FirstOrDefault();

        public double Start(uint movieTimescale, long? samplePts = null)
        {
            if (Header is not { Timescale: > 0 } header)
                return 0;
            var offsets = Table?.Children.OfType<CompositionOffsetBox>().FirstOrDefault();
            long firstPts = samplePts ?? (offsets?.Version == 1
                ? offsets.SampleOffset0?.FirstOrDefault() ?? 0
                : offsets?.SampleOffset?.FirstOrDefault() ?? 0L);
            if (Edit is not { } edit)
                return (double)firstPts / header.Timescale;
            double start = 0;
            for (int i = 0; i < edit.MediaTime.Length; i++)
            {
                if (edit.MediaTime[i] >= 0)
                    return start + (double)Math.Max(0, firstPts - edit.MediaTime[i]) / header.Timescale;
                start += (double)edit.EditDuration[i] / movieTimescale;
            }
            return start;
        }
    }

    public static void WriteExport(string encodedPath, string outputPath)
    {
        using var file = File.OpenRead(encodedPath);
        using var input = new IsoStream(file);
        var container = new Container();
        container.Read(input);
        var movie = container.Children.OfType<MovieBox>().SingleOrDefault();
        var media = container.Children.OfType<MediaDataBox>().SingleOrDefault();
        if (container.IsTruncated || movie is null || media?.Data is not { Length: > 0 })
            throw new InvalidDataException("Invalid MP4 encoder output.");

        // Keep the encoder's edit lists, color metadata and samples intact when moving the index up front.
        int mediaIndex = container.Children.IndexOf(media);
        int movieIndex = container.Children.IndexOf(movie);
        if (movieIndex > mediaIndex)
            MoveMovieBeforeMedia(container, movie, movieIndex, mediaIndex);

        using var output = File.Open(outputPath, FileMode.CreateNew, FileAccess.Write);
        using var stream = new IsoStream(output);
        ulong written = container.Write(stream) >> 3;
        if (written != (ulong)output.Position || written != (container.CalculateSize() >> 3))
            throw new InvalidDataException("Incomplete MP4 output.");
    }

    private static void MoveMovieBeforeMedia(Container container, MovieBox movie, int movieIndex, int mediaIndex)
    {
        ulong shift = IsoStream.CalculateBoxSize(movie) >> 3;
        var tables = movie.Children.OfType<TrackBox>().Select(track => new TrackInfo(track).Table)
            .OfType<SampleTableBox>().ToArray();
        var smallOffsets = tables.SelectMany(table => table.Children.OfType<ChunkOffsetBox>()).ToArray();
        if (smallOffsets.Any(box => box.ChunkOffset.Any(offset => offset + shift > uint.MaxValue)))
            return;

        foreach (var box in smallOffsets)
            for (int i = 0; i < box.ChunkOffset.Length; i++)
                box.ChunkOffset[i] += (uint)shift;
        foreach (var box in tables.SelectMany(table => table.Children.OfType<ChunkLargeOffsetBox>()))
            for (int i = 0; i < box.ChunkOffset.Length; i++)
                box.ChunkOffset[i] += shift;

        container.Children.RemoveAt(movieIndex);
        container.Children.Insert(mediaIndex, movie);
    }
}

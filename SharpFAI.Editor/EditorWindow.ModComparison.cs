using SharpFAI.ModApi;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace SharpFAI.Editor;

internal sealed partial class EditorWindow
{
    private sealed partial class ModEditorInspection
    {
        public async ValueTask<ChartFrameDifference> CompareAsync(FrameCaptureRequest frame, ChartEditRequest? proposedEdit,
            string? referencePng, CancellationToken token)
        {
            if ((proposedEdit is null) == (referencePng is null))
                throw new ArgumentException("Provide exactly one of a proposed edit or an absolute reference PNG path.");
            string? referencePath = referencePng is null ? null : AbsoluteFile(referencePng, ".png");
            var current = await CaptureAsync(frame with { IncludeImage = true }, token);
            // Holding seconds constant matters when the proposed edit changes timing.
            ChartFrameCapture? edited = proposedEdit is null ? null : await CaptureAsync(
                frame with { Floor = null, Seconds = current.State.Seconds, IncludeImage = true }, proposedEdit, token);
            return await Task.Run(() => ComparePixels(current, edited, referencePath, token), token);
        }

        private static ChartFrameDifference ComparePixels(ChartFrameCapture current, ChartFrameCapture? edited, string? path,
            CancellationToken token)
        {
            using var first = Image.Load<Rgba32>(current.Png!);
            using var second = edited is not null ? Image.Load<Rgba32>(edited.Png!) : ReadReference(path!, current.Width, current.Height);
            using var diff = new Image<Rgba32>(first.Width, first.Height);
            int pixels = 0, maximum = 0;
            long sum = 0;
            first.ProcessPixelRows(second, diff, (a, b, output) =>
            {
                for (int y = 0; y < a.Height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    var left = a.GetRowSpan(y);
                    var right = b.GetRowSpan(y);
                    var row = output.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        int r = Math.Abs(left[x].R - right[x].R);
                        int g = Math.Abs(left[x].G - right[x].G);
                        int blue = Math.Abs(left[x].B - right[x].B);
                        int peak = Math.Max(r, Math.Max(g, blue));
                        if (peak > 0) pixels++;
                        maximum = Math.Max(maximum, peak);
                        sum += r + g + blue;
                        row[x] = new((byte)r, (byte)g, (byte)blue, 255);
                    }
                }
            });
            using var encoded = new MemoryStream();
            diff.SaveAsPng(encoded);
            return new(current.State, edited?.State, first.Width, first.Height, pixels, maximum,
                sum / (3.0 * first.Width * first.Height), edited is null ? null : current.State.Camera != edited.State.Camera,
                edited is null ? null : !SameFilters(current.State.Filters, edited.State.Filters), encoded.ToArray());
        }

        private static Image<Rgba32> ReadReference(string path, int width, int height)
        {
            using var stream = File.OpenRead(path);
            var info = Image.Identify(stream);
            if (info.Width != width || info.Height != height)
                throw new ArgumentException($"Reference image dimensions must be {width} × {height}.");
            stream.Position = 0;
            return Image.Load<Rgba32>(stream);
        }

        private static bool SameFilters(IReadOnlyList<ChartFilter> a, IReadOnlyList<ChartFilter> b) =>
            a.Count == b.Count && a.Zip(b).All(pair =>
                pair.First.Layer == pair.Second.Layer && pair.First.Name == pair.Second.Name &&
                pair.First.Parameters.Count == pair.Second.Parameters.Count &&
                pair.First.Parameters.All(item => pair.Second.Parameters.TryGetValue(item.Key, out var value) &&
                    item.Value.AsSpan().SequenceEqual(value)));
    }
}

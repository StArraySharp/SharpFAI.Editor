using Timer = System.Threading.Timer;

namespace SharpFAI.Build;

internal sealed class ConsoleProgressBar : IDisposable
{
    private const int BarWidth = 20;
    private readonly string label;
    private readonly bool redirected = Console.IsOutputRedirected;
    private readonly object gate = new();
    private readonly Timer? animation;
    private long completed;
    private long total;
    private int frame;
    private int previousWidth;
    private int previousMilestone = -1;
    private bool wroteWaiting;
    private bool finished;

    public ConsoleProgressBar(string label)
    {
        this.label = label;
        Draw();
        if (!redirected)
            animation = new Timer(_ => Animate(), null, 150, 150);
    }

    public void Report(long completed, long total)
    {
        lock (gate)
        {
            if (finished)
                return;
            this.completed = completed;
            this.total = total;
            Draw();
        }
    }

    public void Complete(string? detail = null) => Finish($"[{new string('#', BarWidth)}] 100% {label}" +
        (detail is null ? "" : $" ({detail})"));

    public void Dispose() => Finish($"[stopped] {label}");

    private void Animate()
    {
        lock (gate)
        {
            if (finished || total > 0)
                return;
            frame++;
            Draw();
        }
    }

    private void Draw()
    {
        if (total <= 0)
        {
            if (redirected)
            {
                if (!wroteWaiting)
                    Console.WriteLine($"[working] {label}");
                wroteWaiting = true;
            }
            else
            {
                int offset = frame % (2 * BarWidth - 2);
                int position = Math.Min(offset, 2 * BarWidth - 2 - offset);
                WriteLine($"[{new string('-', position)}#{new string('-', BarWidth - position - 1)}]      {label}", final: false);
            }
            return;
        }

        int percent = (int)Math.Clamp(completed * 100d / total, 0, 99);
        int milestone = redirected ? percent / 10 : percent;
        if (milestone == previousMilestone)
            return;
        previousMilestone = milestone;
        int filled = percent * BarWidth / 100;
        WriteLine($"[{new string('#', filled)}{new string('-', BarWidth - filled)}] {percent,3}% {label}", final: false);
    }

    private void Finish(string line)
    {
        lock (gate)
        {
            if (finished)
                return;
            finished = true;
            animation?.Dispose();
            WriteLine(line, final: true);
        }
    }

    private void WriteLine(string line, bool final)
    {
        if (redirected)
        {
            Console.WriteLine(line);
            return;
        }

        if (!final)
        {
            int width = Console.WindowWidth;
            if (width > 1 && line.Length >= width)
                line = line[..(width - 1)];
        }
        Console.Write('\r');
        Console.Write(line.PadRight(previousWidth));
        previousWidth = line.Length;
        if (final)
            Console.WriteLine();
    }
}

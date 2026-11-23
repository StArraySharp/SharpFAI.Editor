namespace SharpFAI.Player.Engine.Filters;

// Shared by the camera chain, including when the active filter changes or is switched off.
internal sealed class CameraFilterOutputHistory<T> where T : class
{
    private (int Width, int Height) _size;
    private double _time = double.NegativeInfinity;
    public T? Output { get; private set; }

    public T? Observe(double time, int width, int height)
    {
        bool invalidated = _size != (width, height) || time < _time;
        _size = (width, height);
        _time = time;
        return invalidated ? Clear() : null;
    }

    public T? Fallback(Exception error) => error is InvalidFilterTargetSizeException or ArithmeticException
        ? Output : null;

    public void Remember(T output) => Output = output;

    public T? Clear()
    {
        T? previous = Output;
        Output = null;
        return previous;
    }
}

internal sealed class InvalidFilterTargetSizeException(string message) : Exception(message);

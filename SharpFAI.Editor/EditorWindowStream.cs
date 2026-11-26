using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using OpenTK.Graphics.OpenGL4;
using SharpFAI.ModApi;
using SharpFAI.Player;

namespace SharpFAI.Editor;

internal sealed class EditorWindowStream : IEditorWindowStream, IDisposable
{
    private readonly object _gate = new();
    private Session? _session;
    private bool _closed;

    internal Session? Current => Volatile.Read(ref _session);

    public IEditorWindowStreamSession? TryOpen()
    {
        lock (_gate)
        {
            if (_closed || _session is not null) return null;
            var session = new Session(this);
            Volatile.Write(ref _session, session);
            return session;
        }
    }

    private void Remove(Session session)
    {
        lock (_gate)
            if (_session == session) Volatile.Write(ref _session, null);
    }

    public void Dispose()
    {
        Session? session;
        lock (_gate)
        {
            _closed = true;
            session = _session;
            Volatile.Write(ref _session, null);
        }
        session?.Dispose();
    }

    internal sealed class Session(EditorWindowStream owner) : IEditorWindowStreamSession
    {
        private readonly Channel<EditorWindowInput> _input = Channel.CreateBounded<EditorWindowInput>(
            new BoundedChannelOptions(256) { SingleReader = true });
        private TaskCompletionSource<EditorWindowFrame>? _frame;
        private int _closed;

        public async ValueTask<EditorWindowFrame> CaptureAsync(CancellationToken token,
            EditorWindowFrameFormat format = EditorWindowFrameFormat.Rgba8)
        {
            token.ThrowIfCancellationRequested();
            if (format is not (EditorWindowFrameFormat.Rgba8 or EditorWindowFrameFormat.Jpeg))
                throw new ArgumentOutOfRangeException(nameof(format));
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            var request = new TaskCompletionSource<EditorWindowFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _frame, request, null) is not null)
                throw new InvalidOperationException("A window frame is already requested.");
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
                var frame = await request.Task.WaitAsync(token).ConfigureAwait(false);
                if (format == EditorWindowFrameFormat.Rgba8) return frame;
                token.ThrowIfCancellationRequested();
                return frame with { Format = format, Data = FrameCapture.EncodeJpeg(frame.Data, frame.Width, frame.Height) };
            }
            finally { Interlocked.CompareExchange(ref _frame, null, request); }
        }

        public ValueTask SendInputAsync(EditorWindowInput input, CancellationToken token)
        {
            bool inBounds = input.X is >= 0 and <= 1 && input.Y is >= 0 and <= 1;
            bool valid = input.Kind switch
            {
                EditorWindowInputKind.Move => inBounds,
                EditorWindowInputKind.Button => inBounds && input.Button is >= 0 and <= 2,
                EditorWindowInputKind.Wheel => inBounds && input.DeltaX is >= -20 and <= 20 && input.DeltaY is >= -20 and <= 20,
                EditorWindowInputKind.Key => input.KeyCode is { Length: > 0 and <= 32 },
                EditorWindowInputKind.Text => input.Text is { Length: > 0 and <= 512 },
                EditorWindowInputKind.Reset => true,
                _ => false,
            };
            if (!valid) throw new ArgumentException("Invalid editor window input.", nameof(input));
            return _input.Writer.WriteAsync(input, token);
        }

        internal bool TryRead([NotNullWhen(true)] out EditorWindowInput? input)
        {
            input = null;
            return Volatile.Read(ref _closed) == 0 && _input.Reader.TryRead(out input);
        }

        internal void Capture(int width, int height)
        {
            if (width <= 0 || height <= 0 || Volatile.Read(ref _frame) is not { Task.IsCompleted: false } request)
                return;
            int framebuffer = GL.GetInteger(GetPName.ReadFramebufferBinding);
            int packBuffer = GL.GetInteger(GetPName.PixelPackBufferBinding);
            try
            {
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
                GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
                request.TrySetResult(new(width, height, EditorWindowFrameFormat.Rgba8, FrameCapture.ReadPixels(width, height)));
            }
            catch (Exception error) { request.TrySetException(error); }
            finally
            {
                GL.BindBuffer(BufferTarget.PixelPackBuffer, packBuffer);
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, framebuffer);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            owner.Remove(this);
            _input.Writer.TryComplete();
            Interlocked.Exchange(ref _frame, null)?.TrySetCanceled();
        }
    }
}

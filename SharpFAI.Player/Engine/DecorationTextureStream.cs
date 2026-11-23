using OpenTK.Graphics.OpenGL4;

namespace SharpFAI.Player.Engine;

/// <summary>Reuses texture storage for rapidly changing decoration images.</summary>
internal sealed class DecorationTextureStream(long idleTextureBudget = 192L * 1024 * 1024,
    bool preferReuse = true, bool useImmutableStorage = true) : IDisposable
{
    internal readonly record struct Key(string Image, bool Smoothing, bool RepeatX, bool RepeatY);

    private sealed class Slot(int handle, int width, int height, DecorationChannels channels, bool smoothing, long bytes)
    {
        public int Handle { get; } = handle;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public DecorationChannels Channels { get; } = channels;
        public bool Smoothing { get; } = smoothing;
        public long Bytes { get; } = bytes;
        public long LastUsed { get; set; }
        public int UploadedRows { get; set; }
        public double NeededAt { get; set; } = double.NegativeInfinity;
        public Key Key { get; set; }
    }

    private readonly List<Slot> _slots = [];
    private readonly Dictionary<Key, Slot> _images = [];
    private long _frame;
    private long _bytes;
    private double _time = double.NegativeInfinity;
    private bool? _immutableStorage;

    public void BeginFrame(double time)
    {
        if (time < _time)
            foreach (Slot slot in _slots)
                slot.NeededAt = double.NegativeInfinity;
        _time = time;
        _frame++;
    }

    public int Get(Key key, in DecorationImage image)
    {
        if (!_images.TryGetValue(key, out Slot? slot))
            slot = Acquire(key, image, allowOverBudget: true)!;
        if (slot.UploadedRows < slot.Height)
            Upload(slot, image, slot.Height - slot.UploadedRows);
        slot.LastUsed = _frame;
        return slot.Handle;
    }

    public bool Prefetch(Key key, in DecorationImage image, double neededAt) =>
        Prefetch(key, image, neededAt, int.MaxValue, out bool ready) || !ready;

    // A partially uploaded texture is reserved but cannot be drawn until all rows exist.
    public bool Prefetch(Key key, in DecorationImage image, double neededAt, int byteBudget, out bool ready)
    {
        if (!_images.TryGetValue(key, out Slot? slot))
            slot = Acquire(key, image, allowOverBudget: false);
        ready = slot is not null && slot.UploadedRows == slot.Height;
        if (slot is null)
            return false;
        slot.NeededAt = Math.Max(slot.NeededAt, neededAt);
        if (ready)
            return false;

        int rows = Math.Min(slot.Height - slot.UploadedRows, Math.Max(1, byteBudget / image.RowBytes));
        Upload(slot, image, rows);
        ready = slot.UploadedRows == slot.Height;
        return true;
    }

    public bool TryTake(Key key, out int handle)
    {
        if (_images.TryGetValue(key, out Slot? slot) && slot.UploadedRows == slot.Height)
        {
            _images.Remove(key);
            _slots.Remove(slot);
            _bytes -= slot.Bytes;
            handle = slot.Handle;
            return true;
        }
        handle = 0;
        return false;
    }

    private Slot? Acquire(Key key, in DecorationImage image, bool allowOverBudget)
    {
        long bytes = image.TextureBytes;
        var slot = preferReuse || _bytes + bytes > idleTextureBudget
            ? FindReusable(image, key.Smoothing) : null;
        if (slot is null)
            slot = Allocate(image, key.Smoothing, bytes, allowOverBudget);
        else
            _images.Remove(slot.Key);
        if (slot is null)
            return null;

        GL.BindTexture(TextureTarget.Texture2D, slot.Handle);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)(key.RepeatX ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)(key.RepeatY ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge));
        slot.Key = key;
        slot.LastUsed = _frame;
        slot.UploadedRows = 0;
        slot.NeededAt = double.NegativeInfinity;
        _images.Add(key, slot);
        return slot;
    }

    private Slot? FindReusable(in DecorationImage image, bool smoothing)
    {
        Slot? oldest = null;
        foreach (Slot slot in _slots)
        {
            // Keep the current frame's handles stable for sprite batches. Leave the
            // preceding frames alone while the GPU is still consuming their draws.
            if (!IsIdle(slot) || slot.Width != image.Width || slot.Height != image.Height ||
                slot.Smoothing != smoothing || slot.Channels != image.Channels)
                continue;
            if (oldest is null || slot.LastUsed < oldest.LastUsed)
                oldest = slot;
        }
        return oldest;
    }

    private Slot? Allocate(in DecorationImage image, bool smoothing, long bytes, bool allowOverBudget)
    {
        if (!allowOverBudget && bytes > idleTextureBudget)
            return null;
        while (_bytes + bytes > idleTextureBudget)
        {
            Slot? oldest = null;
            foreach (Slot slot in _slots)
                if (IsIdle(slot) && (oldest is null || slot.LastUsed < oldest.LastUsed))
                    oldest = slot;
            if (oldest is null)
            {
                if (!allowOverBudget)
                    return null;
                break;
            }
            _images.Remove(oldest.Key);
            _slots.Remove(oldest);
            _bytes -= oldest.Bytes;
            GL.DeleteTexture(oldest.Handle);
        }

        int handle = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, handle);
        var (storage, format) = TextureFormat(image.Channels);
        if (_immutableStorage ??= useImmutableStorage && SupportsImmutableStorage())
        {
            GL.TexStorage2D(TextureTarget2d.Texture2D, 1, (SizedInternalFormat)storage, image.Width, image.Height);
        }
        else
        {
            GL.TexImage2D(TextureTarget.Texture2D, 0, storage, image.Width, image.Height, 0,
                format, PixelType.UnsignedByte, nint.Zero);
        }
        if (image.Channels is DecorationChannels.Gray or DecorationChannels.GrayAlpha)
        {
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleG, (int)All.Red);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleB, (int)All.Red);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleA,
                (int)(image.Channels == DecorationChannels.Gray ? All.One : All.Green));
        }
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)(smoothing ? TextureMinFilter.Linear : TextureMinFilter.Nearest));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)(smoothing ? TextureMagFilter.Linear : TextureMagFilter.Nearest));
        _bytes += bytes;
        var allocated = new Slot(handle, image.Width, image.Height, image.Channels, smoothing, bytes);
        _slots.Add(allocated);
        return allocated;
    }

    internal static bool SupportsImmutableStorage()
    {
        GL.GetInteger(GetPName.MajorVersion, out int major);
        GL.GetInteger(GetPName.MinorVersion, out int minor);
        if (major > 4 || (major == 4 && minor >= 2))
            return true;

        GL.GetInteger(GetPName.NumExtensions, out int count);
        for (int i = 0; i < count; i++)
            if (GL.GetString(StringNameIndexed.Extensions, i) == "GL_ARB_texture_storage")
                return true;
        return false;
    }

    private bool IsIdle(Slot slot) => slot.LastUsed <= _frame - 3 && slot.NeededAt < _time;

    private static (PixelInternalFormat Storage, PixelFormat Transfer) TextureFormat(DecorationChannels channels) =>
        channels switch
        {
            DecorationChannels.Gray => (PixelInternalFormat.R8, PixelFormat.Red),
            DecorationChannels.GrayAlpha => (PixelInternalFormat.Rg8, PixelFormat.Rg),
            DecorationChannels.Rgb => (PixelInternalFormat.Rgb8, PixelFormat.Rgb),
            _ => (PixelInternalFormat.Rgba8, PixelFormat.Rgba),
        };

    private static unsafe void Upload(Slot slot, in DecorationImage image, int rows)
    {
        GL.BindTexture(TextureTarget.Texture2D, slot.Handle);
        int start = slot.UploadedRows;
        int rowBytes = image.RowBytes;
        int alignment = image.Channels == DecorationChannels.Rgba ? 4 : GL.GetInteger(GetPName.UnpackAlignment);
        if (alignment != 4)
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        try
        {
            fixed (byte* pixels = image.Pixels)
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, start, image.Width, rows,
                    TextureFormat(image.Channels).Transfer, PixelType.UnsignedByte, (nint)(pixels + start * rowBytes));
        }
        finally
        {
            if (alignment != 4)
                GL.PixelStore(PixelStoreParameter.UnpackAlignment, alignment);
        }
        slot.UploadedRows += rows;
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Dispose()
    {
        foreach (Slot slot in _slots)
            GL.DeleteTexture(slot.Handle);
    }
}

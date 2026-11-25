using System.Buffers;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

/// <summary>A visual transform around the baked tile origin, in renderer world units and radians.</summary>
public readonly record struct FloorRenderTransform(Vector2 Offset, Vector2 Scale, float Rotation);

internal readonly record struct FloorAppearance(Vector4 Color, float Opacity, float Glow = 0);
internal readonly record struct FloorDecorationDraw(Vector4 Color, float Opacity, float IconAngle, bool IconFlipped,
    int Mask, int MaskMode);

internal sealed unsafe class OfficialFloorRenderer(GlMeshUploader uploader, int textureSlots,
    OfficialFloorRenderer? sharedPipeline = null, bool? useStreamTexture2D = null) : IDisposable
{
    private const int FloorsPerRow = 256;
    private const int TexelsPerFloor = 4;
    private const int RowWidth = FloorsPerRow * TexelsPerFloor;
    private const string ShaderName = "Unity.ADOFAI_FloorMesh.2_glsl";
    private const string DefaultSlotCount = "#define FLOOR_TEXTURE_SLOTS 16";

    [StructLayout(LayoutKind.Sequential)]
    private struct FloorData
    {
        public Vector4 Color;
        public Vector4 CenterRotation;
        public Vector4 Scale;
        public Vector4 VariantOffset; // Variant in X, animated offset in YZ.
    }

    private readonly record struct TextureBinding(int Handle, float AspectRatio);
    private sealed record Batch(int IndexStart, int IndexCount, TextureBinding[] Textures);

    private readonly Dictionary<FloorTexture, TextureBinding> _textureCache = [];
    private readonly FloorStreamGpu _stream = new(
        sharedPipeline?._stream.UsesTexture2D ?? useStreamTexture2D ?? FloorStreamGpu.DefaultUseTexture2D);
    private FloorGeometry? _geometry;
    private GlMeshHandle? _meshHandle;
    private Batch[] _batches = [];
    private int[] _activeFloorsPerBatch = [];
    private FloorData[] _floorData = [];
    private int _firstDirty = int.MaxValue;
    private int _lastDirty = -1;
    private int _dataTexture;
    private int _dataRows;
    private int _program;
    private int _viewProjection;
    private int _cameraOrigin;
    private int _screenParams;
    private int _textureRatio;
    private int _forceIconOutline;
    private int _streamFloors;
    private int _decorationColor;
    private int _decorationParams;
    private int _decorationMaskMode;
    private FloorTrackState[]? _materialStates;
    private int _materialRevision;
    private readonly List<(int Start, int End)> _visibleIndexRanges = [];
    private int[] _drawCounts = [];
    private nint[] _drawOffsets = [];
    public int[]? LevelSelection { get; private set; }

    public int BatchCount => _batches.Length;
    public int FloorCount => _geometry?.Centers.Length ?? 0;
    public int TextureSlots => textureSlots;
    public bool HasGeometry => _geometry is not null;
    public bool UsesStream => _geometry?.Stream is not null;
    public int DrawCalls { get; private set; }
    public long DrawnTriangles { get; private set; }
    public int TriangleCount => (_geometry?.Stream?.IndexCount ?? _meshHandle?.IndexCount ?? 0) / 3;
    public long GeometryBytes => _meshHandle is { } handle ? handle.VertexBytes + handle.IndexBytes : 0;

    public bool UsesLevelStream(FloorShapeLibrary shapes) =>
        _geometry is { IsLevelStream: true, Stream: { } stream } && ReferenceEquals(stream.Shapes, shapes);

    public void SetLevelSelection(int[] floors) => LevelSelection = floors;

    public void PrepareLevelDraw(in Matrix4 viewProjection, Vector2 cameraOrigin, int viewportWidth, int viewportHeight)
    {
        if (LevelSelection is not { } selected)
            return;

        FloorGeometry geometry = _geometry!;
        _visibleIndexRanges.Clear();
        Vector2 halfViewport = new(viewportWidth * .5f, viewportHeight * .5f);
        Vector2 xAxis = new(viewProjection.Row0.X * halfViewport.X, viewProjection.Row1.X * halfViewport.X);
        Vector2 yAxis = new(viewProjection.Row0.Y * halfViewport.Y, viewProjection.Row1.Y * halfViewport.Y);
        Vector2 absXAxis = Vector2.Abs(xAxis), absYAxis = Vector2.Abs(yAxis);
        Vector2 pixelsPerUnit = new(xAxis.Length, yAxis.Length);
        for (int index = selected.Length - 1; index >= 0; index--)
        {
            int floor = selected[index];
            ref readonly FloorData data = ref _floorData[floor];
            if (data.Scale.Z == 0f)
                continue;
            float radius = geometry.GetVariants(floor)[(int)data.VariantOffset.X].Radius *
                MathF.Max(MathF.Abs(data.Scale.X), MathF.Abs(data.Scale.Y));
            Vector2 relative = (data.CenterRotation.Xy - cameraOrigin) + new Vector2(data.VariantOffset.Y, data.VariantOffset.Z);
            Vector2 center = new(Vector2.Dot(relative, xAxis), Vector2.Dot(relative, yAxis));
            // Allow for CPU/GPU floating-point differences near a rounding boundary.
            Vector2 absRelative = Vector2.Abs(relative);
            Vector2 margin = new(
                .01f + 1e-6f * Vector2.Dot(absRelative, absXAxis),
                .01f + 1e-6f * Vector2.Dot(absRelative, absYAxis));
            Vector2 extent = pixelsPerUnit * radius + margin;
            if (OutsideOrCollapses(center.X, extent.X, halfViewport.X) ||
                OutsideOrCollapses(center.Y, extent.Y, halfViewport.Y))
                continue;

            int start = geometry.IndexStarts[floor];
            int end = floor == 0 ? geometry.IndexCount : geometry.IndexStarts[floor - 1];
            if (end <= start)
                continue;
            if (_visibleIndexRanges.Count > 0 && _visibleIndexRanges[^1].End == start)
                _visibleIndexRanges[^1] = (_visibleIndexRanges[^1].Start, end);
            else
                _visibleIndexRanges.Add((start, end));
        }
    }

    private static bool OutsideOrCollapses(float center, float extent, float halfViewport) =>
        MathF.Abs(center) - extent > halfViewport + .5f ||
        // The vertex shader snaps to pixels. All vertices in a single snap cell
        // on either axis produce zero-area triangles.
        MathF.Abs(center - MathF.Round(center)) + extent < .5f;

    public static int GetTextureCapacity()
    {
        GL.GetInteger(GetPName.MaxTextureImageUnits, out int fragmentUnits);
        GL.GetInteger(GetPName.MaxCombinedTextureImageUnits, out int combinedUnits);
        return Math.Min(32, Math.Min(fragmentUnits, combinedUnits - 3));
    }

    /// <summary>Uploads a complete scene; takes ownership only after successful submission.</summary>
    public void SetGeometry(FloorGeometry geometry, bool preserveTransforms = false, bool releaseCpuMesh = false,
        IReadOnlyDictionary<FloorTexture, ImageDecoder.RgbaImage>? preparedTextures = null)
    {
        OfficialFloorRenderer pipeline = sharedPipeline ?? this;
        if (pipeline._program == 0)
        {
            pipeline.ReloadShader();
        }

        Mesh mesh = geometry.Mesh;
        int dataTexture = 0;
        FloorData[] data = [];
        bool reuseDataTexture = geometry.Stream is not null && _geometry?.Stream is not null && _dataTexture != 0;
        try
        {
            Batch[] batches = new Batch[geometry.Batches.Count];
            for (int i = 0; i < batches.Length; i++)
            {
                FloorTextureBatch source = geometry.Batches[i];
                batches[i] = new Batch(source.IndexStart, source.IndexCount,
                    Array.ConvertAll(source.Textures, texture => pipeline.LoadTexture(texture, preparedTextures)));
            }

            GlMeshHandle? handle = null;
            if (geometry.Stream is null && mesh.IndexCount > 0)
            {
                uploader.Upload(mesh);
                if (!uploader.TryGetHandle(mesh, out handle) || !handle.IsValid)
                {
                    throw new InvalidOperationException("Floor mesh upload failed.");
                }
            }

            int rows = (geometry.Centers.Length + FloorsPerRow - 1) / FloorsPerRow;
            if (rows > 0)
            {
                data = ArrayPool<FloorData>.Shared.Rent(rows * FloorsPerRow);
                data.AsSpan(0, rows * FloorsPerRow).Clear();
            }
            int[] activeFloorsPerBatch = new int[batches.Length];
            bool copyTransforms = preserveTransforms && _geometry is { } previous
                && previous.Centers.AsSpan().SequenceEqual(geometry.Centers);
            for (int i = 0; i < geometry.Centers.Length; i++)
            {
                Vector2 center = geometry.Centers[i];
                data[i].Color = Vector4.One;
                data[i].CenterRotation = copyTransforms
                    ? _floorData[i].CenterRotation
                    : new Vector4(center.X, center.Y, 1f, 0f);
                Vector2 scale = copyTransforms ? _floorData[i].Scale.Xy : Vector2.One;
                data[i].Scale = new Vector4(scale.X, scale.Y, 1f, 0f);
                if (copyTransforms)
                {
                    data[i].VariantOffset.Y = _floorData[i].VariantOffset.Y;
                    data[i].VariantOffset.Z = _floorData[i].VariantOffset.Z;
                }
                int batch = geometry.GetVariants(i)[0].Batch;
                if (batch >= 0)
                {
                    activeFloorsPerBatch[batch]++;
                }
            }

            if (rows > 0)
            {
                GL.GetInteger(GetPName.MaxTextureSize, out int maxSize);
                if (rows > maxSize || RowWidth > maxSize)
                {
                    throw new InvalidOperationException("Floor data exceeds the maximum texture size.");
                }

                dataTexture = reuseDataTexture ? _dataTexture : GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, dataTexture);
                try
                {
                    if (!reuseDataTexture)
                    {
                        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                    }
                    if (!reuseDataTexture || rows > _dataRows)
                    {
                        fixed (FloorData* pixels = data)
                        {
                            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, RowWidth, rows, 0,
                                PixelFormat.Rgba, PixelType.Float, geometry.Stream is null ? (nint)pixels : 0);
                        }
                    }
                }
                finally
                {
                    GL.BindTexture(TextureTarget.Texture2D, 0);
                }
            }

            if (geometry.Stream is { IndexCount: > 0 } stream)
            {
                _stream.Upload(stream);
            }
            geometry.Stream?.Dispose();
            ClearGeometry(keepDataTexture: reuseDataTexture && dataTexture != 0);
            _geometry = geometry;
            _meshHandle = handle;
            _batches = batches;
            _activeFloorsPerBatch = activeFloorsPerBatch;
            _floorData = data;
            _dataTexture = dataTexture;
            _dataRows = Math.Max(rows, _dataRows);
            data = [];
            if (geometry.Stream is not null && geometry.Centers.Length > 0)
            {
                _firstDirty = 0;
                _lastDirty = geometry.Centers.Length - 1;
            }
            if (releaseCpuMesh)
            {
                mesh.Dispose();
            }
        }
        catch
        {
            if (data.Length > 0)
                ArrayPool<FloorData>.Shared.Return(data);
            uploader.Release(mesh);
            if (dataTexture != 0 && !reuseDataTexture)
            {
                GL.DeleteTexture(dataTexture);
            }

            throw;
        }
    }

    public void SetMaterials(FloorTrackState[] states, int revision, TrackStyleInfo? styleOverride, int[]? floorIndices)
    {
        if (ReferenceEquals(states, _materialStates) && revision == _materialRevision)
        {
            return;
        }
        FloorGeometry geometry = _geometry ?? throw new InvalidOperationException("No floor geometry is loaded.");
        for (int i = 0; i < geometry.Centers.Length; i++)
        {
            FloorMaterial material = states[floorIndices?[i] ?? i].Material;
            if (styleOverride is not null)
            {
                material = material with { Style = styleOverride };
            }

            ReadOnlySpan<FloorVariant> variants = geometry.GetVariants(i);
            int previous = (int)_floorData[i].VariantOffset.X;
            if (variants[previous].Material == material)
            {
                continue;
            }

            int selected = 0;
            while (selected < variants.Length && variants[selected].Material != material)
            {
                selected++;
            }

            if (selected == variants.Length)
            {
                throw new InvalidOperationException($"Missing preloaded variant for floor {i}.");
            }

            int fromBatch = variants[previous].Batch;
            int toBatch = variants[selected].Batch;
            if (fromBatch >= 0)
            {
                _activeFloorsPerBatch[fromBatch]--;
            }

            if (toBatch >= 0)
            {
                _activeFloorsPerBatch[toBatch]++;
            }

            _floorData[i].VariantOffset.X = selected;
            MarkDirty(i);
        }
        _materialStates = states;
        _materialRevision = revision;
    }

    public ReadOnlySpan<System.Numerics.Vector2> GetOutline(int floor) =>
        _geometry!.GetVariants(floor)[(int)_floorData[floor].VariantOffset.X].Outline;

    public void SetFloorVisual(int index, FloorRenderTransform transform, FloorAppearance appearance)
    {
        Vector2 center = _geometry!.Centers[index];
        (float sin, float cos) = MathF.SinCos(transform.Rotation);
        Vector4 centerRotation = new(center.X, center.Y, cos, sin);
        Vector4 scale = new(transform.Scale.X, transform.Scale.Y, appearance.Opacity, appearance.Glow);
        ref FloorData floor = ref _floorData[index];
        if (floor.CenterRotation != centerRotation || floor.Scale != scale || floor.Color != appearance.Color
            || floor.VariantOffset.Y != transform.Offset.X || floor.VariantOffset.Z != transform.Offset.Y)
        {
            floor.CenterRotation = centerRotation;
            floor.Scale = scale;
            floor.Color = appearance.Color;
            floor.VariantOffset.Y = transform.Offset.X;
            floor.VariantOffset.Z = transform.Offset.Y;
            MarkDirty(index);
        }
    }

    public float FloorOpacity(int floor) => _floorData[floor].Scale.Z;

    public bool TryToFloorLocal(int floor, Vector2 cameraOrigin, Vector2 cameraRelativePoint, out Vector2 local)
    {
        ref FloorData data = ref _floorData[floor];
        Vector2 scale = data.Scale.Xy;
        if (scale.X == 0f || scale.Y == 0f)
        {
            local = default;
            return false;
        }

        float cos = data.CenterRotation.Z;
        float sin = data.CenterRotation.W;
        Vector2 center = (data.CenterRotation.Xy - cameraOrigin) + new Vector2(data.VariantOffset.Y, data.VariantOffset.Z);
        Vector2 relative = cameraRelativePoint - center;
        local = new Vector2(cos * relative.X + sin * relative.Y, -sin * relative.X + cos * relative.Y) / scale;
        return true;
    }

    public void ProjectFloorOutline(int floor, Vector2 cameraOrigin, ReadOnlySpan<System.Numerics.Vector2> outline,
        Span<Vector2> relativeOutline)
    {
        ref FloorData data = ref _floorData[floor];
        Vector2 center = (data.CenterRotation.Xy - cameraOrigin) + new Vector2(data.VariantOffset.Y, data.VariantOffset.Z);
        float cos = data.CenterRotation.Z;
        float sin = data.CenterRotation.W;
        for (int i = 0; i < outline.Length; i++)
        {
            float x = outline[i].X * data.Scale.X;
            float y = outline[i].Y * data.Scale.Y;
            relativeOutline[i] = center + new Vector2(cos * x - sin * y, sin * x + cos * y);
        }
    }

    public int IndexAfterFloor(int localFloor)
    {
        if (_batches.Length == 0 || _geometry?.IndexStarts is not { } offsets)
            return 0;
        if (localFloor <= 0)
            return _batches[^1].IndexStart + _batches[^1].IndexCount;
        return localFloor > offsets.Length ? 0 : offsets[localFloor - 1];
    }

    public void Draw(in Matrix4 viewProjection, Vector2 cameraOrigin, int viewportWidth, int viewportHeight,
        bool forceIconOutline, int fromIndex = 0, int endIndex = int.MaxValue)
        => DrawCore(viewProjection, cameraOrigin, viewportWidth, viewportHeight, forceIconOutline, fromIndex, endIndex, null);

    public void DrawDecoration(in Matrix4 clip, int width, int height, bool iconOutline, FloorDecorationDraw appearance)
        => DrawCore(clip, Vector2.Zero, width, height, iconOutline, 0, int.MaxValue, appearance);

    private void DrawCore(in Matrix4 viewProjection, Vector2 cameraOrigin, int viewportWidth, int viewportHeight,
        bool forceIconOutline, int fromIndex, int endIndex, FloorDecorationDraw? decoration)
    {
        DrawCalls = 0;
        DrawnTriangles = 0;
        bool streamed = _geometry?.Stream is not null;
        if ((!streamed && _meshHandle is null) || _batches.Length == 0 ||
            (LevelSelection is not null && _visibleIndexRanges.Count == 0))
        {
            return;
        }

        OfficialFloorRenderer pipeline = sharedPipeline ?? this;
        GL.UseProgram(pipeline._program);
        Matrix4 projection = viewProjection;
        if (!decoration.HasValue)
            projection.Row3 = new Vector4(0f, 0f, projection.Row3.Z, projection.Row3.W);
        // Submit the array from its queried location rather than deriving individual locations.
        GL.Uniform4(pipeline._viewProjection, 4, (float*)&projection);
        GL.Uniform2(pipeline._cameraOrigin, cameraOrigin);
        GL.Uniform4(pipeline._screenParams, (float)viewportWidth, (float)viewportHeight, 1f, 1f);
        GL.Uniform1(pipeline._forceIconOutline, forceIconOutline ? 1 : 0);
        GL.Uniform1(pipeline._streamFloors, streamed ? 1 : 0);
        GL.Uniform1(pipeline._decorationMaskMode, decoration?.MaskMode ?? 0);
        if (decoration is { } visual)
        {
            GL.Uniform4(pipeline._decorationColor, visual.Color);
            GL.Uniform4(pipeline._decorationParams, visual.Opacity, visual.IconAngle, visual.IconFlipped ? 1f : 0f, 1f);
            if (visual.Mask != 0)
            {
                GL.ActiveTexture(TextureUnit.Texture0 + textureSlots - 1);
                GL.BindTexture(TextureTarget.Texture2D, visual.Mask);
            }
        }
        else
        {
            GL.Uniform4(pipeline._decorationParams, Vector4.Zero);
        }
        GL.ActiveTexture(TextureUnit.Texture0 + textureSlots);
        GL.BindTexture(TextureTarget.Texture2D, _dataTexture);
        UploadDirtyRows();
        if (streamed || _stream.UsesTexture2D)
        {
            // The shader still declares both samplers when drawing an expanded mesh.
            _stream.Bind(textureSlots + 1, streamed ? 0 : _dataTexture);
        }
        if (streamed)
        {
            _stream.BindVertexArray();
        }
        else
        {
            GL.BindVertexArray(_meshHandle!.Vao);
        }

        int usedSlots = 0;
        float* ratios = stackalloc float[textureSlots];
        for (int batchIndex = 0; batchIndex < _batches.Length; batchIndex++)
        {
            if (_activeFloorsPerBatch[batchIndex] == 0)
            {
                continue;
            }

            Batch batch = _batches[batchIndex];
            int start = Math.Max(fromIndex, batch.IndexStart);
            int count = Math.Min(endIndex, batch.IndexStart + batch.IndexCount) - start;
            if (count <= 0)
                continue;
            for (int i = 0; i < batch.Textures.Length; i++)
            {
                GL.ActiveTexture(TextureUnit.Texture0 + i);
                GL.BindTexture(TextureTarget.Texture2D, batch.Textures[i].Handle);
                ratios[i] = batch.Textures[i].AspectRatio;
            }
            GL.Uniform1(pipeline._textureRatio, batch.Textures.Length, ratios);

            usedSlots = Math.Max(usedSlots, batch.Textures.Length);
            if (LevelSelection is not null)
            {
                count = DrawLevelRanges(start, start + count);
                if (count == 0)
                    continue;
            }
            else
            {
                GL.DrawElements(PrimitiveType.Triangles, count, DrawElementsType.UnsignedInt,
                    (nint)start * sizeof(uint));
            }
            DrawCalls++;
            DrawnTriangles += count / 3;
        }

        GL.BindVertexArray(0);
        if (streamed || _stream.UsesTexture2D)
        {
            _stream.Unbind(textureSlots + 1);
        }
        for (int i = 0; i < usedSlots; i++)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + i);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        GL.ActiveTexture(TextureUnit.Texture0 + textureSlots);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        if (decoration is { Mask: not 0 })
        {
            GL.ActiveTexture(TextureUnit.Texture0 + textureSlots - 1);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.UseProgram(0);
    }

    private int DrawLevelRanges(int start, int end)
    {
        if (_drawCounts.Length < _visibleIndexRanges.Count)
        {
            int capacity = Math.Max(_visibleIndexRanges.Count, _drawCounts.Length * 2);
            _drawCounts = new int[capacity];
            _drawOffsets = new nint[capacity];
        }

        int draws = 0, indices = 0;
        foreach ((int first, int last) in _visibleIndexRanges)
        {
            if (first >= end)
                break;
            int offset = Math.Max(start, first);
            int count = Math.Min(end, last) - offset;
            if (count <= 0)
                continue;
            _drawCounts[draws] = count;
            _drawOffsets[draws++] = (nint)offset * sizeof(uint);
            indices += count;
        }
        if (draws == 1)
            GL.DrawElements(PrimitiveType.Triangles, _drawCounts[0], DrawElementsType.UnsignedInt, _drawOffsets[0]);
        else if (draws > 1)
        {
            fixed (int* counts = _drawCounts)
            fixed (nint* offsets = _drawOffsets)
                GL.MultiDrawElements(PrimitiveType.Triangles, counts, DrawElementsType.UnsignedInt, (nint)offsets, draws);
        }
        return indices;
    }

    private void MarkDirty(int floor)
    {
        _firstDirty = Math.Min(_firstDirty, floor);
        _lastDirty = Math.Max(_lastDirty, floor);
    }

    private void UploadDirtyRows()
    {
        if (_lastDirty < 0)
        {
            return;
        }

        int firstRow = _firstDirty / FloorsPerRow;
        int lastRow = _lastDirty / FloorsPerRow;
        fixed (FloorData* pixels = &_floorData[firstRow * FloorsPerRow])
        {
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, firstRow, RowWidth, lastRow - firstRow + 1,
                PixelFormat.Rgba, PixelType.Float, (nint)pixels);
        }

        _firstDirty = int.MaxValue;
        _lastDirty = -1;
    }

    public void ClearGeometry(bool keepDataTexture = false)
    {
        DrawCalls = 0;
        DrawnTriangles = 0;
        if (_geometry is { } geometry)
        {
            uploader.Release(geometry.Mesh);
            geometry.Dispose();
            _geometry = null;
        }

        if (_dataTexture != 0 && !keepDataTexture)
        {
            GL.DeleteTexture(_dataTexture);
            _dataTexture = 0;
            _dataRows = 0;
        }

        _meshHandle = null;
        LevelSelection = null;
        _visibleIndexRanges.Clear();
        _batches = [];
        _activeFloorsPerBatch = [];
        if (_floorData.Length > 0)
            ArrayPool<FloorData>.Shared.Return(_floorData);
        _floorData = [];
        _materialStates = null;
        _firstDirty = int.MaxValue;
        _lastDirty = -1;
    }

    internal static IReadOnlyDictionary<FloorTexture, ImageDecoder.RgbaImage>? PrepareBuiltInTextures(
        FloorGeometry? geometry, CancellationToken token)
    {
        if (geometry is null)
            return null;

        const long byteLimit = 64L * 1024 * 1024;
        long bytes = 0;
        var prepared = new Dictionary<FloorTexture, ImageDecoder.RgbaImage>();
        foreach (var texture in geometry.Batches.SelectMany(batch => batch.Textures).Distinct())
        {
            token.ThrowIfCancellationRequested();
            if (texture.External)
                continue;
            var image = DecodeBuiltInTexture(texture);
            if (texture.FlipVertically)
                ImageDecoder.FlipVertically(image.Pixels, image.Width, image.Height);
            prepared.Add(texture, image);
            bytes += image.Pixels.LongLength;
            if (bytes >= byteLimit)
                break;
        }
        return prepared;
    }

    private static ImageDecoder.RgbaImage DecodeBuiltInTexture(FloorTexture texture) =>
        ImageDecoder.Decode(EmbeddedAssets.Read(texture.Resource)
            ?? throw new FileNotFoundException("Floor texture is missing.", texture.Resource));

    private TextureBinding LoadTexture(FloorTexture texture,
        IReadOnlyDictionary<FloorTexture, ImageDecoder.RgbaImage>? preparedTextures = null)
    {
        if (_textureCache.TryGetValue(texture, out TextureBinding binding))
        {
            return binding;
        }

        if (preparedTextures?.TryGetValue(texture, out var image) == true)
            return UploadTexture(texture, image);

        if (texture.External)
        {
            try
            {
                image = ImageDecoder.Decode(File.ReadAllBytes(texture.Resource));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or SixLabors.ImageSharp.ImageFormatException)
            {
                if (texture.Optional)
                {
                    _textureCache.Add(texture, default);
                    return default;
                }

                image = new ImageDecoder.RgbaImage(1, 1, [255, 255, 255, 255]);
            }
        }
        else
        {
            image = DecodeBuiltInTexture(texture);
        }

        if (texture.FlipVertically)
        {
            ImageDecoder.FlipVertically(image.Pixels, image.Width, image.Height);
        }

        return UploadTexture(texture, image);
    }

    private TextureBinding UploadTexture(FloorTexture texture, ImageDecoder.RgbaImage image)
    {
        bool mipmaps = texture.Filter is FloorTextureFilter.Trilinear or FloorTextureFilter.BilinearMipmapped;
        int handle = ImageDecoder.Upload(image, generateMipmaps: mipmaps, repeat: texture.Wrap == FloorTextureWrap.Repeat);
        GL.BindTexture(TextureTarget.Texture2D, handle);
        if (texture.Wrap == FloorTextureWrap.Mirror)
        {
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.MirroredRepeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.MirroredRepeat);
        }
        if (texture.Filter == FloorTextureFilter.BilinearMipmapped)
        {
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapNearest);
        }
        else if (texture.Filter == FloorTextureFilter.Point)
        {
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        }
        GL.BindTexture(TextureTarget.Texture2D, 0);
        var binding = new TextureBinding(handle, (float)image.Width / image.Height);
        _textureCache.Add(texture, binding);
        return binding;
    }

    public void ClearLevel()
    {
        ClearGeometry();
        _stream.Dispose();
        foreach ((FloorTexture texture, TextureBinding binding) in _textureCache.Where(item => item.Key.External).ToArray())
        {
            GL.DeleteTexture(binding.Handle);
            _textureCache.Remove(texture);
        }
    }

    public void ReloadShader()
    {
        string vertex = ShaderLibrary.Load(ShaderName + ".vert");
        if (_stream.UsesTexture2D)
        {
            vertex = vertex.Insert(vertex.IndexOf('\n') + 1,
                $"#define FLOOR_STREAM_TEXTURE_WIDTH {FloorStreamGpu.TextureRowWidth}\n");
        }
        string fragment = ShaderLibrary.Load(ShaderName + ".frag").Replace(
            DefaultSlotCount, $"#define FLOOR_TEXTURE_SLOTS {textureSlots}", StringComparison.Ordinal);
        int program = LinkProgram(vertex, fragment);

        GL.UseProgram(program);
        Matrix4 model = Matrix4.Identity;
        GL.Uniform4(GL.GetUniformLocation(program, "uModel[0]"), 4, (float*)&model);
        GL.Uniform4(GL.GetUniformLocation(program, "_TileTex_ST"), 0f, 0f, 0f, 0f);
        GL.Uniform4(GL.GetUniformLocation(program, "_Tint"), Vector4.One);
        GL.Uniform4(GL.GetUniformLocation(program, "_IconColor"), Vector4.One);
        GL.Uniform1(GL.GetUniformLocation(program, "_Active"), 1);
        GL.Uniform1(GL.GetUniformLocation(program, "_AbsoluteInset"), 1);
        GL.Uniform1(GL.GetUniformLocation(program, "_PerlinScale"), 0.36f);
        GL.Uniform1(GL.GetUniformLocation(program, "_PerlinStrength"), 1.12f);
        GL.Uniform1(GL.GetUniformLocation(program, "uFloorData"), textureSlots);
        GL.Uniform1(GL.GetUniformLocation(program, "uFloorInstances"), textureSlots + 1);
        GL.Uniform1(GL.GetUniformLocation(program, "uFloorShapes"), textureSlots + 2);
        for (int i = 0; i < textureSlots; i++)
        {
            GL.Uniform1(GL.GetUniformLocation(program, $"uTextures[{i}]"), i);
        }

        _viewProjection = GL.GetUniformLocation(program, "uViewProjection[0]");
        _cameraOrigin = GL.GetUniformLocation(program, "uCameraOrigin");
        _screenParams = GL.GetUniformLocation(program, "uScreenParams");
        _textureRatio = GL.GetUniformLocation(program, "uTextureRatio[0]");
        _forceIconOutline = GL.GetUniformLocation(program, "uForceIconOutline");
        _streamFloors = GL.GetUniformLocation(program, "uStreamFloors");
        _decorationColor = GL.GetUniformLocation(program, "uDecorationColor");
        _decorationParams = GL.GetUniformLocation(program, "uDecorationParams");
        _decorationMaskMode = GL.GetUniformLocation(program, "uDecorationMaskMode");
        GL.UseProgram(0);
        if (_program != 0)
        {
            GL.DeleteProgram(_program);
        }

        _program = program;
    }

    private static int LinkProgram(string vertexSource, string fragmentSource)
    {
        int vertex = CompileShader(ShaderType.VertexShader, vertexSource);
        int fragment = 0;
        int program = 0;
        try
        {
            fragment = CompileShader(ShaderType.FragmentShader, fragmentSource);
            program = GL.CreateProgram();
            GL.AttachShader(program, vertex);
            GL.AttachShader(program, fragment);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int status);
            if (status == 0)
            {
                throw new InvalidOperationException($"Floor shader link failed: {GL.GetProgramInfoLog(program)}");
            }

            GL.DetachShader(program, vertex);
            GL.DetachShader(program, fragment);
            return program;
        }
        catch
        {
            if (program != 0)
            {
                GL.DeleteProgram(program);
            }

            throw;
        }
        finally
        {
            GL.DeleteShader(vertex);
            if (fragment != 0)
            {
                GL.DeleteShader(fragment);
            }
        }
    }

    private static int CompileShader(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, 1, [source], [System.Text.Encoding.UTF8.GetByteCount(source)]);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
        if (compiled == 0)
        {
            string log = GL.GetShaderInfoLog(shader);
            GL.DeleteShader(shader);
            throw new InvalidOperationException($"Floor {type} compile failed: {log}");
        }

        return shader;
    }

    public void Dispose()
    {
        ClearGeometry();
        _stream.Dispose();
        foreach (TextureBinding texture in _textureCache.Values)
        {
            GL.DeleteTexture(texture.Handle);
        }

        _textureCache.Clear();
        if (_program != 0)
        {
            GL.DeleteProgram(_program);
            _program = 0;
        }
    }
}

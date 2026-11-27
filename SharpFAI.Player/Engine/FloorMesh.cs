using System.Drawing;
using System.Globalization;
using System.Numerics;

namespace SharpFAI.Player.Engine;

/// <summary>
/// Builds tile geometry: the outer outline, its inset rings, the shadow band and the stitched index
/// buffer. Coordinates are in metres and <see cref="Scale"/> (default 100) expands them on output to
/// match the editor's pixel-like units.
/// </summary>
public sealed class FloorMesh
{
    // Geodesic constants ----
    private const float Angle5 = 0.08726646f;
    private const float Angle30 = MathF.PI / 6f;
    private const float Angle45 = MathF.PI / 4f;
    private const float Angle90 = MathF.PI / 2f;
    private const float Angle120 = MathF.PI * 2f / 3f;
    private const float AngleEpsilon = 0.0001f;
    private const float RayEpsilon = 0.01f;
    private const float ShadowWidth = 0.11f;
    private const float UTurnOffset = 2f / 3f;
    private const int DefaultCurvaturePoints = 40;

    // Standard track style sizes
    private const float StandardTileWidth = 0.4125f + 0.0375f;
    private const float StandardTileLength = 0.75f + 0.03f;

    /// <summary>Connection value that means "no inset vertex to stitch to".</summary>
    private const int NoConnection = 10001;

    // Shape parameters ----
    private readonly bool isSprite;
    private readonly bool isHexagon;
    private readonly bool useFInset2;

    // Intermediate state ----
    private float angle0;
    private float angle1;
    private float width;
    private float length;
    private int curvaturePoints;

    private float shortAngle;
    private float cornerCenterRadius;
    private float cornerCenterApothem;
    private float curvatureSliceAngle;
    private float angleDifference;
    private float insetDistance0;
    private float insetDistance1;
    private float insetDistance2;
    private float diamondShadowOuterVertexDistance;

    private int cornerPointIndex;
    private bool pathOverlaps;
    private bool cornerRaysIntersectBeforeSpikes;

    private Vector2 origin;
    private Vector2 bisectorIntersection;
    private Vector2 cornerCircleCenter;
    private Vector2[]? overlappingPoints;

    // Derived points shared by the body and the shadows
    private Vector2 vector;
    private Vector2 vector3;
    private Vector2 startMore;
    private Vector2 startLess;
    private Vector2 endMore;
    private Vector2 endLess;
    private Vector2 vector2;
    private Vector2 vector4;
    private Vector2 ccwIntersection;
    private Vector2 cwIntersection;
    private float num2;
    private float num3;
    private float num4;
    private float num5;
    private float cornerLerp;

    // Polygon buffers ----
    private readonly List<Vector2> mainPolygon0 = [];
    private readonly List<int> mainPolygon0Conn = [];
    private List<Vector2> mainPolygon1 = [];
    private readonly List<int> mainPolygon1Conn = [];
    private List<Vector2> mainPolygon2 = [];
    private readonly List<int> mainPolygon2Conn = [];
    private readonly List<Vector2> mainPolygon3 = [];

    private readonly List<Vector2> cwShadowPolygon0 = [];
    private readonly List<int> cwShadowPolygonConn = [];
    private readonly List<Vector2> cwShadowPolygon1 = [];
    private readonly List<Vector2> ccwShadowPolygon0 = [];
    private readonly List<int> ccwShadowPolygonConn = [];
    private readonly List<Vector2> ccwShadowPolygon1 = [];

    private readonly List<Vector2> cwCornerPoints = [];
    private readonly List<Vector2> ccwCornerPoints = [];

    // Output buffers ----
    private readonly List<Vector3> vertices = [];
    private readonly List<Color> colors = [];
    private readonly List<int> indices = [];
    private readonly List<Vector2> uv0 = [];
    private readonly List<Vector2> uv1 = [];

    // Cache ----
    private static readonly Dictionary<string, FloorMeshResult> Cache = [];
    private static readonly Lock CacheLock = new();

    /// <summary>
    /// Upper bound on cached shapes. A level only has a handful of distinct tile shapes, so this is
    /// purely a guard: if a key ever fails to stabilise, the cache is dropped instead of growing until
    /// the process runs out of memory.
    /// </summary>
    private const int CacheLimit = 4096;

    /// <summary>Describe one tile mesh.</summary>
    /// <param name="angle0Degrees">Entry angle in degrees.</param>
    /// <param name="angle1Degrees">Exit angle in degrees.</param>
    /// <param name="width">Tile width in metres.</param>
    /// <param name="curvaturePoints">Arc sample count.</param>
    /// <param name="isSprite">Whether this is a sprite tile.</param>
    /// <param name="isHexagon">Whether this is a midspin hexagon tile.</param>
    /// <param name="useFInset2">Use the alternate inset distance for the second ring.</param>
    /// <param name="scale">Output scale, 100 by default.</param>
    public FloorMesh(
        double angle0Degrees,
        double angle1Degrees,
        float width = StandardTileWidth,
        float length = StandardTileLength,
        int curvaturePoints = DefaultCurvaturePoints,
        bool isSprite = false,
        bool isHexagon = false,
        bool useFInset2 = false,
        float scale = 100f)
    {
        Angle0Degrees = angle0Degrees;
        Angle1Degrees = angle1Degrees;
        this.width = MathF.Max(0f, width);
        this.length = MathF.Max(length, this.width);
        this.curvaturePoints = Math.Max(curvaturePoints, 3);
        this.isSprite = isSprite;
        this.isHexagon = isHexagon;
        this.useFInset2 = useFInset2;
        Scale = scale;
    }

    /// <summary>Entry angle in degrees.</summary>
    public double Angle0Degrees { get; }

    /// <summary>Exit angle in degrees.</summary>
    public double Angle1Degrees { get; }

    /// <summary>Whether this is a sprite tile.</summary>
    public bool IsSprite => isSprite;

    /// <summary>Whether this is a midspin hexagon tile.</summary>
    public bool IsHexagon => isHexagon;

    /// <summary>Output scale from metres to pixel-like units.</summary>
    public float Scale { get; }

    /// <summary>
    /// Cache key. Angles, sizes and curvature identify a shape; the flags and the output scale are
    /// included so two different shapes can never share one cached mesh.
    /// </summary>
    /// <remarks>
    /// Angles are quantised before formatting. The geometry code normalises headings with doubles and
    /// the last bit of a result depends on how the CPU folds the arithmetic, so a raw
    /// <c>double.ToString()</c> can produce a distinct key per tile and blow the cache up to one entry
    /// per floor. Rounding to a ten-thousandth of a degree is far finer than any renderable detail and
    /// makes the key stable across machines.
    /// </remarks>
    public string CacheKey =>
        $"{Quantise(Angle0Degrees)},{Quantise(Angle1Degrees)},{width},{length},{curvaturePoints}," +
        $"{isSprite},{isHexagon},{useFInset2},{Scale}";

    /// <summary>Round an angle to a stable, culture-independent string.</summary>
    private static string Quantise(double degrees)
    {
        if (double.IsNaN(degrees) || double.IsInfinity(degrees))
        {
            return "0";
        }

        // Fold into [0, 360) so 0 and 360 share one entry, then round.
        double wrapped = degrees % 360.0;

        if (wrapped < 0)
        {
            wrapped += 360.0;
        }

        return Math.Round(wrapped, 4).ToString("0.####", CultureInfo.InvariantCulture);
    }

    /// <summary>Drop the shared mesh cache.</summary>
    public static void ClearCache()
    {
        lock (CacheLock)
        {
            Cache.Clear();
        }
    }

    /// <summary>Number of cached meshes.</summary>
    public static int CacheCount
    {
        get
        {
            lock (CacheLock)
            {
                return Cache.Count;
            }
        }
    }

    /// <summary>
    /// Build the mesh, reusing the cached one for an identical <see cref="CacheKey"/>.
    /// </summary>
    public FloorMeshResult Build()
    {
        string key = CacheKey;

        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out FloorMeshResult? cached))
            {
                return cached;
            }

            FloorMeshResult result = Generate();

            // Guard against an unstable key (see CacheLimit): a shape cache is only useful while the
            // number of distinct shapes stays small.
            if (Cache.Count >= CacheLimit)
            {
                Cache.Clear();
            }

            Cache[key] = result;
            return result;
        }
    }

    /// <summary>Generate without touching the cache.</summary>
    public FloorMeshResult Generate()
    {
        vertices.Clear();
        colors.Clear();
        indices.Clear();
        uv0.Clear();
        uv1.Clear();
        cwCornerPoints.Clear();
        ccwCornerPoints.Clear();

        if (isHexagon)
        {
            GenerateHexagon();
        }
        else if (isSprite)
        {
            GenerateSprite();
        }
        else
        {
            GetPositions(Angle0Degrees, Angle1Degrees, width, length, curvaturePoints);
            GenerateMesh();
        }

        return CreateResult();
    }

    private FloorMeshResult CreateResult()
    {
        int count = vertices.Count;
        Vector3[] outVertices = new Vector3[count];
        Vector2[] outUv1 = new Vector2[count];
        Vector2[] outCollider = new Vector2[mainPolygon0.Count];

        for (int i = 0; i < count; i++)
        {
            Vector3 v = vertices[i];
            outVertices[i] = new Vector3(v.X * Scale, v.Y * Scale, v.Z * Scale);

            // uv2 carries tile-local coordinates, so it scales alongside the positions
            if (i < uv1.Count)
            {
                Vector2 uv = uv1[i];
                outUv1[i] = new Vector2(uv.X * Scale, uv.Y * Scale);
            }
        }

        // The collider outline has to scale too; otherwise it stays at metre scale, 100x away from
        // the vertex positions, and every pick misses
        for (int i = 0; i < outCollider.Length; i++)
        {
            Vector2 p = mainPolygon0[i];
            outCollider[i] = new Vector2(p.X * Scale, p.Y * Scale);
        }

        return new FloorMeshResult
        {
            CacheKey = CacheKey,
            Vertices = outVertices,
            Colors = [.. colors],
            Indices = [.. indices],
            Uv0 = [.. uv0],
            Uv1 = outUv1,
            Collider = outCollider,
        };
    }

    // ------------------------------------------------------------------
    // ------------------------------------------------------------------
    private void GenerateHexagon()
    {
        Vector2[] inner = new Vector2[6];
        Vector2[] outerMinus = new Vector2[6];
        Vector2[] outerPlus = new Vector2[6];
        Vector2[] outerRing = new Vector2[6];
        int[] connections = new int[6];

        for (int i = 0; i < 6; i++)
        {
            inner[i] = GenerateNthVertex(i, 0f);
            outerMinus[i] = GenerateNthVertex(i, length);
            connections[i] = i;
            outerPlus[i] = GenerateNthVertex(i, length);
            outerRing[i] = GenerateNthVertex(i, length + ShadowWidth);
        }

        KnitTriangles([.. outerMinus], [.. connections], 6, 0);
        KnitTriangles([.. outerRing], [.. connections], 6, 12);

        List<Vector2> positions =
        [
            .. outerMinus,
            .. inner,
            .. outerPlus,
            .. outerRing,
        ];

        for (int i = 0; i < positions.Count; i++)
        {
            vertices.Add(new Vector3(positions[i], 0f));
            colors.Add(Color.White);

            // uv.y marks the ring: 1, 0, 1 for the body rings and 2 for the shadow band
            uv0.Add(new Vector2(0.5f, i switch
            {
                < 6 => 1f,
                < 12 => 0f,
                < 18 => 1f,
                _ => 2f,
            }));

            uv1.Add(positions[i]);
        }

        for (int i = 0; i < outerMinus.Length; i++)
        {
            mainPolygon0.Add(outerMinus[i]);
        }
    }

    // ------------------------------------------------------------------
    // sprite tile
    // ------------------------------------------------------------------
    private void GenerateSprite()
    {
        // 官方 sprite 网格是固定的 ±0.5 单位四边形，与砖块尺寸无关（`cacheKey` 对 sprite 恒为
        // "sprite"，所有 Gems 砖共用同一份网格），所以这里保持原样。
        // The game's sprite mesh is a fixed ±0.5 unit quad, independent of tile size (its `cacheKey`
        // is always "sprite", so every Gems tile shares one mesh).
        Vector2[] square =
        [
            new(0.5f, 0.5f),
            new(-0.5f, 0.5f),
            new(-0.5f, -0.5f),
            new(0.5f, -0.5f),
        ];

        foreach (Vector2 point in square)
        {
            vertices.Add(new Vector3(point, 0f));
            colors.Add(Color.White);

            // The game pins uv to (0.5, 0.5) for sprites: the tile texture is sampled at its centre
            // and the quad shape alone comes from uv2 (which is the vertex position). Keeping y at
            // 0.5 also keeps the shader out of its shadow branch, which needs uv.y > 1.
            uv0.Add(new Vector2(0.5f, 0.5f));
            uv1.Add(point);
            mainPolygon0.Add(point);
        }

        indices.AddRange([0, 2, 1, 0, 3, 2]);
    }

    // ------------------------------------------------------------------
    // body plus shadows
    // ------------------------------------------------------------------
    private void GenerateMesh()
    {
        List<List<Vector2>> bodyPolygons = [mainPolygon0, mainPolygon1, mainPolygon2, mainPolygon3];
        List<List<Vector2>> shadowPolygons =
            [cwShadowPolygon0, cwShadowPolygon1, ccwShadowPolygon0, ccwShadowPolygon1];

        // Vertices are concatenated as "four body layers, then four shadow polygons"
        foreach (List<Vector2> polygon in bodyPolygons)
        {
            foreach (Vector2 point in polygon)
            {
                vertices.Add(new Vector3(point, 0f));
                colors.Add(Color.White);
            }
        }

        foreach (List<Vector2> polygon in shadowPolygons)
        {
            foreach (Vector2 point in polygon)
            {
                vertices.Add(new Vector3(point, 0f));
                colors.Add(Color.White);
            }
        }

        // Triangles ----
        if (mainPolygon0Conn.Count > 0)
        {
            KnitTriangles(mainPolygon0, mainPolygon0Conn, mainPolygon1.Count, 0);
        }

        if (mainPolygon1Conn.Count > 0)
        {
            KnitTriangles(mainPolygon1, mainPolygon1Conn, mainPolygon2.Count, mainPolygon0.Count);

            if (pathOverlaps && !cornerRaysIntersectBeforeSpikes)
            {
                int a = mainPolygon0.Count + mainPolygon1.Count;
                int b = a - 1;
                int c = a + mainPolygon2.Count - 1;

                indices.Add(b);
                indices.Add(c);
                indices.Add(a);
                indices.Add(b);
                indices.Add(a);
                indices.Add(mainPolygon0.Count);
                indices.Add(b);
                indices.Add(c - 1);
                indices.Add(c);
                indices.Add(b);
                indices.Add(c - 2);
                indices.Add(c - 1);
                indices.Add(b);
                indices.Add(c - 3);
                indices.Add(c - 2);
            }
        }

        if (mainPolygon2Conn.Count > 0)
        {
            KnitTriangles(mainPolygon2, mainPolygon2Conn, mainPolygon3.Count,
                mainPolygon0.Count + mainPolygon1.Count);
        }

        int shadowBase = mainPolygon0.Count + mainPolygon1.Count + mainPolygon2.Count + mainPolygon3.Count;
        KnitTriangles(cwShadowPolygon0, cwShadowPolygonConn, cwShadowPolygon1.Count, shadowBase);
        KnitTriangles(ccwShadowPolygon0, ccwShadowPolygonConn, ccwShadowPolygon1.Count,
            shadowBase + cwShadowPolygon0.Count + cwShadowPolygon1.Count);

        // ---- UV / UVs ----
        float[] insetPerLayer = [0f, insetDistance0, insetDistance1, insetDistance2, 0f];
        float bodyV = 1f;
        int layerIndex = 0;

        foreach (List<Vector2> polygon in bodyPolygons)
        {
            bodyV -= insetPerLayer[layerIndex] / width;

            foreach (Vector2 point in polygon)
            {
                uv0.Add(new Vector2(0.5f, bodyV));
                uv1.Add(point);
            }

            layerIndex++;
        }

        float[] shadowV = [2f, 1f, 2f, 1f];

        if (diamondShadowOuterVertexDistance > 0f)
        {
            shadowV[2] = 2f - diamondShadowOuterVertexDistance;
        }

        int shadowIndex = 0;

        foreach (List<Vector2> polygon in shadowPolygons)
        {
            float v = shadowV[shadowIndex];

            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 uv = new(0.5f, v);

                if (diamondShadowOuterVertexDistance > 0f && shadowIndex == 2 && (i == 0 || i == 2))
                {
                    uv = new Vector2(0.5f, 1f);
                }

                uv0.Add(uv);
                uv1.Add(polygon[i]);
            }

            shadowIndex++;
        }
    }

    /// <summary>
    /// Stitches a polygon to its inset ring. Only values at or above <see cref="NoConnection"/> are
    /// skipped; negative values are meaningful and must take part, because the corner branch writes
    /// -1 so that <c>offset + count + (-1)</c> resolves to this polygon's last vertex and closes the
    /// corner. Treating negatives as "no connection" drops those triangles and leaves holes that show
    /// the background through the tile.
    /// </summary>
    private void KnitTriangles(List<Vector2> polygon, List<int> connections, int insetPolygonCount, int offset)
    {
        int count = polygon.Count;

        for (int i = 0; i < polygon.Count && i < connections.Count; i++)
        {
            int connection = connections[i];

            // Only the sentinel and above mean "nothing to stitch to"; negative values still stitch.
            if (connection >= NoConnection)
            {
                continue;
            }

            indices.Add(offset + i);
            indices.Add(offset + count + connection);
            indices.Add(offset + (i + 1) % polygon.Count);

            int nextConnection = connections[(i + 1) % connections.Count];

            if (nextConnection != connection && nextConnection >= 0)
            {
                indices.Add(offset + (i + 1) % polygon.Count);
                indices.Add(offset + count + connection);
                indices.Add(offset + count + (connection + 1) % insetPolygonCount);
            }
        }
    }

    private Vector2 GenerateNthVertex(int index, float radius)
    {
        float angle = MathF.PI / 2f + MathF.PI / 3f * index;
        return new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
    }

    // ------------------------------------------------------------------
    // Line-by-line port of GetPositions
    // ------------------------------------------------------------------
    private void GetPositions(double angle0Degrees, double angle1Degrees, float tileWidth, float tileLength, int curvature)
    {
        width = MathF.Max(0f, tileWidth);
        length = MathF.Max(tileLength, width);
        curvaturePoints = Math.Max(curvature, 3);

        angle0 = ModAngle360((float)(angle0Degrees * MathF.PI / 180f));
        angle1 = ModAngle360((float)(angle1Degrees * MathF.PI / 180f));

        if (!(ModAngle360(angle1 - angle0) < MathF.PI))
        {
            (angle0, angle1) = (angle1, angle0);
        }

        insetDistance0 = 0f;
        insetDistance1 = 0f;
        insetDistance2 = 0f;
        cornerCenterApothem = 0f;

        shortAngle = SmallestAngleBetweenTwoAngles(angle1, angle0);
        bool zeroAngle = shortAngle < AngleEpsilon;
        bool piAngle = MathF.Abs(shortAngle - MathF.PI) < AngleEpsilon;

        origin = Vector2.Zero;

        if (MathF.Abs(shortAngle - 0f) < AngleEpsilon)
        {
            origin += Add(angle1, -length * 0.3333333f / 4f);
            length *= UTurnOffset;
        }

        vector = origin + Add(angle0, length);
        num2 = angle0 + MathF.PI / 2f;
        startMore = vector + Add(num2, width);
        float angle2 = num2 + MathF.PI / 2f;
        vector2 = startMore + Add(angle2, width * RayEpsilon);

        num3 = angle0 - MathF.PI / 2f;
        startLess = vector + Add(num3, width);
        float angle3 = num3 - MathF.PI / 2f;
        Vector2 end = startLess + Add(angle3, width * RayEpsilon);

        vector3 = origin + Add(angle1, length);
        num4 = angle1 + MathF.PI / 2f;
        endMore = vector3 + Add(num4, width);
        float angle4 = num4 + MathF.PI / 2f;
        Vector2 end2 = endMore + Add(angle4, width * RayEpsilon);

        num5 = angle1 - MathF.PI / 2f;
        endLess = vector3 + Add(num5, width);
        float angle5 = num5 - MathF.PI / 2f;
        vector4 = endLess + Add(angle5, width * RayEpsilon);

        ccwIntersection = zeroAngle || piAngle
            ? origin
            : GetLineIntersection(new Segment(startMore, vector2), new Segment(endLess, vector4));

        cwIntersection = zeroAngle || piAngle
            ? origin
            : GetLineIntersection(new Segment(startLess, end), new Segment(endMore, end2));

        cornerLerp = 0f;

        if (shortAngle < Angle5)
        {
            cornerLerp = 1f;
        }
        else if (shortAngle < Angle30)
        {
            float span = MathF.PI * 5f / 36f;
            cornerLerp = Lerp(1f, 0.83f, MathF.Pow((shortAngle - Angle5) / span, 0.5f));
        }
        else if (shortAngle < Angle45)
        {
            float span = MathF.PI / 12f;
            cornerLerp = Lerp(0.83f, 0.77f, MathF.Pow((shortAngle - Angle30) / span, 1f));
        }
        else if (shortAngle < Angle90)
        {
            float span = Angle45;
            cornerLerp = Lerp(0.77f, 0.15f, MathF.Pow((shortAngle - Angle45) / span, 0.7f));
        }
        else if (shortAngle < Angle120)
        {
            float span = Angle30;
            cornerLerp = Lerp(0.15f, 0f, MathF.Pow((shortAngle - Angle90) / span, 0.5f));
        }

        if (cornerLerp < 0.001f)
        {
            cornerLerp = 0f;
        }

        angleDifference = ModAngle360(angle1 - angle0);

        Vector2 center = Vector2.Lerp(cwIntersection, origin, cornerLerp);
        Vector2 center2 = Vector2.Lerp(ccwIntersection, origin, cornerLerp);
        cornerCenterRadius = Lerp(0f, width, cornerLerp);

        cwCornerPoints.Clear();
        ccwCornerPoints.Clear();

        if (angleDifference < 2.0942953f)
        {
            cornerCircleCenter = center;
            Vector2[] arc = CreateCircleArc(center, ModAngle360(num4), ModAngle360(num3),
                cornerCenterRadius, curvaturePoints, out curvatureSliceAngle);
            cwCornerPoints.AddRange(arc);
        }
        else
        {
            cwCornerPoints.Add(cwIntersection);
        }

        if (angleDifference > 4.3634233f)
        {
            cornerCircleCenter = center2;
            Vector2[] arc = CreateCircleArc(center2, ModAngle360(num2), ModAngle360(num5),
                cornerCenterRadius, curvaturePoints, out curvatureSliceAngle);
            ccwCornerPoints.AddRange(arc);
        }
        else
        {
            ccwCornerPoints.Add(ccwIntersection);
        }

        Vector2 b = angleDifference < MathF.PI ? ccwIntersection : cwIntersection;
        bool isPathOverlapping = Vector2.Distance(origin, startMore) < Vector2.Distance(origin, b);

        overlappingPoints = null;

        if (isPathOverlapping)
        {
            overlappingPoints =
            [
                GetLineIntersection(new Segment(startLess, startMore), new Segment(endLess, vector4)),
                GetLineIntersection(new Segment(startLess, startMore), new Segment(endLess, endMore)),
                GetLineIntersection(new Segment(endLess, endMore), new Segment(startMore, vector2)),
            ];
        }

        mainPolygon0.Clear();
        mainPolygon0Conn.Clear();
        mainPolygon1.Clear();
        mainPolygon1Conn.Clear();
        mainPolygon2.Clear();
        mainPolygon2Conn.Clear();
        mainPolygon3.Clear();
        cwShadowPolygon0.Clear();
        cwShadowPolygonConn.Clear();
        ccwShadowPolygon0.Clear();
        ccwShadowPolygonConn.Clear();
        cwShadowPolygon1.Clear();
        ccwShadowPolygon1.Clear();
        diamondShadowOuterVertexDistance = 0f;

        cornerPointIndex = GeneratePolygon(mainPolygon0, roundCorners: true);

        List<Vector2> list = mainPolygon0;
        pathOverlaps = isPathOverlapping && !zeroAngle && !piAngle;

        if (pathOverlaps)
        {
            Segment bisector = GetBisector(list[^1], list[0], list[1]);
            Segment bisector2 = GetBisector(list[0], list[1], list[2]);
            cornerCenterApothem = cornerCenterRadius * MathF.Cos(curvatureSliceAngle / 2f);
            bisectorIntersection = GetLineIntersection(bisector, bisector2);

            float projection = Projection(new Segment(list[1], list[2]), bisectorIntersection);
            cornerRaysIntersectBeforeSpikes = cornerCenterApothem < projection;

            if (cornerRaysIntersectBeforeSpikes)
            {
                insetDistance0 = cornerCenterApothem;
                insetDistance1 = projection - insetDistance0;
            }
            else
            {
                insetDistance0 = projection;
            }
        }
        else if (cornerLerp > 0f)
        {
            insetDistance0 = cornerCenterRadius;
        }
        else
        {
            insetDistance0 = width;
        }

        bool flag = !zeroAngle && !pathOverlaps;

        if (flag)
        {
            list = [];
            GeneratePolygon(list, roundCorners: false);
        }

        if (!zeroAngle)
        {
            mainPolygon1 = CreateInsetFromPolygon(list, insetDistance0);
        }
        else
        {
            Vector2 item = origin + Add(angle0, length - width);
            mainPolygon1 = [item, origin];
            mainPolygon0Conn.Add(0);

            for (int j = 0; j < curvaturePoints; j++)
            {
                mainPolygon0Conn.Add(1);
            }

            mainPolygon0Conn.Add(0);
        }

        if (!zeroAngle)
        {
            if (flag && mainPolygon1.Count != mainPolygon0.Count)
            {
                for (int k = 0; k < mainPolygon0.Count; k++)
                {
                    if (k == cornerPointIndex)
                    {
                        for (int l = 0; l < curvaturePoints; l++)
                        {
                            mainPolygon0Conn.Add(k);
                        }
                    }
                    else
                    {
                        mainPolygon0Conn.Add(k);
                    }
                }

                insetDistance1 = width - insetDistance0;

                if (insetDistance1 > 0f)
                {
                    mainPolygon2 = CreateInsetFromPolygon(mainPolygon1, insetDistance1);

                    for (int m = 0; m < mainPolygon2.Count; m++)
                    {
                        mainPolygon1Conn.Add(m);
                    }
                }
            }
            else
            {
                for (int n = 0; n < mainPolygon0.Count; n++)
                {
                    mainPolygon0Conn.Add(n);
                }
            }

            if (pathOverlaps)
            {
                if (cornerRaysIntersectBeforeSpikes)
                {
                    int startIndex = 4;
                    int count = curvaturePoints - 1;
                    WeldConnectionsForVertices(mainPolygon0, mainPolygon0Conn, mainPolygon1, startIndex, count);
                    mainPolygon1[3] = cornerCircleCenter;
                    mainPolygon2 = CreateInsetFromPolygon(mainPolygon1, insetDistance1);

                    for (int n = 0; n < mainPolygon2.Count; n++)
                    {
                        mainPolygon1Conn.Add(n);
                    }

                    startIndex = mainPolygon1.Count - 2;
                    count = 4;
                    WeldConnectionsForVertices(mainPolygon1, mainPolygon1Conn, mainPolygon2, startIndex, count);
                    insetDistance2 = width - insetDistance1 - insetDistance0;
                    mainPolygon2Conn.AddRange([0, 0, 0, 0]);
                    mainPolygon3.Add(origin);
                }
                else
                {
                    int startIndex = mainPolygon0.Count - 2;
                    WeldConnectionsForVertices(mainPolygon0, mainPolygon0Conn, mainPolygon1, startIndex, 4);

                    Vector2 start = mainPolygon1[mainPolygon0Conn[cornerPointIndex]];
                    Vector2 end3 = mainPolygon1[mainPolygon0Conn[cornerPointIndex + 1]];
                    float projection = Projection(new Segment(start, end3), cornerCircleCenter);

                    insetDistance1 = cornerCenterApothem - insetDistance0;

                    if (useFInset2)
                    {
                        insetDistance1 = projection;
                    }

                    mainPolygon2 = CreateInsetFromPolygon(mainPolygon1, insetDistance1);

                    float innerLength = length - insetDistance0 - insetDistance1;
                    float innerWidth = width - insetDistance0 - insetDistance1;
                    Vector2 vector5 = Normalized(vector) * innerLength;
                    Vector2 vector6 = Normalized(vector3) * innerLength;
                    Vector2 vector7 = vector5 + Normalized(startMore - vector) * innerWidth;
                    Vector2 vector8 = vector6 + Normalized(endLess - vector3) * innerWidth;
                    Vector2 end4 = vector7 + Normalized(vector2 - startMore) * 0.01f;
                    Vector2 end5 = vector8 + Normalized(vector4 - endLess) * 0.01f;
                    Vector2 lineIntersection =
                        GetLineIntersection(new Segment(vector7, end4), new Segment(vector8, end5));

                    mainPolygon2.RemoveAt(mainPolygon2.Count - 1);
                    mainPolygon2.Add(vector7);
                    mainPolygon2.Add(lineIntersection);
                    mainPolygon2.Add(vector8);

                    for (int n = 0; n < mainPolygon2.Count; n++)
                    {
                        mainPolygon1Conn.Add(n);
                    }

                    WeldConnectionsForVertices(mainPolygon1, mainPolygon1Conn, mainPolygon2, 3,
                        curvaturePoints - 3);

                    mainPolygon1Conn[^1] = -1;
                    mainPolygon1Conn[^2] = -1;
                    mainPolygon1Conn[^3] = -1;

                    innerLength = length - width;
                    vector5 = Normalized(vector) * innerLength;
                    vector6 = Normalized(vector3) * innerLength;

                    Vector2 middle = Vector2.Lerp(mainPolygon2[2], mainPolygon2[^2], 0.5f);
                    mainPolygon3.Add(vector6);
                    mainPolygon3.Add(middle);
                    mainPolygon3.Add(vector5);
                    mainPolygon3.Add(middle);

                    mainPolygon2Conn.AddRange([0, 1, 1, 1, 2, 2, 3, 0]);
                    insetDistance2 = width - insetDistance0 - insetDistance1;
                }
            }
        }

        if (piAngle)
        {
            Vector2 cwOuter = Normalized(startMore - vector) * ShadowWidth + startMore;
            Vector2 ccwOuter = Normalized(endLess - vector3) * ShadowWidth + endLess;
            ccwShadowPolygon0.AddRange([cwOuter, ccwOuter]);
            ccwShadowPolygonConn.AddRange([0, NoConnection]);
            ccwShadowPolygon1.AddRange([startMore, endLess]);

            Vector2 otherCw = Normalized(endMore - vector3) * ShadowWidth + endMore;
            Vector2 otherCcw = Normalized(startLess - vector) * ShadowWidth + startLess;
            cwShadowPolygon0.AddRange([otherCw, otherCcw]);
            cwShadowPolygonConn.AddRange([0, NoConnection]);
            cwShadowPolygon1.AddRange([endMore, startLess]);
            return;
        }

        if (!pathOverlaps && !zeroAngle)
        {
            Vector2 lineIntersection2 =
                GetLineIntersection(new Segment(vector3, endLess), new Segment(vector, startMore));

            if (Vector2.Distance(lineIntersection2, endLess) < ShadowWidth)
            {
                ccwShadowPolygon0.AddRange([startMore, lineIntersection2, endLess]);
                ccwShadowPolygonConn.AddRange([0, 0]);
                ccwShadowPolygon1.Add(ccwIntersection);
                diamondShadowOuterVertexDistance =
                    1f - Vector2.Distance(ccwIntersection, lineIntersection2) / ShadowWidth;
            }
            else
            {
                Vector2 vector14 = Normalized(startMore - vector) * ShadowWidth + startMore;
                Vector2 vector15 = Normalized(endLess - vector3) * ShadowWidth + endLess;
                Vector2 lineIntersection3 = GetLineIntersection(
                    new Segment(vector14, vector14 + (vector2 - startMore)),
                    new Segment(vector15, vector15 + (vector4 - endLess)));

                ccwShadowPolygon0.AddRange([vector14, lineIntersection3, vector15]);
                ccwShadowPolygonConn.AddRange([0, 1]);
                ccwShadowPolygon1.AddRange([startMore, ccwIntersection, endLess]);
            }
        }

        Vector2 cwItem = Normalized(endMore - vector3) * ShadowWidth + endMore;
        Vector2 ccwItem = Normalized(startLess - vector) * ShadowWidth + startLess;

        cwShadowPolygon0.Add(cwItem);
        cwShadowPolygonConn.Add(0);

        bool hasCwArc = cwCornerPoints.Count > 1;
        float shadowRadius = hasCwArc ? ShadowWidth + cornerCenterRadius : ShadowWidth;
        Vector2[] shadowArc = CreateCircleArc(center, ModAngle360(num4), ModAngle360(num3),
            shadowRadius, curvaturePoints, out curvatureSliceAngle);

        int shadowConnection = 1;

        foreach (Vector2 point in shadowArc)
        {
            cwShadowPolygon0.Add(point);
            cwShadowPolygonConn.Add(shadowConnection);

            if (hasCwArc)
            {
                shadowConnection++;
            }
        }

        cwShadowPolygon0.Add(ccwItem);
        cwShadowPolygon1.Add(endMore);
        cwShadowPolygon1.AddRange(cwCornerPoints);
        cwShadowPolygon1.Add(startLess);

        // Local helpers below ----

        List<Vector2> CreateInsetFromPolygon(List<Vector2> polygon, float distance)
        {
            List<Vector2> inset = [];
            Segment[] segments = new Segment[polygon.Count];

            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[(i + 1) % polygon.Count];
                Segment s = new(a, b);
                Vector2 normal = GenerateNormal(s, distance);
                segments[i] = new Segment(a + normal, b + normal);
            }

            for (int i = 0; i < segments.Length; i++)
            {
                int previous = Mod(i - 1, segments.Length);
                int current = Mod(i, segments.Length);
                inset.Add(GetLineIntersection(segments[previous], segments[current]));
            }

            return inset;
        }

        int GeneratePolygon(List<Vector2> v, bool roundCorners)
        {
            List<Vector2> ccwPoints = roundCorners ? ccwCornerPoints : [ccwIntersection];
            List<Vector2> cwPoints = roundCorners ? cwCornerPoints : [cwIntersection];
            int result = 0;

            if (zeroAngle)
            {
                v.Add(startMore);
                v.AddRange(cwPoints);
                v.Add(endLess);
            }
            else if (piAngle)
            {
                v.Add(startMore);
                v.Add(endLess);
                v.Add(endMore);
                v.Add(startLess);
            }
            else if (!isPathOverlapping)
            {
                v.Add(startMore);

                bool ccwIsLonger = ccwPoints.Count > cwPoints.Count;

                if (ccwIsLonger)
                {
                    result = v.Count;
                }

                v.AddRange(ccwPoints);
                v.Add(endLess);
                v.Add(endMore);

                if (!ccwIsLonger)
                {
                    result = v.Count;
                }

                v.AddRange(cwPoints);
                v.Add(startLess);
            }
            else
            {
                v.Add(startMore);
                v.Add(overlappingPoints![2]);
                v.Add(endMore);
                result = v.Count;
                v.AddRange(cwPoints);
                v.Add(startLess);
                v.Add(overlappingPoints[0]);
                v.Add(endLess);
                v.Add(overlappingPoints[1]);
            }

            return result;
        }
    }

    private void WeldConnectionsForVertices(
        List<Vector2> polygon, List<int> connections, List<Vector2> inset, int startIndex, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int polygonVertexIndex = Mod(startIndex + i, polygon.Count);
            WeldWithPreviousVertexConnection(polygon, connections, inset, polygonVertexIndex);
        }
    }

    private static void WeldWithPreviousVertexConnection(
        List<Vector2> polygon, List<int> connections, List<Vector2> inset, int polygonVertexIndex)
    {
        int previousIndex = polygonVertexIndex == 0 ? polygon.Count - 1 : polygonVertexIndex - 1;
        int removed = connections[polygonVertexIndex];
        int value = connections[previousIndex];
        connections[polygonVertexIndex] = value;
        inset.RemoveAt(removed);

        for (int i = 0; i < connections.Count; i++)
        {
            if (connections[i] >= removed)
            {
                connections[i]--;
            }
        }
    }

    private Vector2[] CreateCircleArc(Vector2 center, float angleA, float angleB, float radius,
        int pointCount, out float sliceAngle)
    {
        if (angleA > angleB)
        {
            angleA -= MathF.PI * 2f;
        }

        pointCount = Math.Max(pointCount, 2);
        Vector2[] arc = new Vector2[pointCount];

        for (int i = 0; i < pointCount; i++)
        {
            float t = i / (float)(pointCount - 1);
            float angle = Lerp(angleA, angleB, t);
            arc[i] = center + Add(angle, radius);
        }

        sliceAngle = (angleB - angleA) / (pointCount - 1);
        return arc;
    }

    private static float SmallestAngleBetweenTwoAngles(float angleA, float angleB)
    {
        float a = Mod(angleB - angleA, MathF.PI * 2f);
        float b = Mod(angleA - angleB, MathF.PI * 2f);
        return MathF.Min(a, b);
    }

    private static Vector2 GetLineIntersection(Segment a, Segment b)
    {
        Vector2 dx = new(a.start.X - a.end.X, b.start.X - b.end.X);
        Vector2 dy = new(a.start.Y - a.end.Y, b.start.Y - b.end.Y);
        float determinant = Determinant(dx, dy);
        Vector2 constants = new(Determinant(a.start, a.end), Determinant(b.start, b.end));

        // Undivided determinants would produce infinity; return NaN for parallel lines so callers can
        if (determinant == 0f)
        {
            return new Vector2(float.NaN, float.NaN);
        }

        return new Vector2(Determinant(constants, dx) / determinant, Determinant(constants, dy) / determinant);
    }

    private static float Determinant(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    private static Vector2 GenerateNormal(Segment s, float distance)
    {
        Vector2 result = s.end - s.start;
        float x = result.X;
        float y = result.Y;
        result = new Vector2(-y, x);
        result = Normalized(result) * distance;
        return result;
    }

    private static float Projection(Segment s, Vector2 p)
    {
        Vector2 lhs = p - s.start;
        Vector2 direction = Normalized(s.end - s.start);
        float dot = Vector2.Dot(lhs, direction);
        float magnitude = lhs.Length();
        return MathF.Sqrt(magnitude * magnitude - dot * dot);
    }

    private static Segment GetBisector(Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 toA = Normalized(a - b);
        Vector2 toC = Normalized(c - b);

        float angleC = MathF.Atan2(toC.Y, toC.X);

        if (angleC < 0f)
        {
            angleC += MathF.PI * 2f;
        }

        float angleA = MathF.Atan2(toA.Y, toA.X);

        if (angleA < 0f)
        {
            angleA += MathF.PI * 2f;
        }

        float difference = angleC - angleA;
        float sign = difference > 0f && difference < MathF.PI || difference < -MathF.PI ? 1f : -1f;
        Vector2 direction = Normalized((toA + toC) / 2f) * sign;

        return new Segment(b, b + direction * 0.1f);
    }

    // Polar offset helper
    private static Vector2 Add(float angle, float distance)
        => new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * distance;

    private static Vector2 Normalized(Vector2 value)
        => value.LengthSquared() > 0f ? Vector2.Normalize(value) : Vector2.Zero;

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Mod(float a, float b) => (a % b + b) % b;

    private static int Mod(int a, int b) => (a % b + b) % b;

    private static float ModAngle360(float a) => Mod(a, MathF.PI * 2f);

    /// <summary>A line segment.</summary>
    private readonly struct Segment
    {
        public readonly Vector2 start;
        public readonly Vector2 end;

        public Segment(Vector2 start, Vector2 end)
        {
            this.start = start;
            this.end = end;
        }
    }
}

/// <summary>
/// What <see cref="FloorMesh"/> produces: vertex, colour, index, uv and outline buffers.
/// </summary>
public sealed class FloorMeshResult
{
    /// <summary>The cache key this result was built for.</summary>
    public string CacheKey { get; init; } = string.Empty;

    /// <summary>Vertex positions.</summary>
    public Vector3[] Vertices { get; init; } = [];

    /// <summary>Vertex colors.</summary>
    public Color[] Colors { get; init; } = [];

    /// <summary>Triangle indices.</summary>
    public int[] Indices { get; init; } = [];

    /// <summary>summary>
    public Vector2[] Uv0 { get; init; } = [];

    /// <summary>Tile-local coordinates, used for texture and icon sampling.</summary>
    public Vector2[] Uv1 { get; init; } = [];

    /// <summary>Collider outline.</summary>
    public Vector2[] Collider { get; init; } = [];
}

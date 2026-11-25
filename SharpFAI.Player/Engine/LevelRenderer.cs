using OpenTK.Mathematics;
using SharpFAI.Player.Engine.Play;
using SharpFAI.Player.Engine.Events;
using Thrust.Audio;
using Thrust.Geometry;
using Thrust.Graphics;
using Thrust.Scene;
using SharpFAI.Framework;
using SharpFAI.Serialization;
using System.Diagnostics;

namespace SharpFAI.Player.Engine;

/// <summary>
/// Loads and plays a level, rendering its floors in descending index order.
/// </summary>
public sealed unsafe partial class LevelRenderer : IDisposable
{

// 本文件保存全部状态字段与对外属性；行为按职责拆分在同名的 partial 文件中。
// This file holds every state field and the public surface; behaviour lives in the sibling partial files.

    private readonly GlMeshUploader _uploader = new();

    private readonly Camera2D _camera = new();
    private LevelPlaybackSettings _settings = new();
    private BackgroundRenderer? _background;
    private Vector2 _cameraOrigin;
    private float _homeRotation;
    public float TileSpacing => Floor.length * 2f * _settings.TileLength;

    public bool ShowDecorations { get; set; } = true;

    /// <summary>
    /// Progress preparing the chart's filter programs and textures, or <c>null</c> when ready.
    /// </summary>
    public (int Done, int Total)? ShaderPrewarmProgress =>
        _shaderPrewarm is { Count: > 0 } ? (_shaderPrewarmDone, _shaderPrewarmTotal) : null;

    public Vector4 BackgroundColor => _backdrop?.BackgroundAt(_playTime).Settings.Background ?? _settings.Background;
    private BackdropTimeline? _backdrop;
    private BackdropTimeline.ImageState? _uploadedBackdrop;
    private SceneColorHistory? _colorHistory;
    public double PlaybackRate => _pitch;

    // Frame-time ring buffer; no per-frame allocations
    private const int FrameTimeWindow = 60;

    private readonly double[] _frameTimeRing = new double[FrameTimeWindow];

    private int _frameTimeIndex;

    private int _frameTimeCount;

    private double _frameTimeSum;

    // Full-turn thresholds in degrees; a sweep at or beyond one turn wraps to a single turn
    private const double FullTurnEpsilonDegrees = 1e-6 * 180.0 / Math.PI;

    private const double FullTurnDegrees = 6.283184482025146 * 180.0 / Math.PI;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly List<string> _warnings = [];

    private OfficialFloorRenderer? _officialRenderer;
    private VisibleFloorIndex? _visibleFloorIndex;
    private int[]? _residentFloors;
    private FloorRegion _residentRegion = FloorRegion.Empty;
    private TrackGuideRenderer? _trackGuideRenderer;
    private int _floorTextureCapacity;
    private Vector4[]? _floorRenderColors;
    private FloorRenderTransform[]? _floorRenderTransforms;
    private double _floorVisualTime = double.NaN;
    private RuntimeTrackTimeline? _trackRuntime;
    private HashSet<FloorMaterial>?[] _trackMaterialOptions = [];
    private TrackStyleInfo? _styleOverride;
    private bool _runtimeTrackActive;
    private bool _runtimeTrackBeforeOffline;

    private Aabb _levelBounds;

    private bool _hasBounds;

    private Level? _level;

    private string _status = "未加载关卡";

    /// <summary>
    /// The current track style. Switching swaps the textures, shadow parameters and tile size; the
    /// last one needs a mesh rebuild, so the renderer owns the whole transition.
    /// </summary>
    private TrackStyleInfo _trackStyle = TrackStyleInfo.Standard;

    /// <summary>
    /// The selected floor index, or -1 when nothing is selected. Playback starts from this floor.
    /// </summary>
    private int _selectedFloor = -1;

    private double _lastFrameTime;

    private Vector2 _homePosition;

    private float _homeZoom = 1f;

    // Playback state ----
    private List<Floor> _floors = [];
    private Vector2[]? _floorPositionRemainders;

    /// <summary>Level length, up to the last floor.</summary>
    private double _levelDuration;

    /// <summary>Music offset in seconds from the level offset setting</summary>
    private double _offsetSeconds;

    /// <summary>Level pitch, 1.0 = original speed</summary>
    private double _pitch = 1.0;

    /// <summary>
    ///   entryTimes[i] = entryTime[i] + offset - countdown
    /// Absolute entry time of every floor in seconds, on the **music file position** clock:
    ///   entryTimes[i] = entryTime[i] + offset - countdown
    /// The music, the hitsound and every visual are all evaluated on this single clock.
    /// </summary>
    private double[] _entryTimes = [];

    // The planet's sweep start angle and signed swept angle per floor, in our (cos, sin) basis, degrees
    private double[] _sweepStart = [];

    private double[] _sweepDelta = [];
    private PlanetTrackFloor[] _planetTrack = [];
    private Dictionary<int, PauseAngleCorrection>? _pauseAngles;
    private CameraTimeline? _cameraTimeline;
    private Filters.FilterTimeline? _filterTimeline;
    private Filters.FilterTimeline? _backgroundFilterTimeline;
    private SceneEffectTimeline? _sceneEffects;
    private ImageDecorationTimeline? _imageDecorations;
    private ImageDecorationRenderer? _decorationRenderer;
    private bool _decorationShaderFailed;
    private readonly List<Filters.FilterTimeline.Entry> _scenePasses = [];
    private IReadOnlyList<Filters.FilterTimeline.Entry> _backgroundPasses = [];
    private Filters.OpenGlFilterBackend? _filterBackend;
    private Queue<(string Name, int? Pass)>? _shaderPrewarm;
    private int _shaderPrewarmTotal;
    private int _shaderPrewarmDone;
    private bool _preparingLevelEffects;
    private readonly List<(Filters.FilterTimeline Timeline, string Name)> _componentPrewarm = [];
    private int _componentPrewarmIndex;
    private bool _schedulesPrewarmed;
    private bool _scenePrewarmed;
    private readonly Queue<BackdropTimeline.ImageState> _backgroundPrewarm = [];
    private int _staticTextureIndex;
    private (string Image, double Start)[] _textureWarmupOrder = [];
    private long _prewarmedTextureBytes;
    private long _reservedLargeTextureBytes;
    private long _texturePrewarmBudget;
    private readonly HashSet<string> _reservedLargeTextures = new(StringComparer.Ordinal);
    private int _filterCamerasToPrewarm;
    private bool _prewarmBloom;
    private bool _prewarmMotionBlur;
    private double _lastFilterTime = double.NaN;
    private double _lastFilterWallTime;
    private double _liveFilterClock;
    private bool _filterClockPaused;
    private double _filterClock;
    private float _filterDelta;
    private Vector2 _cameraFollowCenter;
    private bool _cameraEventActive;
    private bool _cameraEventZoomApplied;
    private Vector2 _thirdCenter;

    /// <summary>每块砖解析后的外观，含消失延迟（<see cref="FloorTrackState.BeatsBehind"/>）。</summary>
    /// <remarks>与 <c>_floors</c> 平行；渲染时用来判断某块砖是否已经消失。</remarks>
    private FloorTrackState[] _trackStates = [];

    private int _currentIndex;

    private bool _isPlaying;

    private double _playTime;

    private double _duration;

    // Camera and planet center, lerped between two floors and arriving exactly on the next entryTime
    private Vector2 _planetCenter;

    // Secondary planet centre (mirrored about the midpoint)
    private Vector2 _secondaryCenter;

    // Smooth camera follow state; the camera always trails the planet by about half a tile
    private Vector2 _cameraFrom;

    private Vector2 _cameraTo;

    private double _cameraElapsed;

    private int _cameraIndex = -1;

    private double _lastCameraTime;
    private Vector2 _cameraShake;
    private double _cameraPulseTime = double.NegativeInfinity;
    private int _cameraPulseFloor = -1;
    private Vector2 _cameraHoldOffset;
    private Vector2 _cameraHoldReleaseOffset;
    private double _cameraHoldReleaseTime = double.NaN;
    private bool _cameraRenderDisabled;
    private Action<HitRecord>? _cameraHitCallback;

    // Request a hard camera snap (set by Seek/Restart) so a frame hitch is not mistaken for a jump
    private bool _cameraSnap;

    // The user panned or zoomed the view while paused: the timeline stops overwriting the camera
    // until playback resumes or a seek re-takes control.
    private bool _cameraFree;

    // Planet rotation
    private float _rotationAngle;

    // Continuous (unwrapped) rotation used for hit timing.
    private float _rotationAngleContinuous;

    private bool _isCw = true;

    // Leapfrog flag flipped on non-midspin floors

    // Planets and audio ----
    private CircleRenderer? _circleRenderer;

    private SpriteRenderer? _spriteRenderer;

    /// <summary>Planet sprite animation rate in frames per second.</summary>
    private const float PlanetAnimationFps = 12f;

    // Trail: tracks each planet's continuous path; see PlanetTrail for the implementation
    private readonly PlanetTrail _trail = new();
    private SpriteRenderer.Particle[] _trailParticles = [];

    // Shader hot reload
    private FileSystemWatcher? _shaderWatcher;

    private volatile bool _shaderReloadPending;

    private string _shaderStatus = "着色器: 内嵌";

    private AudioClip _music = new() { Name = "Music" };

    private AudioClip _hitsound = new() { Name = "Hitsound" };

    private bool _audioAvailable;

    private string _audioStatus = "未加载音频";

    private float _musicVolume = 0.8f;

    private float _hitsoundVolume = 1f;

    private bool _musicEnabled = true;

    private bool _hitsoundEnabled = true;

    // Manual hitsound offset against the music in milliseconds, for what a level's offset misses
    private float _hitsoundShiftMs;

    // Audio may only realign after an explicit seek or restart, never mid-stream
    private bool _audioNeedsResync = true;

    // Overrides the chart's exit animation with immediate hiding.
    private bool _hidePassedFloors;

    private ManualTrack? _manualTrack;

    // Soft clock reconciliation against the audio
    //
    // Time constant for absorbing an offset; far longer than a frame, so speed changes by a few percent
    private const double PlaybackClockCorrectionSeconds = 2.0;

    // Offsets below this are left alone so the clock does not chase noise
    private const double PlaybackClockDeadZoneSeconds = 0.05;

    // An offset this large is treated as a real jump and applied directly
    private const double PlaybackClockSnapSeconds = 0.5;

    // Asynchronous loading ----
    // Loading a level takes seconds (parsing, meshing, hitsound synthesis), which would freeze the
    // window if done inline. The CPU-only work runs on a background thread; the main thread only
    // performs the GPU upload and the final scene swap.
    private CancellationTokenSource? _loadCts;

    private Task<LevelLoadResult>? _loadTask;

    private volatile string _loadPhase = string.Empty;

    private double _loadStartedAt;

    private string? _loadError;

    // Per-phase timings of the last load in milliseconds, shown in the panel once it finishes
    private List<(string Phase, double Milliseconds)> _lastLoadTimings = [];

    private double _lastLoadTotalMs;

    /// <summary>Whether a load is running in the background.</summary>
    public bool IsLoading => _loadTask is not null || _preparingLevelEffects;

    /// <summary>
    /// Skip hit-sound synthesis on the next load. It renders every note's kick up front and
    /// dominates load time on long charts, so previews and captures can turn it off.
    /// </summary>
    /// <summary>Skip music and hitsounds on subsequent level loads.</summary>
    public bool SkipAudio { get; set; }

    /// <summary>Whether the last load failed.</summary>
    public string? LoadError => _loadError;

    /// <summary>Human readable description of the running load phase.</summary>
    public string LoadPhase => _loadPhase;

    /// <summary>Seconds elapsed since the load started.</summary>
    public double LoadElapsed => _loadStartedAt > 0.0 ? _clock.Elapsed.TotalSeconds - _loadStartedAt : 0.0;

    /// <summary>
    /// Everything a background thread can compute for a level; the main thread only uploads and swaps.
    /// </summary>
    private sealed class LevelLoadResult
    {
        public required string Path { get; init; }
        public required Level Level { get; init; }
        public required List<Floor> Floors { get; init; }
        public Vector2[]? FloorPositionRemainders { get; init; }
        public FloorGeometry? Geometry { get; init; }
        public IReadOnlyDictionary<FloorTexture, ImageDecoder.RgbaImage>? FloorTextures { get; init; }
        public VisibleFloorIndex? Visibility { get; init; }
        public Mesh? Guides { get; init; }

        /// <summary>Resolved per-floor appearance, parallel to <see cref="Floors"/>.</summary>
        public required FloorTrackState[] TrackStates { get; init; }
        public required RuntimeTrackTimeline TrackRuntime { get; init; }
        public required HashSet<FloorMaterial>?[] TrackMaterialOptions { get; init; }
        public required LevelPlaybackSettings Settings { get; init; }
        public required BackdropTimeline Backdrop { get; init; }
        public required PlaybackTimeline Timeline { get; init; }
        public PlaybackEvent[]? EditorEvents { get; init; }
        public CameraTimeline? CameraTimeline { get; init; }
        public Filters.FilterTimeline? FilterTimeline { get; init; }
        public Filters.FilterTimeline? BackgroundFilterTimeline { get; init; }
        public SceneEffectTimeline? SceneEffects { get; init; }
        public ImageDecorationTimeline? Decorations { get; init; }
        public required int DecorationTexturePercent { get; init; }
        public required ManualTrack ManualTrack { get; init; }
        public string? MusicPath { get; init; }
        public DecodedAudio? Music { get; init; }
        public DecodedAudio? Hitsound { get; init; }
        public List<string> Warnings { get; init; } = [];

        /// <summary>Per-phase timings in milliseconds, in completion order.</summary>
        public List<(string Phase, double Milliseconds)> Timings { get; init; } = [];
    }

    /// <summary>Timeline data computed off the render thread.</summary>
    internal sealed record PlaybackTimeline
    {
        public double[] EntryTimes { get; init; } = [];
        public double[] FloorBpms { get; init; } = [];
        public double[] SweepStart { get; init; } = [];
        public double[] SweepDelta { get; init; } = [];
        public PlanetTrackFloor[] PlanetTrack { get; init; } = [];
        public Dictionary<int, PauseAngleCorrection>? PauseAngles { get; init; }
        public Dictionary<int, double>? PauseLocks { get; init; }
        public double LevelDuration { get; init; }
        public double OffsetSeconds { get; init; }
        public double SeparateCountdownLead { get; init; }

        public int CountdownTicks { get; init; } = 4;

        public double Pitch { get; init; } = 1.0;
        public double SnapBpm { get; init; } = 120.0;
    }

    /// <summary>Whether playback is running.</summary>
    public bool IsPlaying => _isPlaying;

    /// <summary>Current playback time in seconds.</summary>
    public double PlayTime => _playTime;

    /// <summary>Level duration in seconds.</summary>
    public double Duration => _duration;

    /// <summary>Current floor index.</summary>
    public int CurrentIndex => _currentIndex;

    /// <summary>
    /// 行星的连续角度（度，不做 360 折返）。手打判定需要它：官方用角度差而不是时间差。
    /// The planet's continuous angle in degrees, unwrapped. Manual judging needs it because the game
    /// measures an angle difference rather than a time difference.
    /// </summary>
    public double ContinuousAngleDegrees => _rotationAngleContinuous;

    /// <summary>当前砖块是否顺时针。</summary>
    public bool CurrentFloorIsCw => _isCw;

    /// <summary>手打输入状态。</summary>
    public PlayInput PlayInput { get; } = new();

    /// <summary>手打会话（判定与统计）。</summary>
    public PlaySession PlaySession { get; } = new();

    /// <summary>手打模式是否开启。</summary>
    public bool ManualPlayEnabled => PlayInput.Enabled;

    /// <summary>Playback progress in 0..1.</summary>
    public double Progress => _duration > 0 ? Math.Clamp(_playTime / _duration, 0.0, 1.0) : 0.0;

    public Level? Level => _level;

    /// <summary>Number of baked floors.</summary>
    public int FloorCount { get; private set; }

    /// <summary>Triangle count of the baked mesh.</summary>
    public long TriangleCount => _activeFloorChunks is { } chunks
        ? chunks.Sum(chunk => (long)chunk.Renderer.TriangleCount) : _officialRenderer?.TriangleCount ?? 0;

    public double Fps { get; private set; }

    /// <summary>Draw calls submitted for the game scene in the latest frame.</summary>
    public int DrawCalls { get; private set; }

    /// <summary>Floor triangles submitted in the latest frame.</summary>
    public long DrawnTriangles { get; private set; }

    public Camera2D Camera => _camera;

    public string Status => _loadError is null ? ManualStatus ?? _status : _status;

    public IReadOnlyList<string> Warnings => _warnings;

    private string? _audioMusicPath;

    private DecodedAudio? _audioMusic;

    private DecodedAudio? _audioHitsound;

    /// <summary>
    /// 离线模式下的拖尾采样间隔（度）。越小越圆滑，但历史点也越多。
    /// Trail sampling step in degrees while offline. Smaller is smoother but keeps more history.
    /// </summary>
    private const double TrailAngleStepDegrees = 15.0;

    private double _previousPlayTime;

    private int _previousCurrentIndex = -1;
    private double _offlineTrailTime = double.NaN;
    private int _offlineTrailFloor = -1;

    private bool _offlineSubSample;

    /// <summary>Per-phase timings of the last load, in milliseconds.</summary>
    public IReadOnlyList<(string Phase, double Milliseconds)> LoadTimings => _lastLoadTimings;

    /// <summary>Total duration of the last load, in milliseconds.</summary>
    public double LoadTotalMilliseconds => _lastLoadTotalMs;

    public void Dispose()
    {
        CancelPendingLoad();
        ClearScene();
        _filterBackend?.Dispose();
        _filterBackend = null;

        _shaderWatcher?.Dispose();
        _shaderWatcher = null;

        _music.Dispose();
        _hitsound.Dispose();
        ClearFloorChunks();
        _editorInsertedTrack?.Dispose();
        _officialRenderer?.Dispose();
        _officialRenderer = null;
        _circleRenderer?.Dispose();
        _circleRenderer = null;
        _spriteRenderer?.Dispose();
        _spriteRenderer = null;

        _uploader.Dispose();
        AudioEngine.Shutdown();
        _level = null;
    }
}

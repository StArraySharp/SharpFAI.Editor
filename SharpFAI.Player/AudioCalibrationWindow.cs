using System.Runtime.InteropServices;
using ImGui4NET;
using SharpFAI.Player.Engine;
using Thrust.Audio;

namespace SharpFAI.Player;

internal sealed unsafe class AudioCalibrationWindow : IDisposable
{
    private const string Title = "音画校准场景";
    private const double FirstBeat = 1.5;
    private const double BeatInterval = .5;
    private const int BeatCount = 24;
    private const int BeatsPerBar = 8;
    private readonly AudioClip _clicks = new() { Volume = .65f };
    private readonly TapMeasurement _measurement = new();
    private bool _opening;
    private bool _open;
    private int? _measuredDelay;

    public bool IsOpen => _opening || _open;
    public bool IsMeasuring { get; private set; }

    public void Open() => _opening = true;

    public void Tap(LevelRenderer renderer)
    {
        if (!IsMeasuring || !_clicks.IsPlaying || _measurement.AddTap(_clicks.Position) is not int delay)
            return;

        renderer.AudioDelayMs = delay;
        _measuredDelay = delay;
        IsMeasuring = false;
    }

    public void Draw(LevelRenderer renderer)
    {
        if (_opening)
        {
            _opening = false;
            _open = true;
            renderer.Pause();
            if (!_clicks.IsLoaded)
                _clicks.LoadDecoded(CreateClickTrack());
            BeginMeasurement();
            ImGui.OpenPopup(Title, 0);
        }

        if (!_open)
            return;

        ImGuiViewport* viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(new ImVec2
        {
            x = viewport->Pos.x + viewport->Size.x * .5f,
            y = viewport->Pos.y + viewport->Size.y * .5f,
        }, (int)ImGuiCond.ImGuiCond_Appearing, new ImVec2 { x = .5f, y = .5f });

        bool open = _open;
        int flags = (int)(ImGuiWindowFlags.ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags.ImGuiWindowFlags_NoSavedSettings);
        if (ImGui.BeginPopupModal(Title, ref open, flags) != 0)
        {
            ImGui.TextUnformatted(IsMeasuring ? "跟着节拍按空格，前两拍用于准备。"
                : "可跟拍自动校准，也可对照声音和闪光微调。");
            if (_clicks.IsLoaded)
            {
                _clicks.Update();
                if (!_clicks.IsPlaying)
                    Restart();
            }
            else
            {
                ImGui.TextUnformatted("音频设备不可用");
            }

            DrawFlash(IsMeasuring ? 0.0 : _clicks.Position + renderer.AudioDelayMs / 1000.0);
            if (IsMeasuring)
            {
                ImGui.ProgressBar(_measurement.Count / (float)TapMeasurement.RequiredTaps, new ImVec2 { x = -1f },
                    $"有效跟拍 {_measurement.Count} / {TapMeasurement.RequiredTaps}");
                if (ImGui.Button("取消跟拍", default) != 0)
                    IsMeasuring = false;
            }
            else
            {
                if (_measuredDelay is int measured)
                    ImGui.TextUnformatted($"跟拍结果 {measured:+0;-0;0} ms，已应用");
                DrawDelay(renderer);
                if (!_clicks.IsLoaded)
                    ImGui.BeginDisabled();
                if (ImGui.Button("开始跟拍", default) != 0)
                    BeginMeasurement();
                if (!_clicks.IsLoaded)
                    ImGui.EndDisabled();
            }
            ImGui.SameLine();
            if (ImGui.Button("完成", default) != 0)
            {
                open = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        else if (ImGui.IsPopupOpen(Title, 0) == 0)
        {
            open = false;
        }

        _open = open;
        if (!open)
        {
            IsMeasuring = false;
            _clicks.Stop();
        }
    }

    internal static void DrawDelay(LevelRenderer renderer)
    {
        ImGui.TextUnformatted("音频延迟 (ms)，正值让声音晚一点");
        int delay = renderer.AudioDelayMs;
        ImGui.SetNextItemWidth(-68f);
        if (ImGui.InputInt("##music-delay", ref delay, 1, 10) != 0)
            renderer.AudioDelayMs = delay;
        ImGui.SameLine();
        if (ImGui.Button("重置##music-delay", default) != 0)
            renderer.AudioDelayMs = 0;
    }

    private static void DrawFlash(double position)
    {
        double relative = position - FirstBeat;
        int beat = (int)Math.Floor(relative / BeatInterval);
        bool flash = beat >= 0 && beat < BeatCount && relative - beat * BeatInterval < .08;
        uint color = !flash ? 0xFF2A2828 : beat % BeatsPerBar == 0 ? 0xFF58D8FF : 0xFFF3F3F3;

        ImVec2 min = ImGui.GetCursorScreenPos();
        ImVec2 size = new() { x = 320f, y = 120f };
        ImGui.Dummy(size);
        ImVec2 max = new() { x = min.x + size.x, y = min.y + size.y };
        ImDrawList.AddRectFilled(ImGui.GetWindowDrawList(), min, max, color, 6f, 0);
    }

    private static DecodedAudio CreateClickTrack()
    {
        const int rate = 48000;
        int length = (int)((FirstBeat + BeatInterval * BeatCount + 1.0) * rate);
        byte[] pcm = new byte[length * sizeof(short)];
        Span<short> samples = MemoryMarshal.Cast<byte, short>(pcm.AsSpan());
        for (int beat = 0; beat < BeatCount; beat++)
        {
            int start = (int)((FirstBeat + BeatInterval * beat) * rate);
            double frequency = beat % BeatsPerBar == 0 ? 1680.0 : 980.0;
            for (int i = 0; i < rate / 40; i++)
            {
                double time = i / (double)rate;
                samples[start + i] = (short)(25000 * Math.Sin(Math.Tau * frequency * time) * Math.Exp(-200 * time));
            }
        }
        return new DecodedAudio { Pcm = pcm, SampleRate = rate, Channels = 1 };
    }

    private void BeginMeasurement()
    {
        _measurement.Reset();
        _measuredDelay = null;
        IsMeasuring = _clicks.IsLoaded;
        Restart();
    }

    private void Restart()
    {
        if (!_clicks.IsLoaded)
            return;
        _measurement.NewCycle();
        _clicks.Seek(0.0);
        _clicks.Play();
    }

    public void Dispose() => _clicks.Dispose();

    internal sealed class TapMeasurement
    {
        internal const int RequiredTaps = 12;
        private const int WarmupBeats = 2;
        private readonly double[] _errors = new double[RequiredTaps];
        private int _lastBeat = -1;

        public int Count { get; private set; }

        public void Reset()
        {
            Count = 0;
            NewCycle();
        }

        public void NewCycle() => _lastBeat = -1;

        public int? AddTap(double seconds)
        {
            double relative = seconds - FirstBeat;
            int nearestBeat = (int)Math.Round(relative / BeatInterval);
            double error = relative - nearestBeat * BeatInterval;
            if (nearestBeat < WarmupBeats || nearestBeat >= BeatCount || nearestBeat <= _lastBeat
                || Math.Abs(error) > .22 || Count == RequiredTaps)
            {
                return null;
            }

            _lastBeat = nearestBeat;
            _errors[Count++] = error;
            if (Count < RequiredTaps)
                return null;

            Array.Sort(_errors);
            double median = (_errors[RequiredTaps / 2 - 1] + _errors[RequiredTaps / 2]) / 2.0;
            return (int)Math.Round(-median * 1000.0);
        }
    }
}

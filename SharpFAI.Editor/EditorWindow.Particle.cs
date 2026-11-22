using ImGui4NET;
using Newtonsoft.Json.Linq;
using SharpFAI.Editor.Widgets;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private double _particleTime;
    private bool _particlePlaying;

    private void OpenParticle()
    {
        if (!IsSelectedParticle)
            return;
        _particle = _decoration;
        _showParticle = _focusParticle = _particlePlaying = true;
        _particleTime = 0;
    }

    private void DrawParticleEditor()
    {
        if (!_showParticle || _particle is not { Parent: not null } particle)
            return;
        if (_focusParticle)
        {
            ImGui.SetNextWindowFocus();
            _focusParticle = false;
        }
        ImGui.SetNextWindowSize(WidgetDraw.V2(500, 470), (int)ImGuiCond.ImGuiCond_FirstUseEver);
        if (BeginPanel(ParticleWindow, ref _showParticle))
        {
            ImGui.TextUnformatted(DecorationName(particle));
            if (ImGui.Button(_particlePlaying ? L.T("Pause") : L.T("Play"), default) != 0)
                _particlePlaying = !_particlePlaying;
            ImGui.SameLine();
            if (ImGui.Button(L.T("Stop"), default) != 0)
            {
                _particlePlaying = false;
                _particleTime = 0;
            }
            ImGui.SameLine();
            if (ImGui.Button(L.T("Reset"), default) != 0)
                _particleTime = 0;

            DrawParticlePreview(particle);
            if (ImGui.BeginTabBar("##particle-sections") != 0)
            {
                var fields = _catalog.Find("AddParticle")?.Fields ?? [];
                foreach (var (label, keys) in EditorCatalog.ParticleTabs)
                {
                    if (ImGui.BeginTabItem(L.T(label)) == 0)
                        continue;
                    if (ImGui.BeginChild("##particle-fields", default) != 0)
                        foreach (string key in keys)
                            if (Array.Find(fields, item => item.Key == key) is { } field)
                                DrawField(particle, field);
                    ImGui.EndChild();
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }
        ImGui.End();
    }

    private void DrawParticlePreview(JObject particle)
    {
        double speed = Math.Clamp(ParticleNumber(particle, "simulationSpeed", 100) / 100, 0, 30);
        if (_particlePlaying)
            _particleTime += ImGui.GetIO()->DeltaTime * speed;

        float width = Math.Max(1, ImGui.GetContentRegionAvail().x);
        float height = Math.Clamp(ImGui.GetContentRegionAvail().y * 0.36f, 65, 155);
        ImVec2 origin = ImGui.GetCursorScreenPos();
        ImVec2 end = WidgetDraw.V2(origin.x + width, origin.y + height);
        ImVec2 middle = WidgetDraw.V2(origin.x + width / 2, origin.y + height / 2);
        ImDrawList* draw = ImGui.GetWindowDrawList();
        WidgetDraw.Panel(draw, origin, end, WidgetDraw.U32(16, 19, 29), 4);
        ImDrawList.PushClipRect(draw, origin, end, 1);
        ImDrawList.AddLine(draw, WidgetDraw.V2(origin.x, middle.y), WidgetDraw.V2(end.x, middle.y), WidgetDraw.U32(35, 42, 59));
        ImDrawList.AddLine(draw, WidgetDraw.V2(middle.x, origin.y), WidgetDraw.V2(middle.x, end.y), WidgetDraw.U32(35, 42, 59));
        double maxLife = Math.Clamp(ParticleMean(particle, "particleLifetime", 10), 0.01, 60);
        double emission = Math.Clamp(ParticleMean(particle, "emissionRate", 10), 0, 500);
        double duration = Math.Max(0, ParticleNumber(particle, "playDuration", 5));
        bool loop = !bool.TryParse(particle["loop"]?.ToString(), out bool looping) || looping;
        double emitUntil = loop ? _particleTime : Math.Min(_particleTime, duration);
        int count = (int)Math.Clamp(ParticleNumber(particle, "maxParticles", 1000), 0, 480);
        double radius = Math.Clamp(ParticleNumber(particle, "shapeRadius", 1), 0, 50) * 14;
        double arc = Math.Clamp(ParticleNumber(particle, "arc", 360), 0, 360) * Math.PI / 180;
        double size = Math.Clamp(ParticleMean(particle, "particleSize", 100) / 32, 0.5, 24);
        uint seed = unchecked((uint)(int)Math.Clamp(ParticleNumber(particle, "randomSeed"), int.MinValue, int.MaxValue));
        string? shape = particle.Value<string>("shapeType");
        JArray? velocity = particle["velocity"] as JArray;
        JArray? minimum = velocity?.ElementAtOrDefault(0) as JArray;
        JArray? maximum = velocity?.ElementAtOrDefault(1) as JArray;
        long first = (long)Math.Floor(emitUntil * emission);
        for (int i = 0; i < count && emission > 0 && first - i > 0; i++)
        {
            double age = _particleTime - (first - i) / emission;
            if (age > maxLife)
                break;
            uint id = unchecked((uint)(first - i) + seed);
            double angle = RandomUnit(id) * arc;
            double distance = Math.Sqrt(RandomUnit(id ^ 0xe17a1465)) * radius;
            double x = Math.Cos(angle) * distance;
            double y = Math.Sin(angle) * distance;
            if (shape == "Rectangle")
            {
                x = (RandomUnit(id ^ 0xaa65a31f) * 2 - 1) * radius;
                y = (RandomUnit(id ^ 0x7bc2d490) * 2 - 1) * radius;
            }
            double vx = Interpolate(Component(minimum, 0), Component(maximum, 0), RandomUnit(id ^ 0x3beef839));
            double vy = Interpolate(Component(minimum, 1), Component(maximum, 1), RandomUnit(id ^ 0xad85f2a1));
            float alpha = (float)Math.Clamp(1 - age / maxLife, 0, 1);
            ImVec2 point = WidgetDraw.V2(middle.x + (float)(x + vx * age * 18), middle.y - (float)(y + vy * age * 18));
            ImDrawList.AddCircleFilled(draw, point, (float)size, WidgetDraw.U32(0.36f, 0.83f, 0.97f, alpha), 8);
        }
        ImDrawList.PopClipRect(draw);
        ImGui.Dummy(WidgetDraw.V2(width, height));
        WidgetDraw.TextDisabled(L.F("ParticlePreviewStatus", _particleTime));
    }

    private double ParticleNumber(JObject particle, string key, double fallback = 0) =>
        Number(particle[key] ?? _catalog.Find("AddParticle")?.Fields.FirstOrDefault(field => field.Key == key)?.Default, fallback);

    private double ParticleMean(JObject particle, string key, double fallback)
    {
        JToken? value = particle[key] ?? _catalog.Find("AddParticle")?.Fields.FirstOrDefault(field => field.Key == key)?.Default;
        return value is JArray pair ? (Component(pair, 0, fallback) + Component(pair, 1, fallback)) / 2 : Number(value, fallback);
    }

    private static double Interpolate(double start, double end, double t) => start + (end - start) * t;

    private static double RandomUnit(uint value)
    {
        value ^= value >> 16;
        value *= 0x7feb352d;
        value ^= value >> 15;
        value *= 0x846ca68b;
        value ^= value >> 16;
        return value / (double)uint.MaxValue;
    }
}

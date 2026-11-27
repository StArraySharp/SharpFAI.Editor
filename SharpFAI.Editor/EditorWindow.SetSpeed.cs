using System.Globalization;
using ImGui4NET;
using Newtonsoft.Json.Linq;
using SharpFAI.Editor.Widgets;
using SharpFAI.Player.Engine;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private static readonly EditorField SetSpeedEffectiveBpmField = new("effectiveBpm", "Float", Unit: "bpm", Minimum: double.Epsilon);

    private void DrawSetSpeedEffectiveBpm(JObject target)
    {
        ImGui.PushID(SetSpeedEffectiveBpmField.Key);
        string label = L.T("SetSpeedEffectiveBpm");
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        AlignScalarPropertyValue(ImGui.CalcTextSize(label).x);

        int floor = EditorDocument.Floor(target);
        double pitch = LevelRenderer.ReadPlaybackPitch(_document.Settings);
        if (HasCurrentPlayback && _playback?.EditorTiming is { } timing &&
            timing.SpeedContexts?.ContainsKey(floor) == true &&
            timing.TempoAt(floor, pitch) is { Effective: double effective })
        {
            var value = new JValue(effective.ToString("G12", CultureInfo.InvariantCulture));
            DrawNumericInput(target, SetSpeedEffectiveBpmField, value, textOnly: true, tooltip: L.T("SetSpeedEffectiveBpmHelp"));
        }
        else
        {
            WidgetDraw.TextDisabled($"— {NumberUnit(SetSpeedEffectiveBpmField.Unit)}");
            WidgetDraw.ItemTooltip(L.T("SetSpeedEffectiveBpmUnavailable"));
        }
        ImGui.PopID();
    }

    private bool TryCommitSetSpeedEffectiveBpm(JObject target, double goal)
    {
        if (target.Parent is null || !HasCurrentPlayback || _playback?.EditorTiming is not { } timing)
            return false;
        int floor = EditorDocument.Floor(target);
        double pitch = LevelRenderer.ReadPlaybackPitch(_document.Settings);
        if (!SetSpeedTempo.TryConvert(timing, floor, pitch, _document.Events(floor), target, goal, out var conversion))
            return false;
        var value = new JValue(conversion.Value);
        var field = _catalog.Find("SetSpeed")?.Fields.FirstOrDefault(item => item.Key == conversion.Key)
            ?? EditorCatalog.Infer(conversion.Key, value, "SetSpeed");
        if (conversion.Value < field.Minimum || conversion.Value > field.Maximum)
            return false;
        SetProperty(target, conversion.Key, value);
        return true;
    }
}

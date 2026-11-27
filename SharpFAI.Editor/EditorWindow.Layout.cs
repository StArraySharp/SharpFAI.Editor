using ImGui4NET;
using SharpFAI.Player;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private const string PanelComment = "; SharpFAI panels=";
    private readonly ImGuiLayoutStore _imguiLayout = ImGuiLayoutStore.ForApplication(editor: true);
    private bool _restoreLayoutDocks;
    private bool _preserveRestoredFooterSize;

    [Flags]
    private enum LayoutPanels
    {
        Settings = 1,
        Decorations = 2,
        Inspector = 4,
        Events = 8,
        Timeline = 16,
        Shortcuts = 32,
        Spectrum = 64,
        Performance = 128,
    }

    private void LoadEditorLayout()
    {
        if (_imguiLayout.Load() is not { } layout) return;
        _resetLayout = false;
        _restoreLayoutDocks = true;
        foreach (var line in layout.AsSpan().EnumerateLines())
        {
            if (!line.StartsWith(PanelComment, StringComparison.Ordinal) ||
                !Enum.TryParse(line[PanelComment.Length..], out LayoutPanels panels)) continue;
            _showSettings = panels.HasFlag(LayoutPanels.Settings);
            _showDecorations = panels.HasFlag(LayoutPanels.Decorations);
            _showInspector = panels.HasFlag(LayoutPanels.Inspector);
            _showPalette = panels.HasFlag(LayoutPanels.Events);
            _showTimeline = panels.HasFlag(LayoutPanels.Timeline);
            _showShortcuts = panels.HasFlag(LayoutPanels.Shortcuts);
            _audioMonitor.IsOpen = panels.HasFlag(LayoutPanels.Spectrum);
            _playbackPerformance.IsOpen = panels.HasFlag(LayoutPanels.Performance);
            break;
        }
    }

    private void SaveEditorLayout()
    {
        LayoutPanels panels = (_showSettings ? LayoutPanels.Settings : 0) |
                              (_showDecorations ? LayoutPanels.Decorations : 0) |
                              (_showInspector ? LayoutPanels.Inspector : 0) |
                              (_showPalette ? LayoutPanels.Events : 0) |
                              (_showTimeline ? LayoutPanels.Timeline : 0) |
                              (_showShortcuts ? LayoutPanels.Shortcuts : 0) |
                              (_audioMonitor.IsOpen ? LayoutPanels.Spectrum : 0) |
                              (_playbackPerformance.IsOpen ? LayoutPanels.Performance : 0);
        _imguiLayout.Save(PanelComment + panels.ToString("D"));
    }

    private void RestoreDockspaceSlots(ImGuiDockNode* root)
    {
        _restoreLayoutDocks = false;
        uint center = SavedDockId(CanvasWindow);
        if (center == 0) center = root->CentralNode == null ? root->ID : root->CentralNode->ID;
        uint left = SavedDockId(SettingsWindow);
        if (left == 0) left = SavedDockId(DecorationsWindow);
        uint right = SavedDockId(InspectorWindow);
        uint bottom = SavedDockId(PaletteWindow);
        if (root->SplitAxis == ImGuiAxis.ImGuiAxis_Y && root->ChildNodes.e1 != null &&
            root->ChildNodes.e1->ID == bottom)
        {
            _footerDock = bottom;
            _preserveRestoredFooterSize = true;
        }
        _modUi?.SetDocks(left == 0 ? center : left, right == 0 ? center : right,
            bottom == 0 ? center : bottom, center);
    }

    private static uint SavedDockId(string title)
    {
        var settings = ImGui.FindWindowSettingsByID(ImGui.ImHashStr(title, 0, 0));
        return settings == null ? 0 : settings->DockId;
    }
}

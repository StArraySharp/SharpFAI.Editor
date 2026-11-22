using ImGui4NET;
using SharpFAI.Editor.Widgets;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private LevelArchive? _archiveChoices;
    private bool _openArchivePicker;

    private void OpenLevelPath(string path)
    {
        if (!LevelArchive.IsZip(path))
        {
            SetDocument(new(path));
            return;
        }

        LevelArchive archive = LevelArchive.Extract(path);
        if (archive.Levels.Count == 1)
            SetDocument(new(Path.Combine(archive.DirectoryPath, archive.Levels[0])));
        else
            ChooseArchiveLevel(archive);
    }

    internal void ChooseArchiveLevel(LevelArchive archive)
    {
        _archiveChoices = archive;
        _openArchivePicker = true;
    }

    private void DrawArchivePicker()
    {
        if (_archiveChoices is not { } archive)
            return;

        string title = L.T("ChooseArchiveLevel") + "###archive-level-picker";
        if (_openArchivePicker)
        {
            ImGui.OpenPopup(title);
            _openArchivePicker = false;
        }
        if (ImGui.BeginPopupModal(title) == 0)
            return;

        string? selection = null;
        float height = Math.Clamp(archive.Levels.Count, 3, 10) * ImGui.GetFrameHeightWithSpacing();
        if (ImGui.BeginChild("##archive-levels", WidgetDraw.V2(600, height), 0,
            (int)ImGuiWindowFlags.ImGuiWindowFlags_HorizontalScrollbar) != 0)
        {
            for (int i = 0; i < archive.Levels.Count; i++)
            {
                // Paths may contain ImGui label markers, so render them as text beside the action.
                if (ImGui.Button($"{i + 1}##archive-level", WidgetDraw.V2(60, 0)) != 0)
                    selection = archive.Levels[i];
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(archive.Levels[i]);
            }
        }
        ImGui.EndChild();

        if (ImGui.Button(L.T("Cancel"), default) != 0 || Pressed(ImGuiKey.ImGuiKey_Escape) || selection is not null)
        {
            _archiveChoices = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();

        if (selection is not null)
        {
            try { SetDocument(new(Path.Combine(archive.DirectoryPath, selection))); }
            catch (Exception error) { ShowError(error); }
        }
    }
}

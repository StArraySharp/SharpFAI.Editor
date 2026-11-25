using ImGui4NET;
using SharpFAI.Editor.Widgets;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private readonly LastOpenedChart _recentChart = new(Path.Combine(EditorFileStorage.DataDirectory, "last-chart.txt"));
    private readonly EditorAutoBackup _autoBackup = new(Path.Combine(EditorFileStorage.DataDirectory, "Backups"), TimeProvider.System);
    private string? _lastChartPath;

    private void RememberOpenedChart()
    {
        if (_document.Path is { } path)
        {
            _lastChartPath = path;
            _recentChart.Remember(path);
        }
    }

    private void DrawOpenLastChartMenuItem()
    {
        if (ImGui.MenuItem(L.T("OpenLastChart"), "Ctrl+Shift+O", 0, (byte)(_lastChartPath is null ? 0 : 1)) != 0)
            OpenLastChart();
        if (_lastChartPath is { } path)
            WidgetDraw.ItemTooltip(path);
    }

    private void OpenLastChart()
    {
        if (_lastChartPath is not { } path)
            return;
        Confirm(() =>
        {
            try
            {
                if (!File.Exists(path))
                    throw new FileNotFoundException(L.F("LastChartMissing", path));
                OpenLevelPath(path);
            }
            catch (Exception error) { ShowError(error); }
        });
    }
}

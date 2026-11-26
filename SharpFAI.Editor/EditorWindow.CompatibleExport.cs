using ImGui4NET;
using NativeFileDialog;
using SharpFAI.Editor.Mods;
using SharpFAI.ModApi;
using SharpFAI.Player.ModSupport;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private Task<CompatibleChart>? _compatibilityTask;
    private CancellationTokenSource? _compatibilityCancellation;
    private CompatibleChart? _compatibleChart;
    private bool _openCompatibleExport;
    private string? _compatibilitySource;
    private const string CompatiblePopup = "###compatible-chart";

    private void PrepareCompatibleExport()
    {
        if (_compatibilityTask is not null) return;
        FinishNumericEdit();
        var snapshot = _document.SnapshotForPlayback();
        _compatibilitySource = _document.PlaybackPath;
        var converters = _modUi?.Host.Entries<CompatibilityConverter>(UiHost.Editor).ToArray() ?? [];
        _compatibilityCancellation = new();
        var token = _compatibilityCancellation.Token;
        _compatibilityTask = Task.Run(() => CompatibleChartExport.CreateAsync(snapshot, converters, token), token);
        _openCompatibleExport = true;
    }

    private void CancelCompatibleExport()
    {
        var cancellation = _compatibilityCancellation;
        var task = _compatibilityTask;
        _compatibilityTask = null;
        _compatibilityCancellation = null;
        _compatibleChart = null;
        if (cancellation is null) return;
        cancellation.Cancel();
        if (task is null) cancellation.Dispose();
        else _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            cancellation.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void DrawCompatibleExport()
    {
        if (_compatibilityTask is { IsCompleted: true } task)
        {
            _compatibilityTask = null;
            if (task.IsCompletedSuccessfully) _compatibleChart = task.Result;
            else if (task.Exception is { } error) ShowError(error.GetBaseException());
            _compatibilityCancellation?.Dispose();
            _compatibilityCancellation = null;
        }
        string title = (L.IsChinese ? "导出原版兼容副本" : "Export vanilla-compatible copy") + CompatiblePopup;
        if (_openCompatibleExport)
        {
            _openCompatibleExport = false;
            ImGui.OpenPopup(title);
        }
        if (ImGui.BeginPopupModal(title, 0) == 0) return;
        if (_compatibilityTask is not null)
            ImGui.TextUnformatted(L.IsChinese ? "正在转换 Mod 事件…" : "Converting Mod events…");
        else if (_compatibleChart is { } result)
        {
            ImGui.TextUnformatted(L.IsChinese ? $"已转换 {result.Converted} 个 Mod 事件" : $"Converted {result.Converted} Mod events");
            if (result.Notices.Count > 0)
            {
                ImGui.TextUnformatted(L.IsChinese ? "省略或近似转换的项目" : "Omitted or approximate items");
                if (ImGui.BeginChild("##compatible-chart-notices", new ImVec2 { x = 480, y = 160 }) != 0)
                    foreach (string notice in result.Notices) ImGui.TextUnformatted(notice);
                ImGui.EndChild();
            }
            if (ImGui.Button(L.T("Save"), default) != 0 && WriteCompatibleChart(result))
            {
                _compatibleChart = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
        }
        if (ImGui.Button(L.T("Cancel"), default) != 0)
        {
            CancelCompatibleExport();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private bool WriteCompatibleChart(CompatibleChart result)
    {
        try
        {
            string source = Path.GetFullPath(_compatibilitySource!);
            string? file = NativeFileDialog.NativeFileDialog.GetFileDialog()?.SaveFile(L.IsChinese ? "保存原版兼容副本" : "Save vanilla-compatible copy",
                Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + "-vanilla.adofai",
                new SaveFileFilter { Filter = new() { ["ADOFAI"] = "adofai" } });
            if (string.IsNullOrWhiteSpace(file)) return false;
            if (!Path.HasExtension(file)) file += ".adofai";
            string destination = Path.GetFullPath(file);
            if (string.Equals(source, destination, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException(L.IsChinese ? "请为兼容副本选择另一个文件。" : "Choose a separate file for the compatible copy.");
            EditorFileStorage.SaveChartWithAssets(destination, result.Level, Path.GetDirectoryName(source)!);
            return true;
        }
        catch (Exception error) { ShowError(error); return false; }
    }
}

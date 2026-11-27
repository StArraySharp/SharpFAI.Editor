using ImGui4NET;

namespace SharpFAI.Player;

internal sealed unsafe class ApplicationUpdateWindow : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _downloadCancellation;
    private Task<ApplicationUpdater.Release?>? _checking;
    private Task<ApplicationUpdater.Staged>? _downloading;
    private ApplicationUpdater.Release? _offer;
    private ApplicationUpdater.Staged? _ready;
    private string? _error = ApplicationUpdater.RestartError;
    private bool _started, _open, _restartRequested, _upToDate;
    private long _bytes;

    public void CheckNow()
    {
        _open = true;
        if (_downloading is null && _ready is null) StartCheck();
    }

    public void Draw(bool chinese, bool canRestart, Action requestRestart)
    {
        if (!_started)
        {
            _open |= _error is not null;
            StartCheck(clearError: false);
        }
        if (_checking is { IsCompleted: true } check)
        {
            _checking = null;
            if (check.IsCompletedSuccessfully)
            {
                _offer = check.Result;
                _upToDate = _offer is null;
                _open |= _offer is not null;
            }
            else _error ??= check.Exception?.GetBaseException().Message ?? Tr("检查更新超时。", "The update check timed out.");
        }
        if (_downloading is { IsCompleted: true } download)
        {
            _downloading = null;
            _downloadCancellation?.Dispose();
            _downloadCancellation = null;
            if (download.IsCompletedSuccessfully)
            {
                _ready = download.Result;
                _restartRequested = false;
                _open = true;
            }
            else if (download.IsFaulted) _error = download.Exception?.GetBaseException().Message;
        }
        if (_ready is not null && !_restartRequested && canRestart)
        {
            _restartRequested = true;
            requestRestart();
        }
        if (!_open) return;

        ImGui.SetNextWindowSize(new ImVec2 { x = 440 }, (int)ImGuiCond.ImGuiCond_FirstUseEver);
        if (ImGui.Begin(Tr("软件更新", "Software Update") + "###application-update", ref _open,
                (int)(ImGuiWindowFlags.ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags.ImGuiWindowFlags_NoCollapse)) != 0)
        {
            ImGui.TextUnformatted(Tr("当前版本 ", "Current version ") + ApplicationUpdater.CurrentVersion);
            if (_error is not null) ImGui.TextWrapped(_error);
            if (_checking is not null)
                ImGui.TextUnformatted(Tr("正在检查 GitHub 更新…", "Checking GitHub for updates…"));
            else if (_downloading is not null && _offer is { } downloading)
            {
                float progress = (float)((double)Interlocked.Read(ref _bytes) / downloading.Size);
                ImGui.ProgressBar(progress, new ImVec2 { x = -1 }, $"{progress:P0}");
                if (ImGui.Button(Tr("取消下载", "Cancel download"), default) != 0) _downloadCancellation?.Cancel();
            }
            else if (_ready is not null)
            {
                ImGui.TextUnformatted(Tr("下载和校验完成。", "Download verified."));
                ImGui.BeginDisabled((byte)(canRestart ? 0 : 1));
                if (ImGui.Button(Tr("安装并重启", "Install and restart"), default) != 0) requestRestart();
                ImGui.EndDisabled();
            }
            else if (_offer is { } offer)
            {
                ImGui.TextUnformatted($"{Tr("可用更新", "Update available")} {offer.Version} · {offer.Size / (1024d * 1024):F1} MiB");
                if (ApplicationUpdater.Executable is null)
                    ImGui.TextWrapped(Tr("请使用发布版可执行文件安装更新。", "Run the published executable to install updates."));
                else
                {
                    ImGui.BeginDisabled((byte)(canRestart ? 0 : 1));
                    if (ImGui.Button(Tr("下载、安装并重启", "Download, install and restart"), default) != 0)
                    {
                        _error = null;
                        Interlocked.Exchange(ref _bytes, 0);
                        _downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                        _downloading = ApplicationUpdater.DownloadAsync(offer, bytes => Interlocked.Exchange(ref _bytes, bytes),
                            _downloadCancellation.Token);
                    }
                    ImGui.EndDisabled();
                }
                if (ImGui.Button(Tr("稍后", "Later"), default) != 0) _open = false;
            }
            else if (_upToDate)
                ImGui.TextUnformatted(Tr("已是最新版本。", "You are up to date."));
        }
        ImGui.End();

        string Tr(string zh, string en) => chinese ? zh : en;
    }

    public bool Install(string[] arguments)
    {
        if (_ready is not { } ready) return false;
        try
        {
            ApplicationUpdater.StartInstaller(ready, arguments);
            _ready = null;
            return true;
        }
        catch (Exception error)
        {
            _error = error.GetBaseException().Message;
            _open = true;
            return false;
        }
    }

    private void StartCheck(bool clearError = true)
    {
        if (_checking is not null) return;
        _started = true;
        _offer = null;
        _upToDate = false;
        if (clearError) _error = null;
        _checking = ApplicationUpdater.CheckAsync(_lifetime.Token);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        if (_downloading is { } transfer)
            _ = transfer.ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully) task.Result.Delete();
                else _ = task.Exception;
            }, TaskScheduler.Default);
        _downloadCancellation?.Dispose();
        _lifetime.Dispose();
        _ready?.Delete();
    }
}

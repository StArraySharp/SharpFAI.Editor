using SharpFAI.Player;

namespace SharpFAI.Editor;

internal sealed partial class EditorWindow
{
    private ApplicationUpdateWindow? _applicationUpdate;
    internal Func<string?, string[]>? RestartArguments { get; init; }

    private void DrawApplicationUpdate()
    {
        if (RestartArguments is not { } arguments) return;
        _applicationUpdate ??= new();
        bool ready = _pending is null && _error is null && _videoExport?.Request is null && _modFrameJob is null &&
            !_receivedRemoteInput && _remoteButtons == 0 && _remoteKeys.Count == 0;
        _applicationUpdate.Draw(L.IsChinese, ready, () =>
        {
            FinishNumericEdit();
            Confirm(() =>
            {
                if (_applicationUpdate.Install(arguments(_document.Path)))
                {
                    _allowClose = true;
                    Close();
                }
            });
        });
    }
}

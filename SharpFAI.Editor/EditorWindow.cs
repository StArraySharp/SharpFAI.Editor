using System.Diagnostics;
using ImGui4NET;
using SharpFAI.ModApi;
using ImGui4NET.Backend.OpenGL3;
using NativeFileDialog;
using Newtonsoft.Json.Linq;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SharpFAI.Editor.Widgets;
using SharpFAI.Player.Engine;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow : GameWindow
{
    private static string SettingsWindow => L.T("Settings") + "###settings";
    private static string DecorationsWindow => L.T("Decorations") + "###decorations";
    private static string CanvasWindow => L.T("Chart") + "###canvas";
    private static string InspectorWindow => L.T("Inspector") + "###inspector";
    private string PaletteWindow => L.T(_showTimeline ? _showPalette ? "TimelineAndEvents" : "Timeline" : "Palette") + "###palette";
    private static string ParticleWindow => L.T("ParticleEditor") + "###particle";

    private readonly EditorCatalog _catalog;
    private EditorDocument _document;
    private ImGuiContext* _context;
    private SharpFAI.Player.WindowInputMethod? _inputMethod;
    private bool _backendInitialized;
    private bool _resetLayout = true;
    private bool _showSettings = true;
    private bool _showDecorations = true;
    private bool _showInspector = true;
    private bool _showPalette = true;
    private bool _showShortcuts;
    private bool _canvasOnly;
    private bool _canvasOnlyToggleRequested;
    private bool _showBrickNumbers = true;
    private bool _openFindFloor;
    private int _findFloor;
    private bool _allowClose;
    private bool _closeRequested;
    private bool _openConfirm;
    private Action? _pending;
    private string? _error;
    private bool _openError;
    private readonly SharpFAI.Player.MenuBarFrameRate _menuBarFrameRate = new();
    private int _floor;
    private int _category;
    private string? _eventType;
    private int _eventPage;
    private bool _inspectDecoration;
    private JObject? _decoration;
    private JObject? _particle;
    private bool _showParticle;
    private bool _focusDecorations;
    private bool _focusParticle;
    private static readonly Dictionary<string, List<string>> LevelFilter = new() { ["ADOFAI / ZIP"] = ["adofai", "zip"] };

    public EditorWindow(NativeWindowSettings settings, EditorCatalog catalog, EditorDocument document, string? commandLineLanguage)
        : base(GameWindowSettings.Default, settings)
    {
        _catalog = catalog;
        _document = document;
        _commandLineLanguage = commandLineLanguage;
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        InitializeMods();
        _context = ImGui.CreateContext();
        ImGui.SetCurrentContext(_context);
        ImGuiIO* io = ImGui.GetIO();
        io->IniFilename = null;
        io->LogFilename = null;
        io->ConfigFlags |= (int)ImGuiConfigFlags.ImGuiConfigFlags_DockingEnable;
        _backendInitialized = ImGui_ImplOpenGL3.Init("#version 330") != 0;
        if (!_backendInitialized)
            throw new InvalidOperationException(L.T("ImGuiInitError"));

        ImGui.StyleColorsDark();
        ImGuiStyle* style = ImGui.GetStyle();
        style->WindowRounding = 4;
        style->FrameRounding = 4;
        style->TabRounding = 3;
        style->WindowPadding = WidgetDraw.V2(10, 8);
        style->FramePadding = WidgetDraw.V2(7, 4);
        ApplyTheme(style);
        ApplicationFont.LoadInterfaceFont(io, 17f);
        LoadApplicationSettings();
        _lastChartPath = _recentChart.Read();
        RememberOpenedChart();
        InitializePlayback();
        _inputMethod = SharpFAI.Player.WindowInputMethod.Create(WindowPtr);
        TextInput += e => ImGuiIO.AddInputCharacter(io, (uint)e.Unicode);
        MouseWheel += e =>
        {
            ReleaseWebInput();
            FeedEditorWheel(io, e.OffsetX, e.OffsetY,
                KeyboardState.IsKeyDown(Keys.LeftControl) || KeyboardState.IsKeyDown(Keys.RightControl),
                KeyboardState.IsKeyDown(Keys.LeftShift) || KeyboardState.IsKeyDown(Keys.RightShift));
        };
        MouseMove += _ => FeedMouse();
        MouseDown += e => { FeedMouse(); FeedPlaybackMouse(e.Button, true); };
        MouseUp += e => { FeedMouse(); FeedPlaybackMouse(e.Button, false); };
        KeyDown += e => { ReleaseWebInput(); FeedEditorKey(e, true); };
        KeyUp += e => { ReleaseWebInput(); FeedEditorKey(e, false); };
        UpdateTitle();
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        TickMods();
        ApplyPendingApplicationSettings();
        _autoBackup.Update(_document);
        if (ClientSize.X <= 0 || ClientSize.Y <= 0)
            return;
        UpdateVideoRender();
        ImGuiIO* io = ImGui.GetIO();
        io->DisplaySize = WidgetDraw.V2(ClientSize.X, ClientSize.Y);
        io->DisplayFramebufferScale = WidgetDraw.V2(FramebufferSize.X / (float)ClientSize.X, FramebufferSize.Y / (float)ClientSize.Y);
        io->DeltaTime = Math.Clamp((float)args.Time, 0.0001f, 0.1f);
        ImGui_ImplOpenGL3.NewFrame();
        if (WebMirror is not null) FeedWebInput(io);
        _settingsWindow.BeginFrame(io);
        ImGui.NewFrame();
        if (_closeRequested)
        {
            _closeRequested = false;
            SetCanvasOnly(false);
            RequestClose();
        }
        ApplyViewShortcuts();
        if (_canvasOnly)
            ImGui.DockSpaceOverViewport(0, ImGui.GetMainViewport(), (int)ImGuiDockNodeFlags.ImGuiDockNodeFlags_KeepAliveOnly);
        else
        {
            _menuBarFrameRate.Sample(args.Time);
            DrawMenu();
            DrawDockspace();
        }
        if (_modUi?.HandleShortcuts(ShortcutContextBlocked || _canvasOnly) != true && _modUi?.HasFocus != true)
            HandleShortcuts();
        _numericDrawn = false;
        UpdatePlayback();
        if (_canvasOnly)
            DrawCanvasOnly();
        else
        {
            DrawSettings();
            DrawDecorations();
            DrawCanvas();
        }
        _speedWheels.Clear();
        // Starting after the canvas lets it apply a wheel step from this frame first.
        if (_queuedRateShortcut is { } rate && rate != PlaybackRate.Configured)
        {
            _queuedRateShortcut = null;
            StartFromShortcut(rate);
        }
        if (!_canvasOnly)
        {
            DrawInspector();
            DrawPalette();
            DrawParticleEditor();
            DrawShortcuts();
            DrawFindComment();
            DrawVideoRender();
            DrawDialogs();
            DrawCompatibleExport();
            _settingsWindow.Draw(L.IsChinese, editor: true, SharpFAI.Player.ApplicationSettings.EditorDefaults,
                TryApplyApplicationSettings, DrawModSettings);
            if (_playback is { } renderer)
                _audioCalibration.Draw(renderer, L.IsChinese);
            DrawSaveNotice();
        }
        _modUi?.DrawWindows(_settingsWindow.IsOpen);
        if (!_numericDrawn)
            FinishNumericEdit();
        ApplyConfiguredPlaybackShortcut();
        _inputMethod?.Update(IsFocused, !_audioCalibration.IsOpen && (_playbackView switch
        {
            PlaybackView.Edit => _hasFloorSelection || _inspectDecoration,
            PlaybackView.Preview => _playback is { SelectedFloor: >= 0 },
            _ => true,
        }));
        UpdateTitle();

        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
        GL.ClearColor(0.059f, 0.065f, 0.083f, 1);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        ImGui.Render();
        ImGui_ImplOpenGL3.RenderDrawData(ImGui.GetDrawData());
        WebMirror?.CaptureFrame(FramebufferSize.X, FramebufferSize.Y);
        SwapBuffers();
    }

    private static void ApplyTheme(ImGuiStyle* style)
    {
        ImVec4* colors = (ImVec4*)&style->Colors;
        colors[(int)ImGuiCol.ImGuiCol_WindowBg] = WidgetDraw.V4(0.095f, 0.105f, 0.15f, 1);
        colors[(int)ImGuiCol.ImGuiCol_MenuBarBg] = WidgetDraw.V4(0.075f, 0.082f, 0.115f, 1);
        colors[(int)ImGuiCol.ImGuiCol_FrameBg] = WidgetDraw.V4(0.13f, 0.15f, 0.21f, 1);
        colors[(int)ImGuiCol.ImGuiCol_Button] = WidgetDraw.V4(0.16f, 0.20f, 0.27f, 1);
        colors[(int)ImGuiCol.ImGuiCol_ButtonHovered] = WidgetDraw.V4(0.18f, 0.36f, 0.42f, 1);
        colors[(int)ImGuiCol.ImGuiCol_Header] = WidgetDraw.V4(0.13f, 0.32f, 0.37f, 1);
        colors[(int)ImGuiCol.ImGuiCol_HeaderHovered] = WidgetDraw.V4(0.18f, 0.36f, 0.42f, 1);
        colors[(int)ImGuiCol.ImGuiCol_TabSelected] = WidgetDraw.V4(0.13f, 0.32f, 0.37f, 1);
        colors[(int)ImGuiCol.ImGuiCol_CheckMark] = WidgetDraw.V4(0.37f, 0.87f, 0.81f, 1);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_allowClose && _document.Modified)
        {
            e.Cancel = true;
            _closeRequested = true;
        }
    }

    protected override void OnFocusedChanged(FocusedChangedEventArgs e)
    {
        base.OnFocusedChanged(e);
        if (_context != null)
            ImGuiIO.AddFocusEvent(ImGui.GetIO(), (byte)(e.IsFocused ? 1 : 0));
        if (!e.IsFocused)
        {
            _inputMethod?.Restore();
            ResetCanvasGesture();
            _dragDecoration = null;
            ReleasePlaybackInput();
        }
    }

    protected override void OnUnload()
    {
        CancelCompatibleExport();
        _autoBackup.CompleteWrite(wait: true);
        _displayFrameLimit.Dispose();
        _inputMethod?.Dispose();
        _audioCalibration.Dispose();
        DisposeVideoRender();
        DisposePlayback();
        _modUi?.Shutdown();
        DisposeRichLevelTextPreviews();
        if (_backendInitialized)
            ImGui_ImplOpenGL3.Shutdown();
        if (_context != null)
            ImGui.DestroyContext(_context);
        base.OnUnload();
    }

    private void DrawDockspace()
    {
        ImGuiViewport* viewport = ImGui.GetMainViewport();
        uint dock = ImGui.DockSpaceOverViewport(0, viewport);
        if (!_resetLayout)
            return;

        _resetLayout = false;
        ImGui.DockBuilderRemoveNode(dock);
        ImGui.DockBuilderAddNode(dock, (int)ImGuiDockNodeFlagsPrivate.ImGuiDockNodeFlags_DockSpace);
        ImGui.DockBuilderSetNodePos(dock, viewport->WorkPos);
        ImGui.DockBuilderSetNodeSize(dock, viewport->WorkSize);
        uint canvas = dock;
        float footerHeight = TimelineRulerHeight + TimelineFloorHeight +
            5 * ImGui.GetFrameHeightWithSpacing() + EventButtonSide + 2 * ImGui.GetStyle()->WindowPadding.y;
        float footerRatio = Math.Clamp(footerHeight / Math.Max(1, viewport->WorkSize.y), .18f, .46f);
        uint footer = ImGui.DockBuilderSplitNode(canvas, ImGuiDir.ImGuiDir_Down, footerRatio, null, &canvas);
        uint left = ImGui.DockBuilderSplitNode(canvas, ImGuiDir.ImGuiDir_Left, 0.22f, null, &canvas);
        uint right = ImGui.DockBuilderSplitNode(canvas, ImGuiDir.ImGuiDir_Right, 0.30f, null, &canvas);
        ImGui.DockBuilderDockWindow(CanvasWindow, canvas);
        ImGui.DockBuilderDockWindow(SettingsWindow, left);
        ImGui.DockBuilderDockWindow(DecorationsWindow, left);
        ImGui.DockBuilderDockWindow(InspectorWindow, right);
        ImGui.DockBuilderDockWindow(PaletteWindow, footer);
        ImGui.DockBuilderFinish(dock);
        _modUi?.SetDocks(left, right, footer, canvas);
    }

    private void DrawMenu()
    {
        if (ImGui.BeginMainMenuBar() == 0)
            return;
        if (ImGui.BeginMenu(L.T("MenuFile")) != 0)
        {
            if (ImGui.MenuItem(L.T("New"), "Ctrl+N") != 0) Confirm(() => SetDocument(new()));
            if (ImGui.MenuItem(L.T("Open"), "Ctrl+O") != 0) Confirm(Open);
            DrawOpenLastChartMenuItem();
            if (ImGui.MenuItem(L.T("Save"), "Ctrl+S") != 0) Save();
            if (ImGui.MenuItem(L.T("SaveAs"), "Ctrl+Shift+S") != 0) Save(true);
            if (ImGui.MenuItem(L.IsChinese ? "导出原版兼容副本…" : "Export vanilla-compatible copy…") != 0)
                PrepareCompatibleExport();
            _modUi?.DrawItems(MenuSlot.File);
            ImGui.Separator();
            if (ImGui.MenuItem(L.T("Exit")) != 0) RequestClose();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu(L.T("MenuEdit")) != 0)
        {
            if (ImGui.MenuItem(L.T("Undo"), "Ctrl+Z", 0, (byte)(_document.CanUndo ? 1 : 0)) != 0) Undo();
            if (ImGui.MenuItem(L.T("Redo"), "Ctrl+Y", 0, (byte)(_document.CanRedo ? 1 : 0)) != 0) Redo();
            if (ImGui.MenuItem(L.T("Copy"), "Ctrl+C") != 0) CopySelection(false, false, false);
            if (ImGui.MenuItem(L.T("Cut"), "Ctrl+X") != 0) CopySelection(false, false, true);
            if (ImGui.MenuItem(L.T("Paste"), "Ctrl+V") != 0) PasteSelection(false, true);
            if (ImGui.MenuItem(L.T("DeleteSelection"), "Delete") != 0) DeleteSelection(backwards: false);
            _modUi?.DrawItems(MenuSlot.Edit);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu(L.T("MenuView")) != 0)
        {
            if (ImGui.MenuItem(L.T("Fullscreen"), "Alt+Enter", (byte)(_windowMode.IsFullscreen ? 1 : 0)) != 0)
                _windowMode.RequestToggle();
            if (ImGui.MenuItem(L.T("CanvasOnly"), "Alt+H") != 0)
                _canvasOnlyToggleRequested = true;
            if (SceneViewAvailable && ImGui.MenuItem(L.T("SceneView3D"), null, (byte)(_showSceneView ? 1 : 0)) != 0)
                SetSceneView(!_showSceneView);
            if (ImGui.MenuItem(L.T("AudioCalibration"), null, 0, (byte)(_videoExport?.Request is null ? 1 : 0)) != 0)
                OpenAudioCalibration();
            ImGui.Separator();
            ToggleMenu(L.T("Settings"), ref _showSettings);
            ToggleMenu(L.T("Decorations"), ref _showDecorations);
            ToggleMenu(L.T("Inspector"), ref _showInspector);
            ToggleMenu(L.T("Palette"), ref _showPalette);
            ToggleMenu(L.T("Timeline"), ref _showTimeline);
            ToggleMenu(L.T("FloorNumbers"), ref _showBrickNumbers, "Ctrl+F");
            _modUi?.DrawItems(MenuSlot.View);
            _modUi?.DrawWindowMenu();
            ImGui.Separator();
            if (ImGui.MenuItem(L.T("ResetLayout")) != 0)
            {
                _showSettings = _showDecorations = _showInspector = _showPalette = _showTimeline = _resetLayout = true;
                _showParticle = false;
            }
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu(L.T("Chart")) != 0)
        {
            if (ImGui.MenuItem(L.T("FindFloorCommand"), "Alt+;") != 0) OpenFindFloor();
            if (ImGui.MenuItem(L.T("GoToStart"), "Home") != 0) SelectFloor(0, true);
            if (ImGui.MenuItem(L.T("GoToEnd"), "End") != 0) SelectFloor(_document.LastFloor, true);
            ImGui.Separator();
            if (ImGui.MenuItem(L.T("ChartPreview"), null, (byte)(_playbackView == PlaybackView.Preview ? 1 : 0)) != 0)
                SwitchPlaybackView(PlaybackView.Preview);
            if (ImGui.MenuItem(L.T("PlayFromTile"), "Space / P") != 0) StartFromShortcut();
            if (ImGui.MenuItem(L.T("ConfiguredSpeed"), "Ctrl+Space") != 0) StartFromShortcut(PlaybackRate.Configured);
            if (ImGui.MenuItem(L.T("HalfSpeed"), "Ctrl+P") != 0) StartFromShortcut(PlaybackRate.Half);
            if (ImGui.MenuItem(L.T("Manual"), "Alt+A", (byte)(_manualPlay ? 1 : 0)) != 0) SetManualPlayMode(!_manualPlay);
            if (ImGui.MenuItem(L.T("NoFail"), "Alt+N", (byte)(_noFail ? 1 : 0)) != 0) SetNoFail(!_noFail);
            if (ImGui.MenuItem(L.T("FindCommentCommand"), "Alt+F") != 0) _openFindComment = true;
            if (ImGui.MenuItem(L.T("ReturnToEdit"), "Esc") != 0) ReturnToEdit();
            _modUi?.DrawItems(MenuSlot.Chart);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu(L.T("MenuRender")) != 0)
        {
            if (ImGui.MenuItem(L.T("VideoRenderTitle") + "…") != 0) _videoRenderWindow.Open();
            _modUi?.DrawItems(MenuSlot.Render);
            _modUi?.DrawExportItems();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu(L.T("MenuTools")) != 0)
        {
            if (ImGui.MenuItem(L.T("Decorations")) != 0) _showDecorations = _focusDecorations = true;
            if (ImGui.MenuItem(L.T("ParticleEditor"), null, 0, (byte)(IsSelectedParticle ? 1 : 0)) != 0) OpenParticle();
            _modUi?.DrawItems(MenuSlot.Tools);
            ImGui.EndMenu();
        }
        _modUi?.DrawMenuBar();
        if (ImGui.BeginMenu(L.T("MenuHelp")) != 0)
        {
            ToggleMenu(L.T("Shortcuts"), ref _showShortcuts, "Alt+Shift+H");
            _modUi?.DrawItems(MenuSlot.Help);
            ImGui.EndMenu();
        }
        DrawApplicationSettingsMenuItem();
        _menuBarFrameRate.Draw(ChartFileName, _applicationSettings.ShowFrameRate, UnsavedChartMarker);
        ImGui.EndMainMenuBar();
    }

    private static void ToggleMenu(string label, ref bool shown, string? shortcut = null)
    {
        if (ImGui.MenuItem(label, shortcut, (byte)(shown ? 1 : 0)) != 0)
            shown = !shown;
    }

    private static bool Pressed(ImGuiKey key) => ImGui.IsKeyPressed(key, 0) != 0;

    private void Undo() { _pendingTiles.Clear(); _document.Undo(); ClampSelection(); }
    private void Redo() { _pendingTiles.Clear(); _document.Redo(); ClampSelection(); }

    private void ClampSelection()
    {
        _floor = Math.Clamp(_floor, 0, _document.LastFloor);
        if (_rangeAnchor is int anchor)
        {
            anchor = Math.Clamp(anchor, 0, _document.LastFloor);
            _rangeAnchor = anchor == _floor ? null : anchor;
        }
        _selectedDecorations.RemoveWhere(item => item.Parent is null);
        if (_decoration?.Parent is null)
        {
            _decoration = _selectedDecorations.LastOrDefault();
            _inspectDecoration = _decoration is not null;
        }
        if (_particle?.Parent is null)
            _showParticle = false;
    }

    private void DeleteSelection(bool backwards = true)
    {
        _pendingTiles.Clear();
        if (_inspectDecoration && _decoration is not null)
            _document.RemoveDecorations(_selectedDecorations.Where(item => !DecorationLocked(item)));
        else if (_hasFloorSelection)
        {
            var (first, last) = FloorRange;
            if (first == last && !backwards)
                DeleteFloorRange(first + 1, first + 1, first);
            else
                DeleteFloorRange(first, last, backwards ? first - 1 : first);
        }
        ClampSelection();
    }

    private void SetDocument(EditorDocument document)
    {
        SwitchPlaybackView(PlaybackView.Edit);
        _playbackFrameAvailable = false;
        _document = document;
        _sceneFocus = _showSceneView;
        _sceneShapes.Clear();
        RememberOpenedChart();
        _decorationRowsKey = null;
        _decorationMarkersVersion = -1;
        _pendingCameraFocusVersion = -1;
        _decoration = _particle = null;
        _showParticle = _inspectDecoration = false;
        _selectedDecorations.Clear();
        _hideAllDecorations = _lockAllDecorations = false;
        _dragDecoration = null;
        _pendingTiles.Clear();
        _floorHitCycle.Reset();
        _savedEditCamera = null;
        _centerEditDecoration = null;
        SelectFloor(0, true);
    }

    private void Confirm(Action next)
    {
        if (_document.Modified)
        {
            _pending = next;
            _openConfirm = true;
        }
        else
            next();
    }

    private void RequestClose() => Confirm(() => { _allowClose = true; Close(); });

    private void Open()
    {
        try
        {
            string[]? paths = NativeFileDialog.NativeFileDialog.GetFileDialog()?.OpenFile(L.T("OpenLevel"),
                Path.GetDirectoryName(_document.Path) ?? "", new OpenFileFilter { Filter = LevelFilter });
            if (paths is { Length: > 0 })
                OpenLevelPath(paths[0]);
        }
        catch (Exception error) { ShowError(error); }
    }

    private bool Save(bool saveAs = false)
    {
        try
        {
            string? previousPath = _document.Path;
            string? file = previousPath;
            if (saveAs || file is null)
                file = NativeFileDialog.NativeFileDialog.GetFileDialog()?.SaveFile(L.T("SaveLevel"),
                    Path.GetDirectoryName(file) ?? "", Path.GetFileName(file) ?? "level.adofai",
                    new SaveFileFilter { Filter = new() { ["ADOFAI"] = "adofai" } });
            if (string.IsNullOrWhiteSpace(file))
                return false;
            if (!Path.HasExtension(file))
                file += ".adofai";
            _document.Save(file);
            _savedAt = Stopwatch.GetTimestamp();
            if (_document.Path != previousPath)
                RememberOpenedChart();
            return true;
        }
        catch (Exception error) { ShowError(error); return false; }
    }

    private void ShowError(Exception error) { _error = error.Message; _openError = true; }

    private void OpenFindFloor() { _findFloor = _floor; _openFindFloor = true; }

    private void DrawDialogs()
    {
        DrawArchivePicker();
        if (_openFindFloor)
        {
            ImGui.OpenPopup(L.T("FindFloor"));
            ImGui.SetNextWindowFocus();
            _openFindFloor = false;
        }
        if (ImGui.BeginPopupModal(L.T("FindFloor")) != 0)
        {
            int floor = _findFloor;
            bool submitted = ImGui.InputInt(L.T("FloorNumber"), ref floor, 1, 10,
                (int)ImGuiInputTextFlags.ImGuiInputTextFlags_EnterReturnsTrue) != 0;
            _findFloor = floor;
            if (ImGui.Button(L.T("Locate"), default) != 0 || submitted)
            {
                SelectFloor(_findFloor, true);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button(L.T("Cancel"), default) != 0 || Pressed(ImGuiKey.ImGuiKey_Escape))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        if (_openConfirm)
        {
            ImGui.OpenPopup(L.T("Unsaved"));
            _openConfirm = false;
        }
        if (ImGui.BeginPopupModal(L.T("Unsaved")) != 0)
        {
            ImGui.TextUnformatted(L.T("SavePrompt"));
            if (ImGui.Button(L.T("Save"), default) != 0 && Save()) FinishConfirmation(true);
            ImGui.SameLine();
            if (ImGui.Button(L.T("Discard"), default) != 0) FinishConfirmation(true);
            ImGui.SameLine();
            if (ImGui.Button(L.T("Cancel"), default) != 0) FinishConfirmation(false);
            ImGui.EndPopup();
        }
        if (_openError)
        {
            ImGui.OpenPopup(L.T("Error"));
            _openError = false;
        }
        if (ImGui.BeginPopupModal(L.T("Error")) != 0)
        {
            ImGui.TextUnformatted(_error);
            if (ImGui.Button(L.T("Ok"), default) != 0)
            {
                _error = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    private void FinishConfirmation(bool proceed)
    {
        Action? action = _pending;
        _pending = null;
        ImGui.CloseCurrentPopup();
        if (proceed)
            action?.Invoke();
    }

    private string ChartFileName => Path.GetFileName(_document.Path) ?? L.T("Untitled");
    private string UnsavedChartMarker => _document.Modified ? " *" : "";

    private void UpdateTitle()
    {
        string title = $"SharpFAI Editor — {ChartFileName}{UnsavedChartMarker}";
        if (Title != title)
            Title = title;
    }

    private void FeedMouse()
    {
        ReleaseWebInput();
        ImGuiIO* io = ImGui.GetIO();
        ImGuiIO.AddMousePosEvent(io, MouseState.X, MouseState.Y);
        ImGuiIO.AddMouseButtonEvent(io, 0, (byte)(MouseState.IsButtonDown(MouseButton.Left) ? 1 : 0));
        ImGuiIO.AddMouseButtonEvent(io, 1, (byte)(MouseState.IsButtonDown(MouseButton.Right) ? 1 : 0));
        ImGuiIO.AddMouseButtonEvent(io, 2, (byte)(MouseState.IsButtonDown(MouseButton.Middle) ? 1 : 0));
    }

    private void FeedKey(KeyboardKeyEventArgs e, bool down)
    {
        if (down && !e.IsRepeat && e.Key == Keys.Space)
            _spacePressModifiers = e.Modifiers;
        ImGuiIO* io = ImGui.GetIO();
        ImGuiIO.AddKeyEvent(io, ImGuiKey.ImGuiMod_Ctrl, (byte)(e.Control ? 1 : 0));
        ImGuiIO.AddKeyEvent(io, ImGuiKey.ImGuiMod_Shift, (byte)(e.Shift ? 1 : 0));
        ImGuiIO.AddKeyEvent(io, ImGuiKey.ImGuiMod_Alt, (byte)(e.Alt ? 1 : 0));
        ImGuiIO.AddKeyEvent(io, ImGuiKey.ImGuiMod_Super, (byte)(e.Modifiers.HasFlag(KeyModifiers.Super) ? 1 : 0));
        ImGuiKey key = e.Key switch
        {
            >= Keys.A and <= Keys.Z => (ImGuiKey)((int)ImGuiKey.ImGuiKey_A + (e.Key - Keys.A)),
            >= Keys.D0 and <= Keys.D9 => (ImGuiKey)((int)ImGuiKey.ImGuiKey_0 + (e.Key - Keys.D0)),
            Keys.Left => ImGuiKey.ImGuiKey_LeftArrow, Keys.Right => ImGuiKey.ImGuiKey_RightArrow,
            Keys.Up => ImGuiKey.ImGuiKey_UpArrow, Keys.Down => ImGuiKey.ImGuiKey_DownArrow,
            Keys.LeftBracket => ImGuiKey.ImGuiKey_LeftBracket, Keys.RightBracket => ImGuiKey.ImGuiKey_RightBracket,
            Keys.Semicolon => ImGuiKey.ImGuiKey_Semicolon,
            Keys.Minus => ImGuiKey.ImGuiKey_Minus, Keys.Equal => ImGuiKey.ImGuiKey_Equal,
            Keys.Comma => ImGuiKey.ImGuiKey_Comma, Keys.Period => ImGuiKey.ImGuiKey_Period,
            Keys.Slash => ImGuiKey.ImGuiKey_Slash, Keys.GraveAccent => ImGuiKey.ImGuiKey_GraveAccent,
            Keys.KeyPadSubtract => ImGuiKey.ImGuiKey_KeypadSubtract, Keys.KeyPadAdd => ImGuiKey.ImGuiKey_KeypadAdd,
            Keys.KeyPadEnter => ImGuiKey.ImGuiKey_KeypadEnter,
            Keys.F5 => ImGuiKey.ImGuiKey_F5, Keys.F7 => ImGuiKey.ImGuiKey_F7,
            Keys.Home => ImGuiKey.ImGuiKey_Home, Keys.End => ImGuiKey.ImGuiKey_End,
            Keys.Delete => ImGuiKey.ImGuiKey_Delete, Keys.Backspace => ImGuiKey.ImGuiKey_Backspace,
            Keys.Tab => ImGuiKey.ImGuiKey_Tab, Keys.Enter => ImGuiKey.ImGuiKey_Enter,
            Keys.Escape => ImGuiKey.ImGuiKey_Escape, Keys.Space => ImGuiKey.ImGuiKey_Space,
            Keys.LeftControl => ImGuiKey.ImGuiKey_LeftCtrl, Keys.RightControl => ImGuiKey.ImGuiKey_RightCtrl,
            Keys.LeftShift => ImGuiKey.ImGuiKey_LeftShift, Keys.RightShift => ImGuiKey.ImGuiKey_RightShift,
            Keys.LeftAlt => ImGuiKey.ImGuiKey_LeftAlt, Keys.RightAlt => ImGuiKey.ImGuiKey_RightAlt,
            _ => ImGuiKey.ImGuiKey_None
        };
        if (key != ImGuiKey.ImGuiKey_None)
            ImGuiIO.AddKeyEvent(io, key, (byte)(down ? 1 : 0));
    }

}

using ImGui4NET;
using ImGui4NET.Backend.OpenGL3;
using NativeFileDialog;
using Newtonsoft.Json.Linq;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SharpFAI.Editor.Widgets;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow : GameWindow
{
    private static string SettingsWindow => L.T("Settings") + "###settings";
    private static string DecorationsWindow => L.T("Decorations") + "###decorations";
    private static string CanvasWindow => L.T("Chart") + "###canvas";
    private static string InspectorWindow => L.T("Inspector") + "###inspector";
    private static string PaletteWindow => L.T("Palette") + "###palette";
    private static string ParticleWindow => L.T("ParticleEditor") + "###particle";

    private readonly EditorCatalog _catalog;
    private readonly bool _smokeTest;
    private EditorDocument _document;
    private ImGuiContext* _context;
    private bool _backendInitialized;
    private bool _resetLayout = true;
    private string? _pendingLanguage;
    private bool _showSettings = true;
    private bool _showDecorations = true;
    private bool _showInspector = true;
    private bool _showPalette = true;
    private bool _showShortcuts;
    private bool _showBrickNumbers = true;
    private bool _openFindFloor;
    private int _findFloor;
    private bool _allowClose;
    private bool _closeRequested;
    private bool _openConfirm;
    private Action? _pending;
    private string? _error;
    private bool _openError;
    private int _frames;
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

    public EditorWindow(NativeWindowSettings settings, EditorCatalog catalog, EditorDocument document, bool smokeTest,
        bool effectBenchmark = false)
        : base(GameWindowSettings.Default, settings)
    {
        _catalog = catalog;
        _document = document;
        _smokeTest = smokeTest;
        _effectBenchmark = effectBenchmark;
    }

    protected override void OnLoad()
    {
        base.OnLoad();
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
        LoadFont(io);
        InitializePlayback();
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
        KeyDown += e => { ReleaseWebInput(); FeedKey(e, true); FeedPlaybackKey(e, true); };
        KeyUp += e => { ReleaseWebInput(); FeedKey(e, false); FeedPlaybackKey(e, false); };
        UpdateTitle();

        if (_smokeTest)
        {
            VerifyClipboard();
            _showParticle = true;
            _particlePlaying = true;
            _particle = _catalog.Create("AddParticle", 0);
            _document.Add(_particle, true);
            string directory = Path.GetDirectoryName(_document.PlaybackPath)!;
            string testImage = Path.GetRelativePath(directory,
                Path.Combine(AppContext.BaseDirectory, "Resources", "Floor", "Tile.png"));
            if (!EditorDocument.IsLocalAsset(testImage))
                testImage = Directory.EnumerateFiles(directory, "*.png").Select(Path.GetFileName).FirstOrDefault() ?? "";
            if (testImage.Length != 0)
            {
                _smokeDecorationIndex = _document.Decorations.Count;
                _document.Add(new JObject
                {
                    ["floor"] = 0, ["eventType"] = "AddDecoration", ["tag"] = "__sharpfai_sprite_smoke",
                    ["decorationImage"] = testImage, ["relativeTo"] = "Camera", ["position"] = new JArray(0, 0)
                }, true);
            }
            JObject first = _catalog.Create("SetFilter", 0);
            JObject second = _catalog.Create("SetFilter", 0);
            _document.Add(first, false);
            _document.Add(second, false);
            _eventType = "SetFilter";
            _eventPage = _document.Events(0).Count(value => EditorDocument.Type(value) == _eventType) - 1;
            _focusDecorations = true;
        }
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        if (ClientSize.X <= 0 || ClientSize.Y <= 0)
            return;
        ImGuiIO* io = ImGui.GetIO();
        io->DisplaySize = WidgetDraw.V2(ClientSize.X, ClientSize.Y);
        io->DisplayFramebufferScale = WidgetDraw.V2(FramebufferSize.X / (float)ClientSize.X, FramebufferSize.Y / (float)ClientSize.Y);
        io->DeltaTime = Math.Clamp((float)args.Time, 0.0001f, 0.1f);
        ImGui_ImplOpenGL3.NewFrame();
        if (WebMirror is not null) FeedWebInput(io);
        if (_smokeTest)
        {
            FeedNumericSmokeInput(io);
            FeedPlaybackSmokeInput(io);
        }
        ImGui.NewFrame();
        if (_pendingLanguage is { } language)
        {
            L.Language = System.Globalization.CultureInfo.GetCultureInfo(language);
            _pendingLanguage = null;
        }

        if (_closeRequested)
        {
            _closeRequested = false;
            RequestClose();
        }
        DrawMenu();
        DrawDockspace();
        HandleShortcuts();
        _numericDrawn = false;
        UpdatePlayback();
        DrawSettings();
        DrawDecorations();
        DrawCanvas();
        _speedWheels.Clear();
        // Starting here lets the canvas apply a wheel step from this frame first.
        if (_queuedRateShortcut is { } rate)
        {
            _queuedRateShortcut = null;
            StartFromShortcut(rate);
        }
        DrawInspector();
        DrawPalette();
        DrawParticleEditor();
        DrawShortcuts();
        DrawFindComment();
        DrawDialogs();
        if (_smokeTest) DrawNumericSmoke();
        if (!_numericDrawn)
            FinishNumericEdit();
        UpdateTitle();

        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
        GL.ClearColor(0.059f, 0.065f, 0.083f, 1);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        ImGui.Render();
        ImGui_ImplOpenGL3.RenderDrawData(ImGui.GetDrawData());
        WebMirror?.CaptureFrame(FramebufferSize.X, FramebufferSize.Y);
        SwapBuffers();

        if (_effectBenchmark)
        {
            VerifyEffectBenchmark();
            return;
        }
        if (!_smokeTest)
            return;
        if (++_frames == 5)
        {
            VerifyLayout();
            int count = _document.Events(0).Count(item => EditorDocument.Type(item) == "SetFilter");
            ChangeEventPage(-1, true);
            if (_eventPage != 0)
                throw new InvalidOperationException("Could not go to the first event.");
            ChangeEventPage(1, false);
            if (_eventPage != 1)
                throw new InvalidOperationException("Event paging failed.");
            ChangeEventPage(1, true);
            if (_eventPage != count - 1)
                throw new InvalidOperationException("Could not go to the last event.");
            Console.WriteLine("SharpFAI Editor rendered 5 frames and verified docking and event paging.");
            StartPlaybackSmokeTest();
        }
        if (_frames > NumericSmokeLastFrame)
            VerifyPlaybackSmokeTest();
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

    private static void VerifyLayout()
    {
        ImGuiWindow* left = ImGui.FindWindowByName(SettingsWindow);
        ImGuiWindow* manager = ImGui.FindWindowByName(DecorationsWindow);
        ImGuiWindow* center = ImGui.FindWindowByName(CanvasWindow);
        ImGuiWindow* right = ImGui.FindWindowByName(InspectorWindow);
        ImGuiWindow* bottom = ImGui.FindWindowByName(PaletteWindow);
        if (left == null || manager == null || center == null || right == null || bottom == null ||
            center->DockId == 0 || left->DockId != manager->DockId ||
            left->Pos.x >= center->Pos.x || right->Pos.x <= center->Pos.x || bottom->Pos.y <= center->Pos.y)
            throw new InvalidOperationException("Invalid default dock layout.");
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
            ResetCanvasGesture();
            _dragDecoration = null;
            ReleasePlaybackInput();
        }
    }

    protected override void OnUnload()
    {
        DisposePlayback();
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
        uint footer = ImGui.DockBuilderSplitNode(canvas, ImGuiDir.ImGuiDir_Down, 0.16f, null, &canvas);
        uint left = ImGui.DockBuilderSplitNode(canvas, ImGuiDir.ImGuiDir_Left, 0.22f, null, &canvas);
        uint right = ImGui.DockBuilderSplitNode(canvas, ImGuiDir.ImGuiDir_Right, 0.30f, null, &canvas);
        ImGui.DockBuilderDockWindow(CanvasWindow, canvas);
        ImGui.DockBuilderDockWindow(SettingsWindow, left);
        ImGui.DockBuilderDockWindow(DecorationsWindow, left);
        ImGui.DockBuilderDockWindow(InspectorWindow, right);
        ImGui.DockBuilderDockWindow(PaletteWindow, footer);
        ImGui.DockBuilderFinish(dock);
    }

    private void DrawMenu()
    {
        if (ImGui.BeginMainMenuBar() == 0)
            return;
        if (ImGui.BeginMenu(L.T("MenuFile")) != 0)
        {
            if (ImGui.MenuItem(L.T("New"), "Ctrl+N") != 0) Confirm(() => SetDocument(new()));
            if (ImGui.MenuItem(L.T("Open"), "Ctrl+O") != 0) Confirm(Open);
            if (ImGui.MenuItem(L.T("Save"), "Ctrl+S") != 0) Save();
            if (ImGui.MenuItem(L.T("SaveAs"), "Ctrl+Shift+S") != 0) Save(true);
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
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu(L.T("MenuView")) != 0)
        {
            ToggleMenu(L.T("Settings"), ref _showSettings);
            ToggleMenu(L.T("Decorations"), ref _showDecorations);
            ToggleMenu(L.T("Inspector"), ref _showInspector);
            ToggleMenu(L.T("Palette"), ref _showPalette);
            ToggleMenu(L.T("FloorNumbers"), ref _showBrickNumbers, "Ctrl+F");
            ImGui.Separator();
            if (ImGui.MenuItem(L.T("ResetLayout")) != 0)
            {
                _showSettings = _showDecorations = _showInspector = _showPalette = _resetLayout = true;
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
            if (ImGui.MenuItem(L.T("ManualFromTile")) != 0) SwitchPlaybackView(PlaybackView.Manual);
            if (ImGui.MenuItem(L.T("Autoplay"), "Alt+A", (byte)(_shortcutAuto ? 1 : 0)) != 0) ToggleShortcutAutoplay();
            if (ImGui.MenuItem(L.T("NoFail"), "Alt+N", (byte)(_shortcutNoFail ? 1 : 0)) != 0) ToggleShortcutNoFail();
            if (ImGui.MenuItem(L.T("FindCommentCommand"), "Alt+F") != 0) _openFindComment = true;
            if (ImGui.MenuItem(L.T("ReturnToEdit"), "Esc") != 0) SwitchPlaybackView(PlaybackView.Edit);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu(L.T("MenuTools")) != 0)
        {
            if (ImGui.MenuItem(L.T("Decorations")) != 0) _showDecorations = _focusDecorations = true;
            if (ImGui.MenuItem(L.T("ParticleEditor"), null, 0, (byte)(IsSelectedParticle ? 1 : 0)) != 0) OpenParticle();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu(L.T("MenuHelp")) != 0)
        {
            ToggleMenu(L.T("Shortcuts"), ref _showShortcuts);
            if (ImGui.BeginMenu(L.T("Language")) != 0)
            {
                if (ImGui.MenuItem("English", null, (byte)(L.IsChinese ? 0 : 1)) != 0) _pendingLanguage = "en";
                if (ImGui.MenuItem(L.T("Chinese"), null, (byte)(L.IsChinese ? 1 : 0)) != 0) _pendingLanguage = "zh-Hans";
                ImGui.EndMenu();
            }
            ImGui.EndMenu();
        }
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
        _decoration = _particle = null;
        _showParticle = _inspectDecoration = false;
        _selectedDecorations.Clear();
        _hideAllDecorations = _lockAllDecorations = false;
        _dragDecoration = null;
        _pendingTiles.Clear();
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
            string? file = _document.Path;
            if (saveAs || file is null)
                file = NativeFileDialog.NativeFileDialog.GetFileDialog()?.SaveFile(L.T("SaveLevel"),
                    Path.GetDirectoryName(file) ?? "", Path.GetFileName(file) ?? "level.adofai",
                    new SaveFileFilter { Filter = new() { ["ADOFAI"] = "adofai" } });
            if (string.IsNullOrWhiteSpace(file))
                return false;
            if (!Path.HasExtension(file))
                file += ".adofai";
            _document.Save(file);
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

    private void UpdateTitle()
    {
        string name = Path.GetFileName(_document.Path) ?? L.T("Untitled");
        string title = $"SharpFAI Editor — {name}{(_document.Modified ? " *" : "")}";
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

    private static void LoadFont(ImGuiIO* io)
    {
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (string path in new[] { Path.Combine(fonts, "msyh.ttc"), Path.Combine(fonts, "simsun.ttc"),
                     "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc", "/System/Library/Fonts/PingFang.ttc" })
        {
            if (!File.Exists(path))
                continue;
            ImFont* font = ImFontAtlas.AddFontFromFileTTF(io->Fonts, path, 17, null,
                ImFontAtlas.GetGlyphRangesChineseSimplifiedCommon(io->Fonts));
            if (font != null)
            {
                io->FontDefault = font;
                return;
            }
        }
        io->FontDefault = ImFontAtlas.AddFontDefault(io->Fonts);
        Console.Error.WriteLine(L.T("NoChineseFont"));
    }
}

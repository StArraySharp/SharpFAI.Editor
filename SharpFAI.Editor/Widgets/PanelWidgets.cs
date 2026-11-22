using ImGui4NET;

namespace SharpFAI.Editor.Widgets;

/// <summary>
/// Searchable list selector with multi-select and keyboard navigation.
/// </summary>
public static unsafe class FilterableList
{
    /// <summary>
    /// Draw a filterable list.
    /// </summary>
    /// <param name="id">Unique identifier.</param>
    /// <param name="items">All items.</param>
    /// <param name="selected">Selected index, or -1 for none.</param>
    /// <param name="height">List height, 0 for the default.</param>
    /// <param name="searchHint">Search box hint.</param>
    /// <returns>True if the selection changed this frame.</returns>
    public static bool Draw(string id, string[] items, ref int selected, float height = 0f, string searchHint = "搜索...")
    {
        if (height <= 0f)
        {
            height = ImGui.GetFrameHeight() * 8f;
        }

        string filterKey = id + "##filter";
        FilterStates.TryGetValue(filterKey, out string? filter);
        filter ??= string.Empty;

        bool changed = false;

        ImGui.PushID(id);

        // Search box with a clear button
        float clearWidth = ImGui.CalcTextSize("×", null, 0, -1f).x + 12f;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().x - clearWidth - 4f);
        filter = InputTextString(filterKey, filter);
        ImGui.SameLine();
        if (ImGui.Button("×", WidgetDraw.V2(clearWidth, ImGui.GetFrameHeight())) != 0)
        {
            filter = string.Empty;
        }

        if (ImGui.IsItemHovered() != 0)
        {
            WidgetDraw.ItemTooltip("清空筛选");
        }

        FilterStates[filterKey] = filter;

        ImVec2 size = WidgetDraw.V2(ImGui.GetContentRegionAvail().x, height);
        if (ImGui.BeginChild("##list", in size, (int)ImGuiChildFlags.ImGuiChildFlags_Borders) != 0)
        {
            int visibleIndex = 0;
            bool hasMatch = false;

            for (int i = 0; i < items.Length; i++)
            {
                if (filter.Length > 0 && !Contains(items[i], filter))
                {
                    continue;
                }

                hasMatch = true;
                ImGui.PushID(i);

                bool isSelected = i == selected;
                if (ImGui.Selectable(items[i], (byte)(isSelected ? 1 : 0), 0, new ImVec2()) != 0)
                {
                    selected = isSelected ? -1 : i;
                    changed = true;
                }

                // Arrow-key navigation
                if (ImGui.IsItemFocused() != 0)
                {
                    if (ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_DownArrow) != 0)
                    {
                        selected = FindNext(items, filter, i, 1);
                        changed = true;
                    }
                    else if (ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_UpArrow) != 0)
                    {
                        selected = FindNext(items, filter, i, -1);
                        changed = true;
                    }
                }

                ImGui.PopID();
                visibleIndex++;
            }

            if (!hasMatch)
            {
                WidgetDraw.TextDisabled("没有匹配项");
            }
        }

        ImGui.EndChild();

        ImGui.PopID();
        return changed;
    }

    private static readonly Dictionary<string, string> FilterStates = [];
    private static readonly Dictionary<string, byte[]> Buffers = [];

    private static int FindNext(string[] items, string filter, int current, int direction)
    {
        int index = current;
        for (int step = 0; step < items.Length; step++)
        {
            index += direction;
            if (index < 0 || index >= items.Length)
            {
                return current;
            }

            if (filter.Length == 0 || Contains(items[index], filter))
            {
                return index;
            }
        }

        return current;
    }

    private static bool Contains(string value, string filter)
        => value.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Drive InputText with a managed string.</summary>
    private static string InputTextString(string id, string current)
    {
        if (!Buffers.TryGetValue(id, out byte[]? buffer))
        {
            buffer = new byte[512];
            Buffers[id] = buffer;
        }

        byte[] encoded = System.Text.Encoding.UTF8.GetBytes(current);
        if (encoded.Length + 1 > buffer.Length)
        {
            buffer = new byte[encoded.Length + 64];
            Buffers[id] = buffer;
        }

        Array.Clear(buffer);
        Array.Copy(encoded, buffer, encoded.Length);

        fixed (byte* pointer = buffer)
        {
            if (ImGui.InputText("##search", (sbyte*)pointer, (nuint)buffer.Length) != 0)
            {
                return System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)pointer) ?? string.Empty;
            }
        }

        return current;
    }
}

/// <summary>
/// Tree view with collapsing nodes, indentation and selection.
/// </summary>
public static unsafe class TreeView
{
    /// <summary>A tree node.</summary>
    public sealed class Node
    {
        public Node(string label, params Node[] children)
        {
            Label = label;
            Children = children;
        }

        public string Label { get; set; }

        public Node[] Children { get; set; }

        public object? UserData { get; set; }

        public bool Expanded { get; set; } = true;
    }

    /// <summary>
    /// Draw a whole tree. Returns true when the selection changed.
    /// </summary>
    /// <param name="id">Unique identifier.</param>
    /// <param name="nodes">Root nodes.</param>
    /// <param name="selected">Currently selected node.</param>
    public static bool Draw(string id, IReadOnlyList<Node> nodes, ref Node? selected)
    {
        bool changed = false;
        ImGui.PushID(id);
        DrawNodes(nodes, ref selected, ref changed);
        ImGui.PopID();
        return changed;
    }

    private static void DrawNodes(IReadOnlyList<Node> nodes, ref Node? selected, ref bool changed)
    {
        foreach (Node node in nodes)
        {
            ImGui.PushID(node.Label);

            int flags = (int)ImGuiTreeNodeFlags.ImGuiTreeNodeFlags_OpenOnArrow
                | (int)ImGuiTreeNodeFlags.ImGuiTreeNodeFlags_SpanAvailWidth;
            if (node == selected)
            {
                flags |= (int)ImGuiTreeNodeFlags.ImGuiTreeNodeFlags_Selected;
            }

            if (node.Children.Length == 0)
            {
                flags |= (int)ImGuiTreeNodeFlags.ImGuiTreeNodeFlags_Leaf
                       | (int)ImGuiTreeNodeFlags.ImGuiTreeNodeFlags_NoTreePushOnOpen;
            }

            ImGui.SetNextItemOpen((byte)(node.Expanded ? 1 : 0));
            bool isOpen = ImGui.TreeNodeEx(node.Label, flags) != 0;
            node.Expanded = isOpen;

            if (ImGui.IsItemClicked() != 0)
            {
                selected = node;
                changed = true;
            }

            if (node.Children.Length > 0 && isOpen)
            {
                DrawNodes(node.Children, ref selected, ref changed);
                ImGui.TreePop();
            }

            ImGui.PopID();
        }
    }
}

/// <summary>
/// Collapsible section panel with a header bar.
/// </summary>
public static unsafe class CollapsibleSection
{
    /// <summary>
    /// Begin a collapsible section. Returns true when the content should be drawn.
    /// </summary>
    /// <param name="id">Unique identifier.</param>
    /// <param name="title">Section title.</param>
    /// <param name="open">Whether the section is expanded.</param>
    /// <param name="icon">Optional icon glyph.</param>
    public static bool Begin(string id, string title, ref bool open, string? icon = null)
    {
        ImDrawList* draw = ImGui.GetWindowDrawList();
        float height = ImGui.GetFrameHeight() + 4f;
        ImVec2 avail = ImGui.GetContentRegionAvail();
        ImVec2 size = WidgetDraw.V2(avail.x, height);
        ImVec2 origin = ImGui.GetCursorScreenPos();

        ImGui.PushID(id);
        bool clicked = ImGui.InvisibleButton("##header", in size) != 0;
        bool hovered = ImGui.IsItemHovered() != 0;
        ImGui.PopID();

        if (clicked)
        {
            open = !open;
        }

        ImVec2 max = WidgetDraw.V2(origin.x + size.x, origin.y + size.y);
        uint background = hovered
            ? WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_HeaderHovered)
            : WidgetDraw.WithAlpha(WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Header), 0.6f);

        WidgetDraw.Panel(draw, in origin, in max, background, 4f);

        // Disclosure arrow
        float cx = origin.x + 12f;
        float cy = origin.y + height * 0.5f;
        uint arrowColor = WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text, 0.8f);

        if (open)
        {
            ImDrawList.AddTriangleFilled(draw,
                WidgetDraw.V2(cx - 4f, cy - 2f), WidgetDraw.V2(cx + 4f, cy - 2f), WidgetDraw.V2(cx, cy + 3f), arrowColor);
        }
        else
        {
            ImDrawList.AddTriangleFilled(draw,
                WidgetDraw.V2(cx - 2f, cy - 4f), WidgetDraw.V2(cx + 3f, cy), WidgetDraw.V2(cx - 2f, cy + 4f), arrowColor);
        }

        float textX = origin.x + 24f;
        if (!string.IsNullOrEmpty(icon))
        {
            ImDrawList.AddText(draw, WidgetDraw.V2(textX, cy - ImGui.GetTextLineHeight() * 0.5f), arrowColor, icon);
            textX += ImGui.CalcTextSize(icon, null, 0, -1f).x + 6f;
        }

        ImDrawList.AddText(draw, WidgetDraw.V2(textX, cy - ImGui.GetTextLineHeight() * 0.5f),
            WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text), title);

        if (open)
        {
            ImGui.Spacing();
        }

        return open;
    }

    /// <summary>Finish a section and draw a divider.</summary>
    public static void End()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }
}

/// <summary>
/// Hover tooltip card for rich details.
/// </summary>
public static unsafe class TooltipCard
{
    /// <summary>
    /// Show a card while the last item is hovered.
    /// </summary>
    /// <param name="title">Card title.</param>
    /// <param name="lines">Body lines as key-value pairs.</param>
    /// <param name="accent">Accent color in 0~1.</param>
    public static void Show(string title, IReadOnlyList<(string Key, string Value)> lines, OpenTK.Mathematics.Vector4 accent)
    {
        if (ImGui.IsItemHovered((int)ImGuiHoveredFlags.ImGuiHoveredFlags_DelayNormal) == 0)
        {
            return;
        }

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 24f);

        ImGui.TextUnformatted(title);
        ImGui.Separator();

        foreach ((string key, string value) in lines)
        {
            WidgetDraw.TextDisabled(key);
            ImGui.SameLine(ImGui.GetFontSize() * 8f);
            ImGui.TextUnformatted(value);
        }

        ImDrawList* draw = ImGui.GetWindowDrawList();
        ImVec2 min = ImGui.GetWindowPos();
        ImVec2 size = ImGui.GetWindowSize();
        ImVec2 max = WidgetDraw.V2(min.x + size.x, min.y + 2f);
        ImDrawList.AddRectFilled(draw, in min, in max, WidgetDraw.U32(accent), 2f);

        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}

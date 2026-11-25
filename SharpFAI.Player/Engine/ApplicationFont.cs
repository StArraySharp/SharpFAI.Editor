using ImGui4NET;
using SixLabors.Fonts;

namespace SharpFAI.Player.Engine;

internal static class ApplicationFont
{
    internal const string FamilyName = "Noto Sans CJK Regular";
    private const string ResourceName = "SharpFAI.Player.NotoSansCJK";
    private static readonly ushort[] FullWidthRanges = PinnedRanges(
    [
        0x2e80, 0x2fdf, 0x3000, 0x3029, 0x3030, 0x303f, 0x3041, 0x3096, 0x30a0, 0x30ff,
        0x31f0, 0x31ff, 0x3400, 0x4dbf, 0x4e00, 0x9fff, 0xac00, 0xd7a3, 0xf900, 0xfaff,
        0xff01, 0xff60, 0xffe0, 0xffe6, 0,
    ]);
    private static readonly Lazy<byte[]> InterfaceFontData = new(() =>
    {
        using Stream stream = Open();
        byte[] data = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length), pinned: true);
        stream.ReadExactly(data);
        return data;
    });
    private static readonly Lazy<FontFamily> BundledFamily = new(() =>
    {
        using Stream stream = Open();
        return new FontCollection().Add(stream);
    });

    internal static FontFamily Family => BundledFamily.Value;

    internal static unsafe void LoadInterfaceFont(ImGuiIO* io, float size)
    {
        byte[] data = InterfaceFontData.Value;
        fixed (byte* bytes = data)
        fixed (ushort* ranges = FullWidthRanges)
        {
            ImFontConfig normal;
            ImFontConfig.ImFontConfig_Ctor(&normal);
            normal.FontDataOwnedByAtlas = 0;
            normal.GlyphExcludeRanges = ranges;
            io->FontDefault = ImFontAtlas.AddFontFromMemoryTTF(io->Fonts, bytes, data.Length, size, &normal, null);

            ImFontConfig wide;
            ImFontConfig.ImFontConfig_Ctor(&wide);
            wide.FontDataOwnedByAtlas = 0;
            wide.MergeMode = 1;
            wide.GlyphMinAdvanceX = size;
            wide.GlyphMaxAdvanceX = size;
            ImFontAtlas.AddFontFromMemoryTTF(io->Fonts, bytes, data.Length, size, &wide, ranges);
        }
    }

    private static ushort[] PinnedRanges(ReadOnlySpan<ushort> ranges)
    {
        // Dear ImGui reads these ranges again when baking additional glyphs.
        ushort[] storage = GC.AllocateUninitializedArray<ushort>(ranges.Length, pinned: true);
        ranges.CopyTo(storage);
        return storage;
    }

    private static Stream Open() =>
        typeof(ApplicationFont).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException("The bundled interface font is missing.");
}

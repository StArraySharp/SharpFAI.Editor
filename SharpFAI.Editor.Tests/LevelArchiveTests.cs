using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using NUnit.Framework;

namespace SharpFAI.Editor.Tests;

public sealed class LevelArchiveTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp() => _directory = Directory.CreateDirectory(Path.Combine(
        TestContext.CurrentContext.TestDirectory, $"zip-import-test-{Guid.NewGuid():N}")).FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public void ExtractsNestedLevelsAndAssetsAndSortsChoicesWithoutBackups()
    {
        string source = CreateZip(("z/other.adofai", "other"), ("a/deep/main.ADOFAI", "main"),
            ("a/deep/music/song.ogg", "audio"), ("a/deep/BackUP.AdOfAi", "backup"), ("empty/", ""));
        byte[] original = File.ReadAllBytes(source);
        string existing = Path.Combine(_directory, "a", "deep", "main.ADOFAI");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "existing");

        LevelArchive first = LevelArchive.Extract(source);
        LevelArchive second = LevelArchive.Extract(source);

        Assert.Multiple(() =>
        {
            Assert.That(LevelArchive.IsZip(source), Is.True);
            Assert.That(first.Levels, Is.EqualTo(new[] { Path.Combine("a", "deep", "main.ADOFAI"), Path.Combine("z", "other.adofai") }));
            Assert.That(File.ReadAllText(Path.Combine(first.DirectoryPath, "a", "deep", "music", "song.ogg")), Is.EqualTo("audio"));
            Assert.That(File.ReadAllText(Path.Combine(first.DirectoryPath, "a", "deep", "BackUP.AdOfAi")), Is.EqualTo("backup"));
            Assert.That(Directory.Exists(Path.Combine(first.DirectoryPath, "empty")), Is.True);
            Assert.That(first.DirectoryPath, Is.Not.EqualTo(second.DirectoryPath));
            Assert.That(Path.GetDirectoryName(first.DirectoryPath), Is.EqualTo(_directory));
            Assert.That(File.ReadAllBytes(source), Is.EqualTo(original));
            Assert.That(File.ReadAllText(existing), Is.EqualTo("existing"));
        });
    }

    [Test]
    public void FindsOneNestedLevelAndLoadsItThroughEditorDocument()
    {
        const string chart = """{"settings":{"version":13,"songFilename":"assets/song.ogg"},"angleData":[0,90],"actions":[],"decorations":[]}""";
        string source = CreateZip(("package\\nested\\level.adofai", chart), ("package/nested/assets/song.ogg", "audio"));
        LevelArchive result = LevelArchive.Extract(source);
        string path = Path.Combine(result.DirectoryPath, result.Levels.Single());
        var document = new EditorDocument(path);
        Assert.That(document.LastFloor, Is.EqualTo(2));
        Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(document.Path)!, "assets", "song.ogg")), Is.True);
    }

    [Test]
    public void UsesVerifiedUnicodePathForAnUnmarkedKoreanFilename()
    {
        Encoding korean = CodePagesEncodingProvider.Instance.GetEncoding(949)!;
        string source = CreateZip(korean, ("자료/에셋.png", "image"), ("level.adofai", "chart"));
        string corrected = "谱面/日本語/에셋.png";
        AddUnicodePath(source, corrected);

        LevelArchive result = LevelArchive.Extract(source);

        Assert.That(result.Levels, Is.EqualTo(new[] { "level.adofai" }));
        Assert.That(File.ReadAllText(Path.Combine(result.DirectoryPath, "谱面", "日本語", "에셋.png")), Is.EqualTo("image"));
    }

    [Test]
    public void UsesUnicodeDirectoryNamesInZip64Archives()
    {
        string source = CreateZip(("level.adofai", "chart"));
        AddUnicodePath(source, "日本語.adofai");
        byte[] data = File.ReadAllBytes(source);
        int end = data.Length - 22;
        uint directorySize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(end + 12));
        uint directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(end + 16));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(end + 16), uint.MaxValue);
        using (var writer = new BinaryWriter(File.Create(source)))
        {
            writer.Write(data.AsSpan(0, end));
            writer.Write(0x06064b50u);
            writer.Write(44UL);
            writer.Write((ushort)45);
            writer.Write((ushort)45);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(1UL);
            writer.Write(1UL);
            writer.Write((ulong)directorySize);
            writer.Write((ulong)directoryOffset);
            writer.Write(0x07064b50u);
            writer.Write(0u);
            writer.Write((ulong)end);
            writer.Write(1u);
            writer.Write(data.AsSpan(end));
        }

        Assert.That(LevelArchive.Extract(source).Levels, Is.EqualTo(new[] { "日本語.adofai" }));
    }

    [Test]
    public void UsesVerifiedNamesToInferTheEncodingForEntriesWithMissingMetadata()
    {
        Encoding korean = CodePagesEncodingProvider.Instance.GetEncoding(949)!;
        string source = CreateZip(korean, ("자료.adofai", "chart"), ("에셋.png", "image"));
        AddUnicodePath(source, "자료.adofai");
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            LevelArchive result = LevelArchive.Extract(source);
            Assert.That(File.ReadAllText(Path.Combine(result.DirectoryPath, "에셋.png")), Is.EqualTo("image"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PreservesTheOriginalNameWhenUnicodePathIsStaleOrUtf8IsExplicit(bool utf8)
    {
        string path = utf8 ? "日本語.adofai" : "level.adofai";
        string source = CreateZip((path, "chart"));
        AddUnicodePath(source, "other.adofai", validChecksum: utf8);

        Assert.That(LevelArchive.Extract(source).Levels, Is.EqualTo(new[] { path }));
    }

    [Test]
    public void RejectsAnUnsafePathAfterUnicodeDecoding()
    {
        string source = CreateZip(("alias", "data"), ("level.adofai", "chart"));
        AddUnicodePath(source, "資料/NUL.txt");
        Assert.That(() => LevelArchive.Extract(source), Throws.TypeOf<InvalidDataException>());
        AssertNoOutput();
    }

    [TestCase(936, "zh-CN", "谱面/节奏.adofai")]
    [TestCase(950, "zh-TW", "譜面/節奏.adofai")]
    [TestCase(932, "ja-JP", "譜面/リズム.adofai")]
    [TestCase(949, "ko-KR", "자료/리듬.adofai")]
    [TestCase(437, "en-US", "café.adofai")]
    public void ReadsLegacyNamesWithoutEncodingMetadata(int codePage, string culture, string path)
    {
        Encoding encoding = CodePagesEncodingProvider.Instance.GetEncoding(codePage)!;
        string source = CreateZip(encoding, (path, "chart"));
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.That(LevelArchive.Extract(source).Levels,
                Is.EqualTo(new[] { path.Replace('/', Path.DirectorySeparatorChar) }));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public void BackupOnlyArchiveDoesNotLeaveAnExtraction()
    {
        string source = CreateZip(("nested/BACKUP.ADOFAI", "backup"), ("song.ogg", "audio"));
        Assert.That(() => LevelArchive.Extract(source), Throws.TypeOf<InvalidDataException>());
        AssertNoOutput();
    }

    [TestCase("../existing.txt")]
    [TestCase("nested\\..\\existing.txt")]
    [TestCase("/rooted.txt")]
    [TestCase("C:drive-relative.txt")]
    [TestCase("./unclean.txt")]
    [TestCase("nested/NUL.txt")]
    [TestCase("nested/trailing. ")]
    [TestCase("level.adofai")]
    [TestCase("LEVEL.ADOFAI")]
    [TestCase("level.adofai/child.txt")]
    public void RejectsUnsafeOrConflictingPathsBeforeWriting(string invalidPath)
    {
        string source = CreateZip(("level.adofai", "chart"), (invalidPath, "unexpected"));
        string existing = Path.Combine(_directory, "existing.txt");
        File.WriteAllText(existing, "original");
        Assert.That(() => LevelArchive.Extract(source), Throws.TypeOf<InvalidDataException>());
        Assert.That(File.ReadAllText(existing), Is.EqualTo("original"));
        AssertNoOutput();
    }

    [Test]
    public void RejectsArchivedSymlink()
    {
        string source = Path.Combine(_directory, "archive.zip");
        using (ZipArchive zip = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            zip.CreateEntry("level.adofai");
            zip.CreateEntry("link").ExternalAttributes = unchecked((int)0xa1ff0000);
        }
        Assert.That(() => LevelArchive.Extract(source), Throws.TypeOf<InvalidDataException>());
        AssertNoOutput();
    }

    [TestCase(4, 20)]
    [TestCase(5, 9)]
    public void RejectsDeclaredFileAndTotalSizes(long maxFileBytes, long maxTotalBytes)
    {
        string source = CreateZip(("level.adofai", "chart"), ("asset", "asset"));
        Assert.That(() => LevelArchive.Extract(source, maxFileBytes, maxTotalBytes), Throws.TypeOf<InvalidDataException>());
        AssertNoOutput();
    }

    [TestCase(1u)]
    [TestCase(100u)]
    public void CleansUpWhenEntrySizeIsCorrupted(uint declaredSize)
    {
        string source = CreateZip(("level.adofai", "chart"));
        // .NET may cap the stream at the smaller declared size; the original CRC still catches truncation.
        ChangeCentralDirectoryField(source, 24, declaredSize);
        Assert.That(() => LevelArchive.Extract(source), Throws.TypeOf<InvalidDataException>());
        AssertNoOutput();
    }

    [Test]
    public void CleansUpWhenChecksumDiffersButSizeMatches()
    {
        string source = CreateZip(("level.adofai", "chart"));
        ChangeCentralDirectoryField(source, 16, 0);
        Assert.That(() => LevelArchive.Extract(source), Throws.TypeOf<InvalidDataException>());
        AssertNoOutput();
    }

    private static void ChangeCentralDirectoryField(string source, int fieldOffset, uint value)
    {
        byte[] data = File.ReadAllBytes(source);
        // For this one-entry, comment-free ZIP, read the central-directory offset from the EOCD.
        int centralDirectory = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(data.Length - 6, 4));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(centralDirectory, 4)), Is.EqualTo(0x02014b50u));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(centralDirectory + fieldOffset, 4), value);
        File.WriteAllBytes(source, data);
    }

    private static void AddUnicodePath(string source, string name, bool validChecksum = true)
    {
        byte[] data = File.ReadAllBytes(source);
        int central = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(data.Length - 6, 4));
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(central + 28, 2));
        int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(central + 30, 2));
        int insert = central + 46 + nameLength + extraLength;
        byte[] utf8 = Encoding.UTF8.GetBytes(name);
        uint crc = Crc32.HashToUInt32(data.AsSpan(central + 46, nameLength));
        byte[] extra = new byte[9 + utf8.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(extra, 0x7075);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(2), (ushort)(extra.Length - 4));
        extra[4] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(extra.AsSpan(5), validChecksum ? crc : ~crc);
        utf8.CopyTo(extra, 9);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(central + 30), (ushort)(extraLength + extra.Length));
        uint directorySize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(data.Length - 10));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(data.Length - 10), directorySize + (uint)extra.Length);
        using var output = File.Create(source);
        output.Write(data.AsSpan(0, insert));
        output.Write(extra);
        output.Write(data.AsSpan(insert));
    }

    private void AssertNoOutput() => Assert.That(Directory.EnumerateDirectories(_directory), Is.Empty);

    private string CreateZip(params (string Name, string Content)[] entries) => CreateZip(null, entries);

    private string CreateZip(Encoding? encoding, params (string Name, string Content)[] entries)
    {
        string path = Path.Combine(_directory, "levels.ZIP");
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create, encoding);
        foreach (var (name, content) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name);
            if (content.Length == 0)
                continue;
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return path;
    }
}

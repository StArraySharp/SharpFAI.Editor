using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace SharpFAI.Editor;

internal static class ZipEntryNames
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Encoding[] Legacy = new[] { 949, 932, 936, 950, 437 }
        .Select(codePage => CodePagesEncodingProvider.Instance.GetEncoding(codePage,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!)
        .ToArray();

    // ZipArchive supplies decompression and entry metadata but doesn't expose the
    // raw name or its Unicode path extra field. Read only those directory fields.
    public static string[] Read(Stream file, IReadOnlyList<ZipArchiveEntry> entries)
    {
        file.Position = DirectoryOffset(file);
        var names = new string[entries.Count];
        var unresolved = new List<(int Index, byte[] Name)>();
        var matches = new int[Legacy.Length];
        Span<byte> header = stackalloc byte[46];
        byte[] extraBuffer = new byte[ushort.MaxValue];
        for (int i = 0; i < entries.Count; i++)
        {
            file.ReadExactly(header);
            if (U32(header) != 0x02014b50 || U32(header[16..]) != entries[i].Crc32)
                throw InvalidDirectory();

            int nameLength = U16(header[28..]);
            if (nameLength > LevelArchive.MaxPathLength * 4)
                throw new InvalidDataException("The ZIP contains a path that exceeds the import limit.");
            byte[] name = new byte[nameLength];
            file.ReadExactly(name);
            Span<byte> extra = extraBuffer.AsSpan(0, U16(header[30..]));
            file.ReadExactly(extra);
            file.Position += U16(header[32..]);

            bool utf8 = (U16(header[8..]) & 0x0800) != 0;
            if (entries[i].FullName != (utf8 ? Encoding.UTF8 : Encoding.Latin1).GetString(name))
                throw InvalidDirectory();
            if (utf8)
            {
                names[i] = Decode(Utf8, name) ??
                    throw new InvalidDataException("The ZIP contains an invalid UTF-8 path.");
                continue;
            }

            string? unicode = ReadUnicodePath(name, extra);
            string? plain = Decode(Utf8, name);
            if (unicode is not null)
            {
                names[i] = unicode;
                if (plain != unicode)
                    for (int j = 0; j < Legacy.Length; j++)
                        if (Decode(Legacy[j], name) == unicode)
                            matches[j]++;
            }
            else if (plain is not null)
                names[i] = plain;
            else
                unresolved.Add((i, name));
        }

        if (unresolved.Count == 0)
            return names;

        // Estimate one encoding for the archive using any verified Unicode names
        // as evidence. The locale helps when the same byte pairs fit several code pages.
        int locale = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        var ordered = Legacy.Select((encoding, i) => (Encoding: encoding, Matches: matches[i],
                Score: unresolved.Sum(entry => Score(encoding, entry.Name, locale))))
            .OrderByDescending(candidate => candidate.Matches)
            .ThenByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Encoding).ToArray();
        foreach (var (index, name) in unresolved)
            names[index] = ordered.Select(encoding => Decode(encoding, name)).First(decoded => decoded is not null)!;
        return names;
    }

    private static long DirectoryOffset(Stream file)
    {
        const int endSize = 22;
        byte[] tail = new byte[(int)Math.Min(file.Length, endSize + ushort.MaxValue)];
        long tailOffset = file.Length - tail.Length;
        file.Position = tailOffset;
        file.ReadExactly(tail);
        Span<byte> locator = stackalloc byte[20];
        Span<byte> record = stackalloc byte[56];
        for (int i = tail.Length - endSize; i >= 0; i--)
        {
            ReadOnlySpan<byte> end = tail.AsSpan(i);
            if (U32(end) != 0x06054b50 || U16(end[20..]) > end.Length - endSize)
                continue;
            long endOffset = tailOffset + i;
            long directory = U32(end[16..]);
            // The ZIP64 locator, if present, immediately precedes the standard EOCD.
            if (endOffset >= 20 &&
                (U16(end[4..]) == ushort.MaxValue || U16(end[10..]) == ushort.MaxValue || directory == uint.MaxValue))
            {
                long locatorOffset = endOffset - locator.Length;
                file.Position = locatorOffset;
                file.ReadExactly(locator);
                if (U32(locator) == 0x07064b50)
                {
                    long recordOffset = BinaryPrimitives.ReadInt64LittleEndian(locator[8..]);
                    if (recordOffset < 0 || recordOffset > locatorOffset - record.Length)
                        throw InvalidDirectory();
                    file.Position = recordOffset;
                    file.ReadExactly(record);
                    if (U32(record) != 0x06064b50)
                        throw InvalidDirectory();
                    directory = BinaryPrimitives.ReadInt64LittleEndian(record[48..]);
                    endOffset = recordOffset;
                }
            }
            if (directory >= 0 && directory <= endOffset)
                return directory;
        }
        throw InvalidDirectory();
    }

    private static string? ReadUnicodePath(ReadOnlySpan<byte> name, ReadOnlySpan<byte> extra)
    {
        while (extra.Length >= 4)
        {
            int length = U16(extra[2..]);
            if (length > extra.Length - 4)
                break;
            ReadOnlySpan<byte> data = extra.Slice(4, length);
            if (U16(extra) == 0x7075 && length > 5 && data[0] == 1 && U32(data[1..]) == Crc32.HashToUInt32(name))
            {
                if (Decode(Utf8, data[5..]) is { } decoded)
                {
                    if (decoded.Length > LevelArchive.MaxPathLength + 1)
                        throw new InvalidDataException("The ZIP contains a path that exceeds the import limit.");
                    return decoded;
                }
            }
            extra = extra[(4 + length)..];
        }
        return null;
    }

    private static int Score(Encoding encoding, byte[] name, int locale)
    {
        if (Decode(encoding, name) is not { } decoded)
            return -40;
        int score = 0;
        foreach (char c in decoded)
        {
            if (c <= 127)
                continue;
            if ((encoding.CodePage == 932 && c is >= '\u3041' and <= '\u30fe') ||
                (encoding.CodePage == 949 && c is >= '\uac00' and <= '\ud7a3'))
                score += 4;
            else if (c is >= '\u3400' and <= '\u9fff' ||
                     (encoding.CodePage == 437 && c is >= '\u00c0' and <= '\u024f'))
                score += 2;
            else if (char.IsControl(c) || c is >= '\u2500' and <= '\u259f' or
                         >= '\ue000' and <= '\uf8ff' or >= '\uff61' and <= '\uff9f')
                score -= 4;
            if (encoding.CodePage == locale)
                score += 2;
        }
        return score;
    }

    private static string? Decode(Encoding encoding, ReadOnlySpan<byte> name)
    {
        try { return encoding.GetString(name); }
        catch (DecoderFallbackException) { return null; }
    }

    private static ushort U16(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt16LittleEndian(data);
    private static uint U32(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt32LittleEndian(data);
    private static InvalidDataException InvalidDirectory() => new("The ZIP directory contains invalid entry metadata.");
}

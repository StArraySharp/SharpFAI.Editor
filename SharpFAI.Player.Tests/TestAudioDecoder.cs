using System.Buffers.Binary;
using Thrust.Audio;

namespace SharpFAI.Player.Tests;

public sealed class TestAudioDecoder
{
    [TestCase(false, 1)]
    [TestCase(false, 2)]
    [TestCase(true, 2)]
    public void FloatWavConvertsToInterleavedPcm16(bool extensible, int channels)
    {
        float[] samples = [0f, 0.5f, -0.5f, -1f, 1f, 1.25f, -1.25f, float.NaN];
        short[] expected = [0, 16384, -16384, -32768, 32767, 32767, -32768, 0];
        Guid? subtype = extensible ? new Guid("00000003-0000-0010-8000-00aa00389b71") : null;
        DecodedAudio? audio = DecodeWav(extensible ? (ushort)0xFFFE : (ushort)3, channels, subtype,
            writer => { foreach (float sample in samples) writer.Write(sample); });

        Assert.That(audio, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(audio!.SampleRate, Is.EqualTo(44100));
            Assert.That(audio.Channels, Is.EqualTo(channels));
            Assert.That(audio.Duration, Is.EqualTo(samples.Length / (44100.0 * channels)));
            Assert.That(ReadPcm(audio), Is.EqualTo(expected));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void IntegerPcm32StillUsesHighSixteenBits(bool extensible)
    {
        Guid? subtype = extensible ? new Guid("00000001-0000-0010-8000-00aa00389b71") : null;
        DecodedAudio? audio = DecodeWav(extensible ? (ushort)0xFFFE : (ushort)1, 1, subtype,
            writer => writer.Write(unchecked((int)0xC0001234)));

        Assert.That(audio, Is.Not.Null);
        Assert.That(ReadPcm(audio!), Is.EqualTo(new short[] { -16384 }));
    }

    [Test]
    public void ExtensibleWavRejectsUnknownSubtype()
    {
        var subtype = new Guid("00000003-0000-0010-8000-00aa00389b72");
        Assert.That(DecodeWav(0xFFFE, 1, subtype, writer => writer.Write(0.5f)), Is.Null);
    }

    [Test]
    public void FloatWavRejectsIncompleteStereoFrame()
    {
        Assert.That(DecodeWav(3, 2, null, writer => writer.Write(0.5f)), Is.Null);
    }

    [TestCase("mono", 1, 32_000, 5_120)]
    [TestCase("stereo", 2, 44_100, 7_056)]
    public void Mp3PreservesChannelsAndTrimsTaggedEncoderPadding(string fixture, int channels, int rate, int frames)
    {
        using Stream input = typeof(TestAudioDecoder).Assembly.GetManifestResourceStream(
            $"SharpFAI.Player.Tests.Fixtures.Audio.{fixture}.mp3")!;
        using var bytes = new MemoryStream();
        input.CopyTo(bytes);
        byte[] source = bytes.ToArray();
        var audio = AudioDecoder.Decode(source, Guid.NewGuid().ToString("N"), ".mp3");
        Assert.That(audio, Is.Not.Null);
        short[] pcm = ReadPcm(audio!);
        Assert.Multiple(() =>
        {
            Assert.That(audio!.Channels, Is.EqualTo(channels));
            Assert.That(audio.SampleRate, Is.EqualTo(rate));
            Assert.That(pcm, Has.Length.EqualTo(frames * channels));
            Assert.That(pcm.Take(64 * channels).Any(sample => Math.Abs((int)sample) > 1000), Is.True,
                "The tone begins with the first decoded samples.");
            if (channels == 2)
                Assert.That(Enumerable.Range(0, frames).Any(i => pcm[2 * i] != pcm[2 * i + 1]), Is.True);
        });

        string path = Path.Combine(Path.GetTempPath(), $"sharpfai-decode-{Guid.NewGuid():N}.MP3");
        try
        {
            File.WriteAllBytes(path, source);
            Assert.That(AudioDecoder.Decode(path)?.Pcm, Is.EqualTo(audio!.Pcm));
            AssertStreamMatches(path, audio!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void Mp3KeepsSamplePositionWhenSeekingAcrossCompressedCheckpoints()
    {
        using Stream input = typeof(TestAudioDecoder).Assembly.GetManifestResourceStream(
            "SharpFAI.Player.Tests.Fixtures.Audio.stereo.mp3")!;
        using var bytes = new MemoryStream();
        input.CopyTo(bytes);
        byte[] fixture = bytes.ToArray();
        int audioOffset = fixture.AsSpan(0, 3).SequenceEqual("ID3"u8)
            ? 10 + ((fixture[6] << 21) | (fixture[7] << 14) | (fixture[8] << 7) | fixture[9]) : 0;
        using var repeated = new MemoryStream();
        for (int i = 0; i < 48; i++)
            repeated.Write(fixture.AsSpan(audioOffset));
        Assert.That(DecodeAndCheckStream(repeated.ToArray(), ".mp3"), Is.Not.Null);
    }

    [TestCase(".aif")]
    [TestCase(".aiff")]
    public void BigEndianAiffHonorsChannelOrderAndSoundOffset(string extension)
    {
        byte[] samples = [0x40, 0, 1, 0xc0, 0, 2, 0x7f, 0xff, 0xff, 0x80, 0, 0];
        var audio = DecodeAiff(extension, null, 2, 2, 24, samples);
        Assert.That(audio, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(audio!.SampleRate, Is.EqualTo(48_000));
            Assert.That(audio.Channels, Is.EqualTo(2));
            Assert.That(ReadPcm(audio), Is.EqualTo(new short[] { 16384, -16384, 32767, -32768 }));
        });
    }

    [Test]
    public void AiffSupportsSignedEightBitAndLittleEndianAifc()
    {
        var signed = DecodeAiff(".aif", null, 1, 3, 8, [0x80, 0, 0x7f]);
        Assert.That(ReadPcm(signed!), Is.EqualTo(new short[] { -32768, 0, 32512 }));
        var swapped = DecodeAiff(".aifc", "sowt", 1, 2, 16, [0x34, 0x12, 0, 0xc0]);
        Assert.That(ReadPcm(swapped!), Is.EqualTo(new short[] { 0x1234, -16384 }));
    }

    [Test]
    public void AiffConvertsFloatingPointAndRejectsInvalidOrCompressedSamples()
    {
        var audio = DecodeAiff(".aif", "fl32", 1, 2, 32, [0x3f, 0, 0, 0, 0xbf, 0x80, 0, 0]);
        Assert.That(ReadPcm(audio!), Is.EqualTo(new short[] { 16384, -32768 }));
        Assert.That(DecodeAiff(".aif", null, 2, 1, 16, [0, 0]), Is.Null);
        Assert.That(DecodeAiff(".aifc", "ulaw", 1, 1, 8, [0]), Is.Null);
    }

    private static DecodedAudio? DecodeAiff(string extension, string? format, ushort channels, uint frames,
        ushort bits, byte[] samples)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("FORM"u8);
        writer.Write(0);
        writer.Write(format is null ? "AIFF"u8 : "AIFC"u8);
        writer.Write("ANNO"u8);
        writer.Write(BinaryPrimitives.ReverseEndianness(1));
        writer.Write((ushort)0);
        writer.Write("COMM"u8);
        writer.Write(BinaryPrimitives.ReverseEndianness(format is null ? 18 : 24));
        writer.Write(BinaryPrimitives.ReverseEndianness(channels));
        writer.Write(BinaryPrimitives.ReverseEndianness(frames));
        writer.Write(BinaryPrimitives.ReverseEndianness(bits));
        writer.Write(Convert.FromHexString("400EBB80000000000000")); // Extended float, 48,000 Hz.
        if (format is not null)
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes(format));
            writer.Write((ushort)0);
        }
        writer.Write("SSND"u8);
        int soundSize = 9 + samples.Length;
        writer.Write(BinaryPrimitives.ReverseEndianness(soundSize));
        writer.Write(BinaryPrimitives.ReverseEndianness(1)); // Offset before the first sample.
        writer.Write(0); // No disk block alignment.
        writer.Write((byte)0xff);
        writer.Write(samples);
        if ((soundSize & 1) != 0) writer.Write((byte)0);
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return DecodeAndCheckStream(bytes, extension);
    }

    private static short[] ReadPcm(DecodedAudio audio) => Enumerable.Range(0, audio.Pcm.Length / 2)
        .Select(i => BinaryPrimitives.ReadInt16LittleEndian(audio.Pcm.AsSpan(i * 2))).ToArray();

    private static DecodedAudio? DecodeWav(ushort format, int channels, Guid? subtype, Action<BinaryWriter> writeSamples)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVEJUNK"u8);
        writer.Write(1);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write("fmt "u8);
        writer.Write(subtype.HasValue ? 40 : 16);
        writer.Write(format);
        writer.Write((ushort)channels);
        writer.Write(44100);
        writer.Write(44100 * channels * sizeof(float));
        writer.Write((ushort)(channels * sizeof(float)));
        writer.Write((ushort)32);
        if (subtype is Guid guid)
        {
            writer.Write((ushort)22);
            writer.Write((ushort)32);
            writer.Write(channels == 1 ? 4 : 3);
            writer.Write(guid.ToByteArray());
        }
        writer.Write("data"u8);
        long dataSizeOffset = stream.Position;
        writer.Write(0);
        writeSamples(writer);
        long length = stream.Length;
        stream.Position = dataSizeOffset;
        writer.Write((int)(length - dataSizeOffset - sizeof(int)));
        stream.Position = 4;
        writer.Write((int)(length - 8));

        return DecodeAndCheckStream(stream.ToArray(), ".wav");
    }

    private static DecodedAudio? DecodeAndCheckStream(byte[] bytes, string extension)
    {
        var audio = AudioDecoder.Decode(bytes, Guid.NewGuid().ToString("N"), extension);
        string path = Path.Combine(Path.GetTempPath(), $"sharpfai-stream-{Guid.NewGuid():N}{extension}");
        try
        {
            File.WriteAllBytes(path, bytes);
            if (audio is null)
                Assert.That(AudioDecoder.OpenStream(path), Is.Null);
            else
                AssertStreamMatches(path, audio);
            return audio;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertStreamMatches(string path, DecodedAudio decoded)
    {
        var streamed = AudioDecoder.OpenStream(path);
        Assert.That(streamed, Is.Not.Null);
        Assert.That(streamed!.IsStreamed, Is.True);
        Assert.That(streamed.Duration, Is.EqualTo(decoded.Duration));
        Assert.That(streamed.Channels, Is.EqualTo(decoded.Channels));
        Assert.That(streamed.SampleRate, Is.EqualTo(decoded.SampleRate));

        int channels = streamed.Channels;
        var expected = ReadPcm(decoded);
        using var reader = streamed.OpenReader();
        using var independent = streamed.OpenReader();
        var samples = new short[73 * channels];
        long end = streamed.FrameCount;
        foreach (long first in new[] { end / 2, 0, end - 1, end })
        {
            reader.Seek(first);
            int count = reader.Read(samples);
            Assert.That(count, Is.EqualTo(Math.Min(73, end - first)));
            Assert.That(samples.AsSpan(0, count * channels).ToArray(),
                Is.EqualTo(expected.AsSpan((int)first * channels, count * channels).ToArray()));
            Assert.That(reader.Position, Is.EqualTo(first + count));
        }
        Assert.That(independent.Position, Is.Zero);
        for (int first = 0; first < end;)
        {
            int count = independent.Read(samples);
            Assert.That(count, Is.Positive);
            Assert.That(samples.AsSpan(0, count * channels).ToArray(),
                Is.EqualTo(expected.AsSpan(first * channels, count * channels).ToArray()));
            first += count;
        }
        Assert.That(independent.Read(samples), Is.Zero);
    }
}

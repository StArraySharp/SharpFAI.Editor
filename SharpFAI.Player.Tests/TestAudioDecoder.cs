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

        return AudioDecoder.Decode(stream.ToArray(), Guid.NewGuid().ToString("N"), ".wav");
    }
}

using System.Buffers.Binary;
using Diktat.Transcription.Audio;
using Diktat.Transcription.Engines;

namespace Diktat.Transcription.Tests;

public class AudioResamplerTests
{
    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    [InlineData(22_050)]
    [InlineData(8_000)]
    [InlineData(16_000)]
    [InlineData(47_999)] // no small rational ratio: computed without the table
    public void KeepsLengthAndFrequency(int rate)
    {
        var input = TestAudio.Sine(440, rate, 2.0);
        var output = AudioResampler.Resample(input, rate, 16_000);
        Assert.InRange(output.Length, 32_000 - 2, 32_000 + 2);
        Assert.InRange(TestAudio.Frequency(output, 16_000), 437, 443);
        // Amplitude 0.5 sine: RMS about 0.354.
        Assert.InRange(TestAudio.Rms(output.AsSpan(1600, 28_800)), 0.33, 0.37);
    }

    [Fact]
    public void FiltersWhatAbove8kHzWouldAlias()
    {
        var input = TestAudio.Sine(11_000, 48_000, 1.0);
        var output = AudioResampler.Resample(input, 48_000, 16_000);
        // Without a low-pass the 11 kHz tone would fold to 5 kHz at full strength.
        Assert.True(TestAudio.Rms(output.AsSpan(1600, 12_800)) < 0.01);
    }

    [Fact]
    public void BlocksGiveTheSameResultAsOneCall()
    {
        var input = TestAudio.Sine(1000, 44_100, 1.5);
        var whole = AudioResampler.Resample(input, 44_100, 16_000);

        var resampler = new StreamingResampler(44_100, 16_000);
        var output = new FloatBuffer();
        for (var start = 0; start < input.Length; start += 1234)
        {
            resampler.Process(input.AsSpan(start, Math.Min(1234, input.Length - start)), output);
        }
        resampler.Flush(output);

        Assert.Equal(whole.Length, output.Count);
        for (var i = 0; i < whole.Length; i++) Assert.Equal(whole[i], output.AsSpan()[i], 5);
    }

    [Fact]
    public void StereoIsAveragedToMono()
    {
        var left = TestAudio.Sine(300, 16_000, 0.5);
        var right = left.Select(s => -s).ToArray();
        var silent = AudioResampler.ToMono16k(TestAudio.Interleave(left, right), 16_000, 2);
        Assert.Equal(left.Length, silent.Length);
        Assert.True(TestAudio.Rms(silent) < 1e-6);

        var same = AudioResampler.ToMono16k(TestAudio.Interleave(left, left), 48_000, 2);
        Assert.InRange(same.Length, left.Length / 3 - 1, left.Length / 3 + 1);
    }
}

public class WavFileTests
{
    [Theory]
    [InlineData(WavSampleFormat.Pcm16, 1, 16_000)]
    [InlineData(WavSampleFormat.Float32, 1, 44_100)]
    [InlineData(WavSampleFormat.Pcm16, 2, 48_000)]
    public void RoundTrip(WavSampleFormat format, int channels, int rate)
    {
        var mono = TestAudio.Sine(500, rate, 0.25);
        var samples = channels == 1 ? mono : TestAudio.Interleave(mono, mono.Select(s => s * 0.5f).ToArray());
        var path = Path.Combine(TestAudio.TempDirectory(), "test.wav");
        WavFile.Write(path, samples, rate, channels, format);

        var read = WavFile.Read(path);
        Assert.Equal(rate, read.SampleRate);
        Assert.Equal(channels, read.Channels);
        Assert.Equal(samples.Length, read.Samples.Length);
        var tolerance = format == WavSampleFormat.Pcm16 ? 1.0 / 32_000 : 1e-7;
        for (var i = 0; i < samples.Length; i++) Assert.True(Math.Abs(samples[i] - read.Samples[i]) <= tolerance);
        Assert.Equal(TimeSpan.FromSeconds(0.25), read.Duration);
    }

    [Fact]
    public void ReadMono16kConverts()
    {
        var path = Path.Combine(TestAudio.TempDirectory(), "stereo48.wav");
        var tone = TestAudio.Sine(440, 48_000, 1.0);
        WavFile.Write(path, TestAudio.Interleave(tone, tone), 48_000, 2);
        var mono = WavFile.ReadMono16k(path);
        Assert.InRange(mono.Length, 15_998, 16_002);
        Assert.InRange(TestAudio.Frequency(mono, 16_000), 437, 443);
    }

    [Fact]
    public void WriterUpdatesTheHeaderOnFlush()
    {
        var path = Path.Combine(TestAudio.TempDirectory(), "live.wav");
        using var writer = new WavWriter(path, 16_000);
        writer.Write(new float[1600]);
        writer.Flush();
        var bytes = ReadShared(path);
        Assert.Equal(3200u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
        Assert.Equal((uint)(bytes.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
    }

    [Fact]
    public void RecoveryFixesTheHeaderOfAnInterruptedRecording()
    {
        var path = Path.Combine(TestAudio.TempDirectory(), "crash.wav");
        WavFile.Write(path, TestAudio.Sine(300, 16_000, 1.0), 16_000);
        // What a crash leaves: the samples, but sizes of zero.
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = 4;
            stream.Write(new byte[4]);
            stream.Position = 40;
            stream.Write(new byte[4]);
        }
        var header = File.ReadAllBytes(path);
        var patches = WavRecovery.SizePatches(header, header.Length);
        Assert.NotNull(patches);
        Assert.Equal([(4, (uint)(header.Length - 8)), (40, 32_000u)], patches);

        Assert.True(WavRecovery.RepairIfNeeded(path));
        Assert.False(WavRecovery.RepairIfNeeded(path));
        Assert.Equal(16_000, WavFile.Read(path).Samples.Length);
    }

    [Fact]
    public void ReaderReadsToTheEndWhenTheDataSizeIsZero()
    {
        var path = Path.Combine(TestAudio.TempDirectory(), "zero.wav");
        WavFile.Write(path, new float[800], 16_000);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = 40;
            stream.Write(new byte[4]);
        }
        Assert.Equal(800, WavFile.Read(path).Samples.Length);
    }

    [Fact]
    public void SizePatchesIgnoresOtherFiles()
    {
        Assert.Null(WavRecovery.SizePatches("not a wav file at all"u8, 21));
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}

public class AudioFileDecoderTests
{
    [Fact]
    public void DecodesWavToMono16k()
    {
        var path = Path.Combine(TestAudio.TempDirectory(), "voice.wav");
        WavFile.Write(path, TestAudio.Sine(440, 22_050, 1.0), 22_050);
        var samples = new AudioFileDecoder().DecodeToMono16k(path);
        Assert.InRange(samples.Length, 15_998, 16_002);
    }

    [Fact]
    public void DecodesOpusInOgg()
    {
        var path = Path.Combine(TestAudio.TempDirectory(), "voice.opus");
        OpusTestFile.Write(path, TestAudio.Sine(440, 48_000, 1.0, 0.4f), channels: 1);

        var decoder = new AudioFileDecoder();
        var data = decoder.Decode(path);
        Assert.Equal(48_000, data.SampleRate);
        Assert.Equal(1, data.Channels);

        var samples = decoder.DecodeToMono16k(path);
        // The encoder adds some look-ahead and pads the last frame.
        Assert.InRange(samples.Length, 15_000, 17_500);
        Assert.InRange(TestAudio.Frequency(samples.AsSpan(1600, 12_800), 16_000), 430, 450);
        Assert.InRange(TestAudio.Rms(samples.AsSpan(3200, 9600)), 0.2, 0.35);
    }

    [Fact]
    public void DecodesStereoOpus()
    {
        var path = Path.Combine(TestAudio.TempDirectory(), "stereo.ogg");
        var tone = TestAudio.Sine(440, 48_000, 0.5, 0.4f);
        OpusTestFile.Write(path, TestAudio.Interleave(tone, tone), channels: 2);
        var data = new AudioFileDecoder().Decode(path);
        Assert.Equal(2, data.Channels);
        Assert.InRange(data.Duration.TotalSeconds, 0.45, 0.6);
    }

    [Fact]
    public void RejectsUnknownFilesWithAGermanMessage()
    {
        var path = Path.Combine(TestAudio.TempDirectory(), "notes.txt");
        File.WriteAllText(path, "Das ist kein Audio.");
        var error = Assert.Throws<AudioDecodeException>(() => new AudioFileDecoder().DecodeToMono16k(path));
        Assert.Equal("Diese Audiodatei kann Diktat nicht öffnen.", error.Message);
    }

    [Fact]
    public void RegisteredDecodersAreUsedForOtherFormats()
    {
        var path = Path.Combine(TestAudio.TempDirectory(), "memo.m4a");
        File.WriteAllBytes(path, [0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p']);
        var decoder = new AudioFileDecoder();
        decoder.Register(new FakeM4aDecoder());
        var samples = decoder.DecodeToMono16k(path);
        Assert.Equal(16_000, samples.Length);
    }

    [Fact]
    public void Mp3IsRecognizedByItsHeader()
    {
        var mp3 = new Mp3Decoder();
        Assert.True(mp3.CanDecode("x.bin", "ID3\u0004"u8));
        Assert.True(mp3.CanDecode("x.bin", [0xFF, 0xFB, 0x90, 0x64]));
        Assert.False(mp3.CanDecode("x.aac", [0xFF, 0xF1, 0x50, 0x80])); // AAC in ADTS
        Assert.True(mp3.CanDecode("song.MP3", [0, 0, 0, 0]));
    }

    private sealed class FakeM4aDecoder : IAudioFileDecoder
    {
        public bool CanDecode(string path, ReadOnlySpan<byte> header) => path.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase);

        public IAudioSampleReader Open(string path) => new Reader();

        private sealed class Reader : IAudioSampleReader
        {
            private int _left = 32_000;
            public int SampleRate => 32_000;
            public int Channels => 1;

            public int Read(Span<float> buffer)
            {
                var n = Math.Min(buffer.Length, _left);
                buffer[..n].Fill(0.1f);
                _left -= n;
                return n;
            }

            public void Dispose() { }
        }
    }
}

public class AudioChunkerTests
{
    [Fact]
    public void ShortAudioIsOnePiece()
    {
        var chunks = AudioChunker.Split(new float[16_000 * 30], 60, 15);
        Assert.Equal([new AudioChunker.Chunk(0, 16_000 * 30)], chunks);
    }

    [Fact]
    public void CutsInThePauseNearTheWindowEnd()
    {
        // 150 s of tone with a pause at 50-51 s and 105-106 s.
        var samples = TestAudio.Sine(200, 16_000, 150, 0.3f);
        Array.Clear(samples, 50 * 16_000, 16_000);
        Array.Clear(samples, 105 * 16_000, 16_000);

        var chunks = AudioChunker.Split(samples, 60, 15);
        Assert.Equal(3, chunks.Count);
        Assert.InRange(chunks[0].End / 16_000.0, 50, 51);
        Assert.InRange(chunks[1].End / 16_000.0, 105, 106);
        Assert.Equal(samples.Length, chunks[^1].End);
        Assert.All(chunks, c => Assert.True(c.Length <= 60 * 16_000));
        for (var i = 1; i < chunks.Count; i++) Assert.Equal(chunks[i - 1].End, chunks[i].Start);
    }
}

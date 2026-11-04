using System.IO;
using NAudio.Wave;

namespace Diktat.Windows.Platform;

/// Scales the input level to the microphone at hand (port of the Mac AdaptiveLevel). The
/// loudest recent level counts as full scale, falling back slowly (about two seconds to half
/// when called every 50 ms), never below a floor, so room noise does not fill the bars.
public struct AdaptiveLevel
{
    public const float MinimumPeak = 0.25f;
    public const float Floor = 0.05f;
    private const float Decay = 0.985f;

    private float _peak;

    public float Peak => _peak == 0 ? MinimumPeak : _peak;

    public float Normalize(float raw)
    {
        _peak = Math.Max(raw, Math.Max(Peak * Decay, MinimumPeak));
        return Math.Clamp((raw - Floor) / (_peak - Floor), 0f, 1f);
    }

    public void Reset() => _peak = MinimumPeak;

    /// RMS in dB mapped to 0...1 the way the Mac recorder meters: -50 dB → 0, 0 dB → 1.
    public static float RawFromDecibels(float db) => Math.Clamp((db + 50f) / 50f, 0f, 1f);
}

/// Meters 16 kHz mono audio in 50 ms windows: level, loudest moment, digital silence.
internal sealed class SignalMeter
{
    /// Digital silence reads about -120 dB; the quietest real room is far above -110.
    public const float SilenceDb = -110f;
    private const int WindowSamples = 800; // 50 ms at 16 kHz

    private readonly int _noSignalSamples;
    private AdaptiveLevel _adaptive;
    private double _sumSquares;
    private int _windowCount;
    private long _silentSamples;

    public SignalMeter(TimeSpan noSignalAfter, int sampleRate = 16_000)
    {
        _noSignalSamples = (int)(noSignalAfter.TotalSeconds * sampleRate);
        Reset();
    }

    public volatile float Level;
    public volatile float CurrentDb = -160f;
    public volatile float PeakDb = -160f;
    public volatile bool NoSignal;

    public void Reset()
    {
        _adaptive.Reset();
        _sumSquares = 0;
        _windowCount = 0;
        _silentSamples = 0;
        Level = 0;
        CurrentDb = -160f;
        PeakDb = -160f;
        NoSignal = false;
    }

    public void Add(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            _sumSquares += sample * sample;
            if (++_windowCount < WindowSamples) continue;

            var rms = Math.Sqrt(_sumSquares / _windowCount);
            var db = (float)(20 * Math.Log10(Math.Max(rms, 0.000_001)));
            CurrentDb = db;
            if (db > PeakDb) PeakDb = db;
            Level = _adaptive.Normalize(AdaptiveLevel.RawFromDecibels(db));
            if (db < SilenceDb)
            {
                _silentSamples += _windowCount;
                NoSignal = _silentSamples > _noSignalSamples;
            }
            else
            {
                _silentSamples = 0;
                NoSignal = false;
            }
            _sumSquares = 0;
            _windowCount = 0;
        }
    }
}

/// Converts interleaved capture bytes (PCM 16/24/32 bit or IEEE float 32/64, plain or
/// WAVE_FORMAT_EXTENSIBLE) to mono float by averaging the channels.
internal sealed class PcmToMono
{
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly int _channels;
    private readonly int _bytesPerSample;
    private readonly bool _isFloat;
    private float[] _scratch = new float[4096];

    public int SampleRate { get; }

    public PcmToMono(WaveFormat format)
    {
        SampleRate = format.SampleRate;
        _channels = Math.Max(1, format.Channels);
        _bytesPerSample = format.BitsPerSample / 8;
        var encoding = format.Encoding;
        if (encoding == WaveFormatEncoding.Extensible && format is WaveFormatExtensible extensible)
        {
            if (extensible.SubFormat == SubtypeFloat) encoding = WaveFormatEncoding.IeeeFloat;
            else if (extensible.SubFormat == SubtypePcm) encoding = WaveFormatEncoding.Pcm;
            else throw new NotSupportedException($"Audio subformat {extensible.SubFormat} is not supported.");
        }
        _isFloat = encoding == WaveFormatEncoding.IeeeFloat;
        if (!_isFloat && encoding != WaveFormatEncoding.Pcm)
            throw new NotSupportedException($"Audio encoding {format.Encoding} is not supported.");
        if (_isFloat ? _bytesPerSample is not (4 or 8) : _bytesPerSample is not (1 or 2 or 3 or 4))
            throw new NotSupportedException($"{format.BitsPerSample}-bit audio is not supported.");
    }

    /// Converts whole frames of <paramref name="bytes"/>; the returned span is valid until the next call.
    public ReadOnlySpan<float> Convert(ReadOnlySpan<byte> bytes)
    {
        int frameBytes = _bytesPerSample * _channels;
        int frames = bytes.Length / frameBytes;
        if (_scratch.Length < frames) _scratch = new float[Math.Max(frames, _scratch.Length * 2)];
        float scale = 1f / _channels;
        int offset = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            for (int channel = 0; channel < _channels; channel++)
            {
                sum += ReadSample(bytes.Slice(offset, _bytesPerSample));
                offset += _bytesPerSample;
            }
            _scratch[frame] = sum * scale;
        }
        return _scratch.AsSpan(0, frames);
    }

    private float ReadSample(ReadOnlySpan<byte> b)
    {
        if (_isFloat)
        {
            return _bytesPerSample == 4
                ? BitConverter.ToSingle(b)
                : (float)BitConverter.ToDouble(b);
        }
        return _bytesPerSample switch
        {
            1 => (b[0] - 128) / 128f,
            2 => BitConverter.ToInt16(b) / 32768f,
            3 => ((b[0] << 8) | (b[1] << 16) | (b[2] << 24)) / 2147483648f,
            _ => BitConverter.ToInt32(b) / 2147483648f,
        };
    }
}

/// Streaming sample-rate converter: windowed-sinc (Blackman) low-pass interpolation with a
/// lookup table. Good enough for speech recognition at any ratio (48k, 44.1k, 32k, 8k → 16k).
/// Not thread-safe.
public sealed class StreamingResampler
{
    private const int ZeroCrossings = 12;
    private const int TableResolution = 256; // steps per input sample

    private readonly double _step;     // input samples per output sample
    private readonly double _halfWidth; // kernel half width in input samples
    private readonly float[] _table;
    private readonly bool _passThrough;

    private float[] _history = new float[8192];
    private int _historyCount;
    private long _historyStart;  // absolute input index of _history[0]
    private long _inputCount;    // absolute input samples received
    private long _outputCount;   // absolute output samples produced

    public int InputRate { get; }
    public int OutputRate { get; }

    public StreamingResampler(int inputRate, int outputRate)
    {
        if (inputRate <= 0 || outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        InputRate = inputRate;
        OutputRate = outputRate;
        _passThrough = inputRate == outputRate;
        _step = (double)inputRate / outputRate;
        // Cutoff in cycles per input sample, a little below the lower Nyquist frequency.
        double cutoff = 0.5 * Math.Min(1.0, (double)outputRate / inputRate) * 0.92;
        _halfWidth = ZeroCrossings / (2 * cutoff);
        int size = (int)Math.Ceiling(_halfWidth * TableResolution) + 2;
        _table = new float[size];
        for (int i = 0; i < size; i++)
        {
            double d = (double)i / TableResolution;
            if (d >= _halfWidth) { _table[i] = 0; continue; }
            double x = 2 * cutoff * d;
            double sinc = x == 0 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
            double w = d / _halfWidth;
            double blackman = 0.42 + 0.5 * Math.Cos(Math.PI * w) + 0.08 * Math.Cos(2 * Math.PI * w);
            _table[i] = (float)(sinc * blackman);
        }
    }

    /// Feeds input and appends all output that is ready to <paramref name="output"/>.
    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
        if (_passThrough)
        {
            foreach (var sample in input) output.Add(sample);
            _inputCount += input.Length;
            _outputCount += input.Length;
            return;
        }
        Append(input);
        Produce(output, flushing: false);
    }

    /// Emits the tail: as many output samples as the input length implies in total.
    public void Flush(List<float> output)
    {
        if (_passThrough) return;
        Produce(output, flushing: true);
    }

    private void Append(ReadOnlySpan<float> input)
    {
        if (_historyCount + input.Length > _history.Length)
        {
            Array.Resize(ref _history, Math.Max(_history.Length * 2, _historyCount + input.Length));
        }
        input.CopyTo(_history.AsSpan(_historyCount));
        _historyCount += input.Length;
        _inputCount += input.Length;
    }

    private void Produce(List<float> output, bool flushing)
    {
        long expectedTotal = (long)Math.Floor(_inputCount / _step);
        long historyEnd = _historyStart + _historyCount;
        while (true)
        {
            double t = _outputCount * _step;
            long first = (long)Math.Ceiling(t - _halfWidth);
            long last = (long)Math.Floor(t + _halfWidth);
            if (flushing ? _outputCount >= expectedTotal : last >= historyEnd) break;

            double sum = 0, weights = 0;
            long from = Math.Max(first, _historyStart);
            long to = Math.Min(last, historyEnd - 1);
            for (long k = from; k <= to; k++)
            {
                double d = Math.Abs(t - k) * TableResolution;
                int i = (int)d;
                if (i + 1 >= _table.Length) continue;
                double frac = d - i;
                double h = _table[i] + frac * (_table[i + 1] - _table[i]);
                sum += _history[k - _historyStart] * h;
                weights += h;
            }
            output.Add(weights > 1e-9 ? (float)(sum / weights) : 0f);
            _outputCount++;
        }

        // Drop history no future output needs.
        long keepFrom = (long)Math.Ceiling(_outputCount * _step - _halfWidth) - 1;
        int drop = (int)Math.Clamp(keepFrom - _historyStart, 0, _historyCount);
        if (drop > 4096 || (flushing && drop > 0))
        {
            Array.Copy(_history, drop, _history, 0, _historyCount - drop);
            _historyCount -= drop;
            _historyStart += drop;
        }
    }
}

/// Growable float storage in chunks, so long recordings never need one huge reallocation.
/// Not thread-safe.
internal sealed class FloatBuffer
{
    private const int ChunkSize = 1 << 16;
    private readonly List<float[]> _chunks = new();
    private int _lastCount = ChunkSize;

    public long Count { get; private set; }

    public void Add(ReadOnlySpan<float> samples)
    {
        while (!samples.IsEmpty)
        {
            if (_lastCount == ChunkSize)
            {
                _chunks.Add(new float[ChunkSize]);
                _lastCount = 0;
            }
            int n = Math.Min(samples.Length, ChunkSize - _lastCount);
            samples[..n].CopyTo(_chunks[^1].AsSpan(_lastCount));
            _lastCount += n;
            Count += n;
            samples = samples[n..];
        }
    }

    public void AddSilence(long count)
    {
        Span<float> zeros = stackalloc float[512];
        zeros.Clear();
        while (count > 0)
        {
            int n = (int)Math.Min(count, zeros.Length);
            Add(zeros[..n]);
            count -= n;
        }
    }

    public float[] ToArray(long start = 0)
    {
        start = Math.Clamp(start, 0, Count);
        var result = new float[Count - start];
        long position = 0;
        int written = 0;
        for (int c = 0; c < _chunks.Count; c++)
        {
            int length = c == _chunks.Count - 1 ? _lastCount : ChunkSize;
            long chunkEnd = position + length;
            if (chunkEnd > start)
            {
                int offset = (int)Math.Max(0, start - position);
                int n = length - offset;
                Array.Copy(_chunks[c], offset, result, written, n);
                written += n;
            }
            position = chunkEnd;
        }
        return result;
    }

    public void Clear()
    {
        _chunks.Clear();
        _lastCount = ChunkSize;
        Count = 0;
    }
}

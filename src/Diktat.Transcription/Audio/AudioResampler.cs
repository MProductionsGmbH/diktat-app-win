namespace Diktat.Transcription.Audio;

/// Converts audio to the 16 kHz mono float the speech models expect.
public static class AudioResampler
{
    public const int TargetSampleRate = 16_000;

    /// Any rate and channel count (interleaved) to 16 kHz mono.
    public static float[] ToMono16k(ReadOnlySpan<float> interleaved, int sampleRate, int channels)
    {
        var mono = channels == 1 ? interleaved.ToArray() : DownmixToMono(interleaved, channels);
        return Resample(mono, sampleRate, TargetSampleRate);
    }

    /// Averages the channels of interleaved audio.
    public static float[] DownmixToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (channels == 1) return interleaved.ToArray();
        var frames = interleaved.Length / channels;
        var mono = new float[frames];
        var scale = 1f / channels;
        for (var frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            var offset = frame * channels;
            for (var c = 0; c < channels; c++) sum += interleaved[offset + c];
            mono[frame] = sum * scale;
        }
        return mono;
    }

    /// Band-limited resampling of one channel (windowed sinc, see <see cref="StreamingResampler"/>).
    public static float[] Resample(ReadOnlySpan<float> mono, int fromRate, int toRate)
    {
        if (fromRate == toRate) return mono.ToArray();
        var resampler = new StreamingResampler(fromRate, toRate);
        var output = new FloatBuffer((int)((long)mono.Length * toRate / fromRate) + 64);
        resampler.Process(mono, output);
        resampler.Flush(output);
        return output.ToArray();
    }
}

/// A growable float array without List&lt;float&gt;'s per-item overhead.
public sealed class FloatBuffer(int capacity = 4096)
{
    private float[] _items = new float[Math.Max(16, capacity)];

    public int Count { get; private set; }

    public void Add(float value)
    {
        if (Count == _items.Length) Grow(Count + 1);
        _items[Count++] = value;
    }

    public void AddRange(ReadOnlySpan<float> values)
    {
        if (Count + values.Length > _items.Length) Grow(Count + values.Length);
        values.CopyTo(_items.AsSpan(Count));
        Count += values.Length;
    }

    public ReadOnlySpan<float> AsSpan() => _items.AsSpan(0, Count);

    public float[] ToArray() => _items.AsSpan(0, Count).ToArray();

    private void Grow(int needed)
    {
        var size = _items.Length;
        while (size < needed) size = size < 1 << 26 ? size * 2 : size + (1 << 26);
        Array.Resize(ref _items, size);
    }
}

/// Windowed-sinc (Blackman) resampler for one channel that works on blocks, so long files
/// never need to be in memory at their original rate. Rational ratios (48 kHz, 44.1 kHz,
/// 22.05 kHz, 8 kHz to 16 kHz) use a precomputed polyphase table.
///
/// The low-pass sits at 95 % of the lower Nyquist frequency with 16 zero crossings per side,
/// which keeps aliasing well below what matters for speech recognition.
public sealed class StreamingResampler
{
    private const int ZeroCrossings = 16;
    private const double Rolloff = 0.95;
    private const int MaxPhases = 4096;

    private readonly long _up;   // L: output steps per input step
    private readonly long _down; // M
    private readonly int _halfWidth;
    private readonly double _cutoff;
    private readonly float[][]? _table;

    // Input history: _buffer[0] is input sample number _bufferStart.
    private float[] _buffer = new float[8192];
    private int _bufferCount;
    private long _bufferStart;
    private long _inputCount;
    private long _outputIndex;
    private bool _flushed;

    public StreamingResampler(int fromRate, int toRate)
    {
        if (fromRate <= 0 || toRate <= 0) throw new ArgumentOutOfRangeException(nameof(fromRate), "Sample rates must be positive.");
        FromRate = fromRate;
        ToRate = toRate;
        var gcd = Gcd(fromRate, toRate);
        _up = toRate / gcd;
        _down = fromRate / gcd;
        _cutoff = Math.Min(1.0, (double)toRate / fromRate) * Rolloff;
        _halfWidth = (int)Math.Ceiling(ZeroCrossings / _cutoff);
        if (_up <= MaxPhases) _table = BuildTable();
        // The first output sample sits on input sample 0; the history must reach back that far.
        _bufferStart = -_halfWidth;
        _bufferCount = _halfWidth;
        EnsureCapacity(_bufferCount);
    }

    public int FromRate { get; }
    public int ToRate { get; }

    /// Feeds input and appends every output sample that can be computed so far.
    public void Process(ReadOnlySpan<float> input, FloatBuffer output)
    {
        if (_flushed) throw new InvalidOperationException("The resampler was flushed.");
        if (FromRate == ToRate)
        {
            output.AddRange(input);
            return;
        }
        Append(input);
        _inputCount += input.Length;
        Produce(output, _bufferStart + _bufferCount);
    }

    /// Ends the stream: pads with silence and emits the remaining output.
    public void Flush(FloatBuffer output)
    {
        if (_flushed) return;
        _flushed = true;
        if (FromRate == ToRate) return;
        Append(new float[_halfWidth + 1]);
        // As many output samples as the input duration holds.
        var total = (_inputCount * _up + _down - 1) / _down;
        Produce(output, _bufferStart + _bufferCount, total);
    }

    private void Produce(FloatBuffer output, long availableEnd, long limit = long.MaxValue)
    {
        while (_outputIndex < limit)
        {
            var position = _outputIndex * _down; // in units of 1/L input samples
            var center = position / _up;
            var phase = (int)(position % _up);
            var last = center + _halfWidth;
            if (last >= availableEnd) break;
            var first = center - _halfWidth + 1;
            var offset = (int)(first - _bufferStart);
            var window = _buffer.AsSpan(offset, 2 * _halfWidth);

            double sum = 0;
            if (_table is not null)
            {
                var weights = _table[phase];
                for (var k = 0; k < weights.Length; k++) sum += window[k] * weights[k];
            }
            else
            {
                var fraction = (double)phase / _up;
                double weightSum = 0;
                for (var k = 0; k < window.Length; k++)
                {
                    var w = Weight(k - _halfWidth + 1 - fraction);
                    weightSum += w;
                    sum += window[k] * w;
                }
                if (weightSum > 0) sum /= weightSum;
            }
            output.Add((float)sum);
            _outputIndex++;
        }
        Compact();
    }

    /// Drops history that no future output sample needs.
    private void Compact()
    {
        var nextCenter = _outputIndex * _down / _up;
        var keepFrom = nextCenter - _halfWidth + 1;
        var drop = (int)Math.Clamp(keepFrom - _bufferStart, 0, _bufferCount);
        if (drop < 4096) return;
        Array.Copy(_buffer, drop, _buffer, 0, _bufferCount - drop);
        _bufferCount -= drop;
        _bufferStart += drop;
    }

    private void Append(ReadOnlySpan<float> input)
    {
        EnsureCapacity(_bufferCount + input.Length);
        input.CopyTo(_buffer.AsSpan(_bufferCount));
        _bufferCount += input.Length;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buffer.Length) return;
        var size = _buffer.Length;
        while (size < needed) size *= 2;
        Array.Resize(ref _buffer, size);
    }

    private float[][] BuildTable()
    {
        var table = new float[_up][];
        for (var phase = 0; phase < _up; phase++)
        {
            var fraction = (double)phase / _up;
            var weights = new double[2 * _halfWidth];
            double sum = 0;
            for (var k = 0; k < weights.Length; k++)
            {
                weights[k] = Weight(k - _halfWidth + 1 - fraction);
                sum += weights[k];
            }
            // Unity gain at DC for every phase.
            table[phase] = weights.Select(w => (float)(w / sum)).ToArray();
        }
        return table;
    }

    /// Low-pass kernel at distance x (in input samples) from the output position.
    private double Weight(double x)
    {
        var span = _halfWidth;
        if (Math.Abs(x) >= span) return 0;
        var t = _cutoff * x;
        var sinc = Math.Abs(t) < 1e-12 ? 1.0 : Math.Sin(Math.PI * t) / (Math.PI * t);
        // Blackman window over [-span, span].
        var n = (x + span) / (2.0 * span);
        var window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * n) + 0.08 * Math.Cos(4 * Math.PI * n);
        return _cutoff * sinc * window;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}

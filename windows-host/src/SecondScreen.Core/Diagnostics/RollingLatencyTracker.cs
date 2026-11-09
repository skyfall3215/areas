namespace SecondScreen.Core.Diagnostics;

public sealed class RollingLatencyTracker
{
    private readonly double[] _samples;
    private readonly object _lock = new();
    private int _count;
    private int _index;

    public RollingLatencyTracker(int capacity = 120)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _samples = new double[capacity];
    }

    public int Capacity => _samples.Length;
    public int SampleCount { get { lock (_lock) return _count; } }

    public void AddSample(double value)
    {
        lock (_lock)
        {
            _samples[_index] = value;
            _index = (_index + 1) % _samples.Length;
            if (_count < _samples.Length)
            {
                _count++;
            }
        }
    }

    public LatencyStats GetStats()
    {
        lock (_lock)
        {
            if (_count == 0)
            {
                return new LatencyStats(0, 0, 0, 0, 0, 0);
            }

            double[] copy = new double[_count];
            for (int i = 0; i < _count; i++)
            {
                copy[i] = _samples[i];
            }

            Array.Sort(copy);

            double sum = 0;
            for (int i = 0; i < copy.Length; i++) sum += copy[i];
            double avg = sum / copy.Length;

            double min = copy[0];
            double max = copy[^1];

            int p50Idx = (int)Math.Floor(0.50 * (copy.Length - 1));
            int p95Idx = (int)Math.Floor(0.95 * (copy.Length - 1));

            return new LatencyStats(
                Average: avg,
                P50: copy[p50Idx],
                P95: copy[p95Idx],
                Min: min,
                Max: max,
                SampleCount: _count);
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _count = 0;
            _index = 0;
        }
    }
}

public readonly record struct LatencyStats(
    double Average,
    double P50,
    double P95,
    double Min,
    double Max,
    int SampleCount);


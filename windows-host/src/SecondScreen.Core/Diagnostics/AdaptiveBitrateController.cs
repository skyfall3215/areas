namespace SecondScreen.Core.Diagnostics;

/// <summary>
/// Thread-safe, deterministic AIMD (Additive Increase / Multiplicative Decrease) adaptive bitrate controller
/// for wireless (Wi-Fi / Hotspot) streaming sessions.
/// </summary>
public sealed class AdaptiveBitrateController
{
    public const uint DefaultMinBitrateBps = 2_000_000;
    public const double SendSlowRatioThreshold = 1.1;
    public const double SendSevereSpikeRatioThreshold = 2.0;
    public const int ConsecutiveSlowSendThreshold = 3;
    public const double RttCongestionThresholdMs = 40.0;
    public const long KeyframeWindowMs = 3_000;
    public const double DecreaseFactor = 0.75;
    public const long DecreaseCooldownMs = 1_500;
    public const uint AdditiveIncreaseBps = 500_000;
    public const long StableIncreaseIntervalMs = 4_000;
    public const uint UpdateThresholdBps = 250_000;
    private const double SendEmaAlpha = 0.25;

    private readonly object _lock = new();
    private readonly Func<long> _clockMs;
    private readonly uint _minBitrateBps;
    private readonly uint _maxBitrateBps;

    private uint _targetBitrateBps;
    private uint _lastAppliedBitrateBps;
    private double _emaSendDurationMs;
    private bool _hasSendSample;
    private int _consecutiveSlowSends;
    private double? _lastRttMs;
    private long _lastDecreaseMs = long.MinValue / 4;
    private long _lastKeyframeRequestMs = long.MinValue / 4;
    private long _stableSinceMs;

    public AdaptiveBitrateController(
        uint maxBitrateBps,
        uint minBitrateBps = DefaultMinBitrateBps,
        uint? initialBitrateBps = null,
        Func<long>? clockMs = null)
    {
        if (maxBitrateBps == 0)
            throw new ArgumentOutOfRangeException(nameof(maxBitrateBps), "Max bitrate must be positive.");

        _clockMs = clockMs ?? (() => Environment.TickCount64);
        _minBitrateBps = Math.Min(minBitrateBps, maxBitrateBps);
        _maxBitrateBps = maxBitrateBps;

        uint initial = initialBitrateBps ?? maxBitrateBps;
        _targetBitrateBps = Math.Clamp(initial, _minBitrateBps, _maxBitrateBps);
        _lastAppliedBitrateBps = _targetBitrateBps;
        _stableSinceMs = _clockMs();
    }

    public uint MinBitrateBps => _minBitrateBps;
    public uint MaxBitrateBps => _maxBitrateBps;

    public uint TargetBitrateBps
    {
        get
        {
            lock (_lock) return _targetBitrateBps;
        }
    }

    public uint LastAppliedBitrateBps
    {
        get
        {
            lock (_lock) return _lastAppliedBitrateBps;
        }
    }

    public double EmaSendDurationMs
    {
        get
        {
            lock (_lock) return _emaSendDurationMs;
        }
    }

    public double? LastRttMs
    {
        get
        {
            lock (_lock) return _lastRttMs;
        }
    }

    /// <summary>
    /// Records the wire-send duration of a video frame relative to the target frame interval.
    /// </summary>
    public void RecordSendSample(double sendDurationMs, double frameIntervalMs)
    {
        if (double.IsNaN(sendDurationMs) || double.IsInfinity(sendDurationMs) || sendDurationMs < 0)
            return;
        if (double.IsNaN(frameIntervalMs) || double.IsInfinity(frameIntervalMs) || frameIntervalMs <= 0)
            return;

        lock (_lock)
        {
            long nowMs = _clockMs();

            if (!_hasSendSample)
            {
                _emaSendDurationMs = sendDurationMs;
                _hasSendSample = true;
            }
            else
            {
                _emaSendDurationMs = (SendEmaAlpha * sendDurationMs) + ((1.0 - SendEmaAlpha) * _emaSendDurationMs);
            }

            if (sendDurationMs > frameIntervalMs * SendSevereSpikeRatioThreshold)
            {
                _consecutiveSlowSends++;
                OnCongestionLocked(nowMs);
            }
            else if (sendDurationMs > frameIntervalMs * SendSlowRatioThreshold)
            {
                _consecutiveSlowSends++;
                _stableSinceMs = nowMs;
                if (_consecutiveSlowSends >= ConsecutiveSlowSendThreshold)
                {
                    OnCongestionLocked(nowMs);
                }
            }
            else
            {
                _consecutiveSlowSends = 0;
                if (_lastRttMs is null || _lastRttMs.Value <= RttCongestionThresholdMs)
                {
                    TryAdditiveIncreaseLocked(nowMs);
                }
            }
        }
    }

    /// <summary>
    /// Records a measured network RTT sample in milliseconds.
    /// </summary>
    public void RecordRttSample(double rttMs)
    {
        if (double.IsNaN(rttMs) || double.IsInfinity(rttMs) || rttMs < 0)
            return;

        lock (_lock)
        {
            long nowMs = _clockMs();
            _lastRttMs = rttMs;

            if (rttMs > RttCongestionThresholdMs)
            {
                OnCongestionLocked(nowMs);
            }
            else if (_consecutiveSlowSends == 0)
            {
                TryAdditiveIncreaseLocked(nowMs);
            }
        }
    }

    /// <summary>
    /// Records a client keyframe request as a mild congestion signal.
    /// Triggers a multiplicative step-down if 2 or more requests arrive within 3 seconds.
    /// </summary>
    public void OnKeyframeRequested()
    {
        lock (_lock)
        {
            long nowMs = _clockMs();
            if (nowMs - _lastKeyframeRequestMs <= KeyframeWindowMs)
            {
                _lastKeyframeRequestMs = nowMs;
                OnCongestionLocked(nowMs);
            }
            else
            {
                _lastKeyframeRequestMs = nowMs;
            }
        }
    }

    /// <summary>
    /// Returns true and outputs the new target bitrate only when the target differs
    /// from the last applied bitrate by at least 250,000 bps.
    /// </summary>
    public bool TryGetBitrateUpdate(out uint newBitrateBps)
    {
        lock (_lock)
        {
            long delta = Math.Abs((long)_targetBitrateBps - _lastAppliedBitrateBps);
            if (delta >= UpdateThresholdBps)
            {
                _lastAppliedBitrateBps = _targetBitrateBps;
                newBitrateBps = _targetBitrateBps;
                return true;
            }

            newBitrateBps = _lastAppliedBitrateBps;
            return false;
        }
    }

    private void OnCongestionLocked(long nowMs)
    {
        _stableSinceMs = nowMs;

        if (nowMs - _lastDecreaseMs < DecreaseCooldownMs)
        {
            return;
        }

        _lastDecreaseMs = nowMs;
        _consecutiveSlowSends = 0;

        uint decreased = (uint)Math.Round(_targetBitrateBps * DecreaseFactor);
        _targetBitrateBps = Math.Max(_minBitrateBps, decreased);
    }

    private void TryAdditiveIncreaseLocked(long nowMs)
    {
        if (nowMs - _stableSinceMs < StableIncreaseIntervalMs)
        {
            return;
        }

        _stableSinceMs = nowMs;
        if (_targetBitrateBps < _maxBitrateBps)
        {
            ulong next = (ulong)_targetBitrateBps + AdditiveIncreaseBps;
            _targetBitrateBps = (uint)Math.Min(next, _maxBitrateBps);
        }
    }
}

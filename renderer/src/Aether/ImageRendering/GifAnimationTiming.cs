namespace Aether;

internal sealed class GifAnimationTiming
{
    private bool _paused;
    private long _nextDeadline;
    private long _pausedRemainingTicks;

    public bool IsPaused => _paused;
    public bool IsActive => !_paused && _nextDeadline > 0;
    public long NextDeadline => _nextDeadline;

    public void Start(long nowTicks, long delayTicks)
    {
        _nextDeadline = checked(nowTicks + NormalizeDelay(delayTicks));
        _pausedRemainingTicks = 0;
    }

    public bool SetPaused(bool paused, long nowTicks)
    {
        if (_paused == paused)
            return false;

        if (paused)
        {
            _pausedRemainingTicks = _nextDeadline > 0
                ? Math.Max(0, _nextDeadline - nowTicks)
                : 0;
            _paused = true;
        }
        else
        {
            _paused = false;
            if (_nextDeadline > 0)
                _nextDeadline = checked(nowTicks + NormalizeDelay(_pausedRemainingTicks));
            _pausedRemainingTicks = 0;
        }

        return true;
    }

    public void Advance(long delayTicks)
    {
        if (_nextDeadline <= 0)
            return;
        _nextDeadline = checked(_nextDeadline + NormalizeDelay(delayTicks));
    }

    public void ResetLoop(long nowTicks)
    {
        if (_nextDeadline > 0)
            _nextDeadline = nowTicks;
    }

    public void Complete()
    {
        _nextDeadline = 0;
        _pausedRemainingTicks = 0;
    }

    private static long NormalizeDelay(long delayTicks) => Math.Max(1, delayTicks);
}

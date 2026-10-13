namespace JustWatch.Core.Services;

public class FailureDetector
{
    private readonly int _threshold;
    private readonly TimeSpan _window;
    private readonly Queue<DateTime> _crashTimes = new();

    public FailureDetector(int threshold, int windowSeconds)
    {
        _threshold = threshold;
        _window = TimeSpan.FromSeconds(windowSeconds);
    }

    public int RecentCrashCount
    {
        get
        {
            Prune();
            return _crashTimes.Count;
        }
    }

    public bool RecordCrash()
    {
        _crashTimes.Enqueue(DateTime.UtcNow);
        Prune();
        return _crashTimes.Count >= _threshold;
    }

    public void Reset() => _crashTimes.Clear();

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - _window;
        while (_crashTimes.Count > 0 && _crashTimes.Peek() < cutoff)
            _crashTimes.Dequeue();
    }
}

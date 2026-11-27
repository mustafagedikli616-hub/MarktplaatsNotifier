namespace MarktplaatsNotifier;

public class RequestLimiter
{
    private const int MinDelayMs = 2000;
    private const int MaxDelayMs = 60000;

    private readonly object statsLock = new object();
    private readonly Queue<DateTime> recentRequests = new Queue<DateTime>();
    private DateOnly statsDate = DateOnly.FromDateTime(DateTime.Now);
    private DateTime calmUntil = DateTime.MinValue;

    public int DelayMs { get; private set; } = 2500;
    public DateTime PausedUntil { get; private set; } = DateTime.MinValue;
    public int BlocksToday { get; private set; }
    public int Blocks403Today { get; private set; }

    public double RequestsPerMinute
    {
        get
        {
            lock (statsLock)
            {
                PruneRequestsUnlocked();
                if (recentRequests.Count == 0)
                {
                    return 0;
                }

                double spanMinutes = Math.Max(1.0, (DateTime.Now - recentRequests.Peek()).TotalMinutes);
                return Math.Round(recentRequests.Count / Math.Min(10.0, spanMinutes), 1);
            }
        }
    }

    public void LogRequest()
    {
        lock (statsLock)
        {
            RolloverDayUnlocked();
            recentRequests.Enqueue(DateTime.Now);
            PruneRequestsUnlocked();
        }
    }

    public void LogBlock(int statusCode = 403)
    {
        lock (statsLock)
        {
            RolloverDayUnlocked();
            BlocksToday++;
            if (statusCode == 403)
            {
                Blocks403Today++;
            }
        }
    }

    // Adds ±20% jitter so requests don't fire on an exact robotic cadence
    public int NextDelayWithJitterMs()
    {
        int jitter = Random.Shared.Next(-DelayMs / 5, (DelayMs / 5) + 1);
        return Math.Max(MinDelayMs, DelayMs + jitter);
    }

    public void Success()
    {
        if (DateTime.Now < calmUntil)
        {
            return;
        }

        DelayMs = Math.Max(MinDelayMs, (int)(DelayMs * 0.97));
    }

    public void Blocked(int statusCode = 403)
    {
        LogBlock(statusCode);
        DelayMs = Math.Min(MaxDelayMs, DelayMs * 2);
        PausedUntil = DateTime.Now.AddMinutes(3);
        calmUntil = DateTime.Now.AddMinutes(15);
        BotLog.Write($"Blocked ({statusCode}): pausing 3 min, then {DelayMs} ms between requests ({Blocks403Today}x 403 today)");
    }

    private void PruneRequestsUnlocked()
    {
        DateTime cutoff = DateTime.Now.AddMinutes(-10);
        while (recentRequests.Count > 0 && recentRequests.Peek() < cutoff)
        {
            recentRequests.Dequeue();
        }
    }

    private void RolloverDayUnlocked()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        if (today != statsDate)
        {
            statsDate = today;
            BlocksToday = 0;
            Blocks403Today = 0;
        }
    }
}

namespace MarktplaatsNotifier;

public class RequestLimiter
{
    private const int MinDelayMs = 2000;
    private const int MaxDelayMs = 60000;

    public int DelayMs { get; private set; } = 2500;

    // Adds ±20% jitter so requests don't fire on an exact robotic cadence
    public int NextDelayWithJitterMs()
    {
        int jitter = Random.Shared.Next(-DelayMs / 5, (DelayMs / 5) + 1);
        return Math.Max(MinDelayMs, DelayMs + jitter);
    }

    public void Success()
    {
        DelayMs = Math.Max(MinDelayMs, (int)(DelayMs * 0.97));
    }

    public void Blocked()
    {
        DelayMs = Math.Min(MaxDelayMs, DelayMs * 2);
        BotLog.Write($"Slowing down: now {DelayMs} ms between requests");
    }
}

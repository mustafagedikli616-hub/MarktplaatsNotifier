namespace MarktplaatsNotifier;

public class RequestLimiter
{
    private const int MinDelayMs = 1500;
    private const int MaxDelayMs = 60000;

    public int DelayMs { get; private set; } = 2000;
    
    public void Success()
    {
        DelayMs = Math.Max(MinDelayMs, (int)(DelayMs * 0.97));
    }
    
    public void Blocked()
    {
        DelayMs = Math.Min(MaxDelayMs, DelayMs * 2);
        Console.WriteLine($"Slowing down: now {DelayMs} ms between requests");
    }
    
}
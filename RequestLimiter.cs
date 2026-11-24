namespace MarktplaatsNotifier;

public class RequestLimiter
{
    private const int MinDelayMs = 3000;
    private const int MaxDelayMs = 60000;

    private Random random = new Random();
    private DateTime calmUntil = DateTime.MinValue;
    private int successesSinceSave = 0;

    public int DelayMs { get; private set; } = 3000;

   
    public DateTime PausedUntil { get; private set; } = DateTime.MinValue;

    
    public int NextWaitMs()
    {
        double factor = 0.7 + random.NextDouble() * 0.6;
        return (int)(DelayMs * factor);
    }

    public void Success()
    {
       
        if (DateTime.Now < calmUntil)
        {
            return;
        }

        DelayMs = Math.Max(MinDelayMs, (int)(DelayMs * 0.97));
        successesSinceSave++;
        if (successesSinceSave >= 25)
        {
            Save();
        }
    }

    
    public void Blocked()
    {
        DelayMs = Math.Min(MaxDelayMs, DelayMs * 2);
        PausedUntil = DateTime.Now.AddMinutes(3);
        calmUntil = DateTime.Now.AddMinutes(15);
        Console.WriteLine($"Blocked: pausing 3 minutes, then {DelayMs} ms between requests");
        Save();
        
    }
    public void Load()
    {
        if (File.Exists("limiter.txt") && int.TryParse(File.ReadAllText("limiter.txt").Trim(), out int saved))
        {
            DelayMs = Math.Clamp(saved, MinDelayMs, MaxDelayMs);
            Console.WriteLine($"Starting with the learned speed: {DelayMs} ms between requests");
        }
    }

    public void Save()
    {
        successesSinceSave = 0;
        File.WriteAllText("limiter.txt", DelayMs.ToString());
    }
}
using System.Text;

namespace MarktplaatsNotifier;

public static class BotLog
{
    private const int MaxLines = 60;
    private static readonly Queue<string> RecentLines = new Queue<string>();
    private static readonly object LogLock = new object();

    public static event Action? OnWrite;

    public static void Write(string message)
    {
        Console.WriteLine(message);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        string entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (LogLock)
        {
            RecentLines.Enqueue(entry);
            while (RecentLines.Count > MaxLines)
            {
                RecentLines.Dequeue();
            }
        }

        try { OnWrite?.Invoke(); } catch { }
    }

    public static List<string> GetRecentLines(int count = 25)
    {
        lock (LogLock)
        {
            List<string> snapshot = RecentLines.ToList();
            int skip = Math.Max(0, snapshot.Count - count);
            return snapshot.Skip(skip).ToList();
        }
    }

    public static string GetRecent(int count = 25)
    {
        List<string> snapshot;
        lock (LogLock)
        {
            snapshot = RecentLines.ToList();
        }

        if (snapshot.Count == 0)
        {
            return "📜 No logs recorded yet.";
        }

        int skip = Math.Max(0, snapshot.Count - count);
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"📜 Last {snapshot.Count - skip} log lines:\n");
        for (int i = skip; i < snapshot.Count; i++)
        {
            sb.AppendLine(snapshot[i]);
        }

        return sb.ToString().TrimEnd();
    }
}

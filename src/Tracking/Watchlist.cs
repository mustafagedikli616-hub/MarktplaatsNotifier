using System.Text.Json;

namespace MarktplaatsNotifier;

public class WatchItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Link { get; set; } = "";
    public double LastPrice { get; set; }
    public double HighestBid { get; set; }
    public int BidCount { get; set; }
    public double TargetPrice { get; set; }
    public string Status { get; set; } = "ACTIVE";
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public DateTime LastCheckedAt { get; set; } = DateTime.MinValue;
}

public class Watchlist
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

    private readonly string filePath;
    private readonly object syncLock = new object();
    private readonly Dictionary<string, WatchItem> items =
        new Dictionary<string, WatchItem>(StringComparer.OrdinalIgnoreCase);

    public List<WatchItem> Items
    {
        get { lock (syncLock) { return items.Values.ToList(); } }
    }

    public int Count
    {
        get { lock (syncLock) { return items.Count; } }
    }

    public Watchlist(string? dataDir = null)
    {
        string dir = dataDir ?? AppFiles.DataDirectory;
        filePath = Path.Combine(dir, "watchlist.json");
    }

    public void Load()
    {
        lock (syncLock)
        {
            items.Clear();
            if (!File.Exists(filePath))
            {
                return;
            }

            try
            {
                List<WatchItem>? loaded = JsonSerializer.Deserialize<List<WatchItem>>(File.ReadAllText(filePath));
                if (loaded != null)
                {
                    foreach (WatchItem item in loaded)
                    {
                        if (!string.IsNullOrWhiteSpace(item.Id))
                        {
                            items[item.Id] = item;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                BotLog.Write($"Couldn't read watchlist.json ({ex.Message}), starting empty.");
            }
        }
    }

    public void Save()
    {
        lock (syncLock)
        {
            SaveUnlocked();
        }
    }

    private void SaveUnlocked()
    {
        string tempPath = filePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(items.Values.ToList(), JsonOptions));
        File.Move(tempPath, filePath, overwrite: true);
    }

    public bool Contains(string itemId)
    {
        lock (syncLock) { return items.ContainsKey(itemId); }
    }

    public WatchItem? Get(string itemId)
    {
        lock (syncLock) { return items.GetValueOrDefault(itemId); }
    }

    public WatchItem AddOrUpdate(WatchItem item)
    {
        lock (syncLock)
        {
            items[item.Id] = item;
            SaveUnlocked();
            return item;
        }
    }

    public bool Remove(string itemId)
    {
        lock (syncLock)
        {
            if (items.Remove(itemId))
            {
                SaveUnlocked();
                return true;
            }

            return false;
        }
    }

    public WatchItem? NextDue(TimeSpan interval)
    {
        lock (syncLock)
        {
            WatchItem? oldest = null;
            DateTime cutoff = DateTime.Now - interval;
            foreach (WatchItem item in items.Values)
            {
                if (item.LastCheckedAt > cutoff)
                {
                    continue;
                }

                if (oldest == null || item.LastCheckedAt < oldest.LastCheckedAt)
                {
                    oldest = item;
                }
            }

            return oldest;
        }
    }
}

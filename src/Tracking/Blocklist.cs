using System.Text.Json;

namespace MarktplaatsNotifier;

public class BlocklistData
{
    public List<string> GlobalSkipWords { get; set; } = new List<string>();
    public List<string> BlockedSellers { get; set; } = new List<string>();
}

public class Blocklist
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

    private readonly string filePath;
    private readonly object syncLock = new object();
    private BlocklistData data = new BlocklistData();

    public List<string> GlobalSkipWords
    {
        get { lock (syncLock) { return data.GlobalSkipWords.ToList(); } }
    }

    public List<string> BlockedSellers
    {
        get { lock (syncLock) { return data.BlockedSellers.ToList(); } }
    }

    public Blocklist(string? dataDir = null)
    {
        string dir = dataDir ?? AppFiles.DataDirectory;
        filePath = Path.Combine(dir, "blocklist.json");
    }

    public void Load()
    {
        lock (syncLock)
        {
            if (!File.Exists(filePath))
            {
                data = new BlocklistData();
                return;
            }

            try
            {
                BlocklistData? loaded = JsonSerializer.Deserialize<BlocklistData>(File.ReadAllText(filePath));
                if (loaded != null)
                {
                    data = loaded;
                }
            }
            catch (Exception ex)
            {
                BotLog.Write($"Couldn't read blocklist.json ({ex.Message}), starting empty.");
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
        File.WriteAllText(tempPath, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(tempPath, filePath, overwrite: true);
    }

    public bool IsSellerBlocked(string sellerName)
    {
        if (string.IsNullOrWhiteSpace(sellerName))
        {
            return false;
        }

        string target = sellerName.Trim();
        lock (syncLock)
        {
            foreach (string blocked in data.BlockedSellers)
            {
                if (string.Equals(blocked, target, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public bool AddSeller(string sellerName)
    {
        string clean = sellerName.Trim();
        if (clean == "")
        {
            return false;
        }

        lock (syncLock)
        {
            if (data.BlockedSellers.Any(s => string.Equals(s, clean, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            data.BlockedSellers.Add(clean);
            SaveUnlocked();
            return true;
        }
    }

    public bool RemoveSeller(string sellerName)
    {
        string clean = sellerName.Trim();
        lock (syncLock)
        {
            int removed = data.BlockedSellers.RemoveAll(s => string.Equals(s, clean, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveUnlocked();
                return true;
            }

            return false;
        }
    }

    public int AddGlobalSkipWords(IEnumerable<string> words)
    {
        lock (syncLock)
        {
            int added = 0;
            foreach (string raw in words)
            {
                string clean = raw.Trim().ToLowerInvariant();
                if (clean == "")
                {
                    continue;
                }

                if (!data.GlobalSkipWords.Contains(clean, StringComparer.OrdinalIgnoreCase))
                {
                    data.GlobalSkipWords.Add(clean);
                    added++;
                }
            }

            if (added > 0)
            {
                SaveUnlocked();
            }

            return added;
        }
    }

    public bool RemoveGlobalSkipWord(string word)
    {
        string clean = word.Trim().ToLowerInvariant();
        lock (syncLock)
        {
            int removed = data.GlobalSkipWords.RemoveAll(w => string.Equals(w, clean, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveUnlocked();
                return true;
            }

            return false;
        }
    }

    public string[] GetGlobalSkipWordsArray()
    {
        lock (syncLock)
        {
            return data.GlobalSkipWords.ToArray();
        }
    }
}

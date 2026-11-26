using System.Text;
using System.Text.Json;

namespace MarktplaatsNotifier;

public class SearchStatEntry
{
    public string Query { get; set; } = "";
    public int Checks { get; set; }
    public int FoundNew { get; set; }
    public int Sent { get; set; }
    public int FilteredOut { get; set; }
    public double TotalSentPrice { get; set; }
    public int SentWithPriceCount { get; set; }
    public DateTime LastMatchAt { get; set; } = DateTime.MinValue;
}

public class SearchStats
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

    private readonly string filePath;
    private readonly object syncLock = new object();
    private readonly Dictionary<string, SearchStatEntry> stats =
        new Dictionary<string, SearchStatEntry>(StringComparer.OrdinalIgnoreCase);
    private DateTime lastSave = DateTime.Now;

    public SearchStats(string? dataDir = null)
    {
        string dir = dataDir ?? AppFiles.DataDirectory;
        filePath = Path.Combine(dir, "stats.json");
    }

    public void Load()
    {
        lock (syncLock)
        {
            stats.Clear();
            if (!File.Exists(filePath))
            {
                return;
            }

            try
            {
                List<SearchStatEntry>? loaded = JsonSerializer.Deserialize<List<SearchStatEntry>>(File.ReadAllText(filePath));
                if (loaded != null)
                {
                    foreach (SearchStatEntry entry in loaded)
                    {
                        if (!string.IsNullOrWhiteSpace(entry.Query))
                        {
                            stats[entry.Query] = entry;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                BotLog.Write($"Couldn't read stats.json ({ex.Message}), starting fresh.");
            }
        }
    }

    public void Save()
    {
        lock (syncLock)
        {
            string tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(stats.Values.ToList(), JsonOptions));
            File.Move(tempPath, filePath, overwrite: true);
            lastSave = DateTime.Now;
        }
    }

    public void SaveIfNeeded()
    {
        if (DateTime.Now - lastSave >= TimeSpan.FromMinutes(10))
        {
            Save();
        }
    }

    public void RecordCheck(string query)
    {
        lock (syncLock) { GetOrCreateUnlocked(query).Checks++; }
    }

    public void RecordNew(string query)
    {
        lock (syncLock)
        {
            SearchStatEntry entry = GetOrCreateUnlocked(query);
            entry.FoundNew++;
            entry.LastMatchAt = DateTime.Now;
        }
    }

    public void RecordSent(string query, double price)
    {
        lock (syncLock)
        {
            SearchStatEntry entry = GetOrCreateUnlocked(query);
            entry.Sent++;
            entry.LastMatchAt = DateTime.Now;
            if (price > 0)
            {
                entry.TotalSentPrice += price;
                entry.SentWithPriceCount++;
            }
        }
    }

    public void RecordFiltered(string query)
    {
        lock (syncLock) { GetOrCreateUnlocked(query).FilteredOut++; }
    }

    public void Remove(string query)
    {
        lock (syncLock) { stats.Remove(query); }
    }

    public SearchStatEntry? GetFor(string query)
    {
        lock (syncLock) { return stats.GetValueOrDefault(query); }
    }

    public string BuildReport(IEnumerable<string> activeSearches)
    {
        HashSet<string> active = new HashSet<string>(activeSearches, StringComparer.OrdinalIgnoreCase);
        List<SearchStatEntry> activeStats;
        lock (syncLock)
        {
            activeStats = active
                .Select(q => stats.TryGetValue(q, out SearchStatEntry? e) ? e : new SearchStatEntry { Query = q })
                .ToList();
        }

        if (activeStats.Count == 0)
        {
            return "📊 No searches yet.";
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"📊 Search stats ({activeStats.Count} searches)\n");

        List<SearchStatEntry> top = activeStats
            .Where(s => s.Sent > 0)
            .OrderByDescending(s => s.Sent)
            .Take(8)
            .ToList();

        if (top.Count > 0)
        {
            sb.AppendLine("🏆 Most sent:");
            foreach (SearchStatEntry s in top)
            {
                string avg = s.SentWithPriceCount > 0
                    ? $" · avg {Listing.FormatEuro(s.TotalSentPrice / s.SentWithPriceCount)}"
                    : "";
                sb.AppendLine($"• {s.Query}: {s.Sent} sent{avg}");
            }
            sb.AppendLine();
        }

        List<SearchStatEntry> noisy = activeStats
            .Where(s => s.FilteredOut >= 5 && s.FilteredOut > s.Sent * 3)
            .OrderByDescending(s => s.FilteredOut)
            .Take(6)
            .ToList();

        if (noisy.Count > 0)
        {
            sb.AppendLine("📢 Most skipped:");
            foreach (SearchStatEntry s in noisy)
            {
                sb.AppendLine($"• {s.Query}: {s.FilteredOut} skipped vs {s.Sent} sent");
            }
            sb.AppendLine();
        }

        DateTime deadCutoff = DateTime.Now.AddDays(-14);
        List<SearchStatEntry> dead = activeStats
            .Where(s => s.Checks >= 5 && (s.LastMatchAt == DateTime.MinValue || s.LastMatchAt < deadCutoff))
            .OrderByDescending(s => s.Checks)
            .Take(8)
            .ToList();

        if (dead.Count > 0)
        {
            sb.AppendLine("💤 No matches in 14+ days:");
            foreach (SearchStatEntry s in dead)
            {
                sb.AppendLine($"• {s.Query} ({s.Checks} checks, 0 recent)");
            }
        }
        else if (top.Count == 0 && noisy.Count == 0)
        {
            sb.AppendLine("Not enough data yet. Check back after the bot runs for a bit.");
        }

        return sb.ToString().TrimEnd();
    }

    private SearchStatEntry GetOrCreateUnlocked(string query)
    {
        if (!stats.TryGetValue(query, out SearchStatEntry? entry))
        {
            entry = new SearchStatEntry { Query = query };
            stats[query] = entry;
        }

        return entry;
    }
}

using System.Globalization;

namespace MarktplaatsNotifier;

// One listing the bot has seen in the search results
public class HistoryEntry
{
    public string Id { get; set; } = "";
    public string Search { get; set; } = "";
    public int Category { get; set; } = 0;
    public double Price { get; set; } = 0;
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public string Title { get; set; } = "";
    public string Seller { get; set; } = "";
    public string PhotoHash { get; set; } = "";

    // In-memory tracking for pending price drops so a drop rejected by one search
    // (e.g. still above its max price) isn't lost for another overlapping search.
    public double PendingDropFrom { get; set; } = 0;
    public HashSet<string> CheckedDropSearches { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

// Remembers every listing the bot sees (for 30 days), saved in history.txt.
// Used for: price drops, typical price, repost detection, and stale listing sniping.
public class ListingHistory
{
    private const int KeepDays = 30;
    private const int MinListingsForTypical = 8;

    private readonly string filePath;
    private readonly Dictionary<string, HistoryEntry> entries = new Dictionary<string, HistoryEntry>();
    private DateTime lastSave = DateTime.Now;
    private readonly object listLock = new object();

    public ListingHistory(string? dataDir = null)
    {
        string dir = dataDir ?? AppFiles.DataDirectory;
        filePath = Path.Combine(dir, "history.txt");
    }

    public int Count
    {
        get
        {
            lock (listLock)
            {
                return entries.Count;
            }
        }
    }

    public HistoryEntry? GetById(string itemId)
    {
        lock (listLock)
        {
            return entries.GetValueOrDefault(itemId);
        }
    }

    public void Load()
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        lock (listLock)
        {
            entries.Clear();
            foreach (string line in File.ReadAllLines(filePath))
            {
                // id;search;category;price;first seen;last seen;seller;title
                string[] parts = line.Split(';');
                if (parts.Length < 8)
                {
                    continue;
                }

                try
                {
                    HistoryEntry entry = new HistoryEntry
                    {
                        Id = parts[0],
                        Search = parts[1],
                        Category = int.Parse(parts[2], CultureInfo.InvariantCulture),
                        Price = double.Parse(parts[3], CultureInfo.InvariantCulture),
                        FirstSeen = DateTime.Parse(parts[4], CultureInfo.InvariantCulture),
                        LastSeen = DateTime.Parse(parts[5], CultureInfo.InvariantCulture),
                        Seller = parts[6],
                        Title = parts[7],
                        PhotoHash = parts.Length > 8 ? parts[8] : ""
                    };
                    entries[entry.Id] = entry;
                }
                catch
                {
                }
            }

            BotLog.Write($"History: remembered {entries.Count} listings.");
        }
    }

    public double Remember(Listing ad, string search)
    {
        lock (listLock)
        {
            return RememberUnlocked(ad, search);
        }
    }

    private double RememberUnlocked(Listing ad, string search)
    {
        if (entries.TryGetValue(ad.Id, out HistoryEntry? entry))
        {
            entry.LastSeen = DateTime.Now;

            if (ad.Price > 0)
            {
                if (entry.Price > 0 && ad.Price < entry.Price)
                {
                    if (entry.PendingDropFrom <= 0)
                    {
                        entry.PendingDropFrom = entry.Price;
                    }

                    entry.Price = ad.Price;
                    entry.CheckedDropSearches.Clear();
                }
                else if (ad.Price > entry.Price)
                {
                    entry.Price = ad.Price;
                    entry.PendingDropFrom = 0;
                    entry.CheckedDropSearches.Clear();
                }
            }

            if (entry.PendingDropFrom > 0 && ad.Price > 0 && ad.Price < entry.PendingDropFrom &&
                !entry.CheckedDropSearches.Contains(search))
            {
                return entry.PendingDropFrom;
            }

            return 0;
        }

        HistoryEntry newEntry = new HistoryEntry
        {
            Id = ad.Id,
            Search = Clean(search),
            Category = ad.CategoryId,
            Price = ad.Price,
            FirstSeen = DateTime.Now,
            LastSeen = DateTime.Now,
            Seller = Clean(ad.SellerName),
            Title = Clean(ad.Title)
        };
        entries[ad.Id] = newEntry;
        return 0;
    }

    public void MarkPriceDropChecked(string itemId, string search)
    {
        lock (listLock)
        {
            if (entries.TryGetValue(itemId, out HistoryEntry? entry))
            {
                entry.CheckedDropSearches.Add(search);
            }
        }
    }

    public void MarkPriceAlerted(string itemId)
    {
        lock (listLock)
        {
            if (entries.TryGetValue(itemId, out HistoryEntry? entry))
            {
                entry.PendingDropFrom = 0;
                entry.CheckedDropSearches.Clear();
            }
        }
    }

    public double TypicalPrice(string search, int category, out int count)
    {
        List<double> prices = new List<double>();
        lock (listLock)
        {
            foreach (HistoryEntry entry in entries.Values)
            {
                if (string.Equals(entry.Search, search, StringComparison.OrdinalIgnoreCase) &&
                    entry.Category == category &&
                    entry.Price > 0)
                {
                    prices.Add(entry.Price);
                }
            }
        }

        count = prices.Count;
        if (count < MinListingsForTypical)
        {
            return 0;
        }

        prices.Sort();
        return prices[count / 2];
    }

    public void SetPhotoHash(string itemId, string photoHash)
    {
        if (string.IsNullOrEmpty(photoHash))
        {
            return;
        }

        lock (listLock)
        {
            if (entries.TryGetValue(itemId, out HistoryEntry? entry))
            {
                entry.PhotoHash = photoHash;
            }
        }
    }

    public List<double> GetPricesForQuery(string query)
    {
        List<double> prices = new List<double>();
        string lower = query.Trim().ToLowerInvariant();
        if (lower == "")
        {
            return prices;
        }

        lock (listLock)
        {
            foreach (HistoryEntry entry in entries.Values)
            {
                if (entry.Price > 0 &&
                    (entry.Search.Equals(lower, StringComparison.OrdinalIgnoreCase) ||
                     entry.Title.ToLowerInvariant().Contains(lower, StringComparison.Ordinal)))
                {
                    prices.Add(entry.Price);
                }
            }
        }

        return prices;
    }

    public HistoryEntry? FindRepost(Listing ad, out bool matchedByPhoto)
    {
        matchedByPhoto = false;
        string title = Clean(ad.Title).ToLowerInvariant();
        string seller = Clean(ad.SellerName).ToLowerInvariant();
        string photoHash = ad.PhotoHash;

        HistoryEntry? oldest = null;
        bool oldestByPhoto = false;

        lock (listLock)
        {
            foreach (HistoryEntry entry in entries.Values)
            {
                if (entry.Id == ad.Id)
                {
                    continue;
                }

                if ((DateTime.Now - entry.FirstSeen).TotalDays < 1)
                {
                    continue;
                }

                bool exactTitleSeller = title != "" && seller != "" &&
                                        entry.Title.ToLowerInvariant() == title &&
                                        entry.Seller.ToLowerInvariant() == seller;

                bool photoMatch = !exactTitleSeller &&
                                  photoHash != "" &&
                                  entry.PhotoHash != "" &&
                                  (ad.CategoryId == 0 || entry.Category == 0 || entry.Category == ad.CategoryId) &&
                                  ImageHasher.AreSimilar(photoHash, entry.PhotoHash);

                if (!exactTitleSeller && !photoMatch)
                {
                    continue;
                }

                if (oldest == null || entry.FirstSeen < oldest.FirstSeen)
                {
                    oldest = entry;
                    oldestByPhoto = photoMatch;
                }
            }
        }

        matchedByPhoto = oldestByPhoto;
        return oldest;
    }

    public HistoryEntry? FindRepost(Listing ad) => FindRepost(ad, out _);

    // Finds still-active listings sitting unsold for at least minDays, slightly above maxPrice (prime negotiation targets).
    public List<(HistoryEntry Entry, int MaxPrice, int DaysOld)> FindStaleListings(
        SearchList searchList, Blocklist blocklist, int minDays = 7, double maxOverBudgetRatio = 1.35)
    {
        List<(HistoryEntry Entry, int MaxPrice, int DaysOld)> results = new List<(HistoryEntry, int, int)>();
        DateTime now = DateTime.Now;
        string[] globalSkip = blocklist.GetGlobalSkipWordsArray();

        lock (listLock)
        {
            foreach (HistoryEntry entry in entries.Values)
            {
                if (entry.Price <= 0)
                {
                    continue;
                }

                if ((now - entry.LastSeen).TotalDays > 3)
                {
                    continue;
                }

                int daysOld = (int)(now - entry.FirstSeen).TotalDays;
                if (daysOld < minDays)
                {
                    continue;
                }

                if (!searchList.Rules.TryGetValue(entry.Search, out SearchRule? rule))
                {
                    continue;
                }

                if (rule.MaxPrice <= 0 || entry.Price <= rule.MaxPrice || entry.Price > rule.MaxPrice * maxOverBudgetRatio)
                {
                    continue;
                }

                if (blocklist.IsSellerBlocked(entry.Seller))
                {
                    continue;
                }

                if (Filters.IsNotForSale(entry.Title) ||
                    Filters.FindBadFault(entry.Title, rule.SkipWords) != "" ||
                    Filters.FindBadFault(entry.Title, globalSkip) != "")
                {
                    continue;
                }

                results.Add((entry, rule.MaxPrice, daysOld));
            }
        }

        return results
            .OrderByDescending(x => x.DaysOld)
            .ThenBy(x => x.Entry.Price / x.MaxPrice)
            .Take(12)
            .ToList();
    }

    public void SaveIfNeeded()
    {
        if (DateTime.Now - lastSave < TimeSpan.FromMinutes(10))
        {
            return;
        }

        Save();
    }

    public void Save()
    {
        lock (listLock)
        {
            SaveUnlocked();
        }
    }

    private void SaveUnlocked()
    {
        List<string> lines = new List<string>();
        List<string> tooOld = new List<string>();

        foreach (HistoryEntry entry in entries.Values)
        {
            if ((DateTime.Now - entry.LastSeen).TotalDays > KeepDays)
            {
                tooOld.Add(entry.Id);
                continue;
            }

            lines.Add(string.Join(";",
                Clean(entry.Id),
                Clean(entry.Search),
                entry.Category.ToString(CultureInfo.InvariantCulture),
                entry.Price.ToString(CultureInfo.InvariantCulture),
                entry.FirstSeen.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                entry.LastSeen.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                Clean(entry.Seller),
                Clean(entry.Title),
                Clean(entry.PhotoHash)));
        }

        foreach (string id in tooOld)
        {
            entries.Remove(id);
        }

        string tempPath = filePath + ".tmp";
        File.WriteAllLines(tempPath, lines);
        File.Move(tempPath, filePath, overwrite: true);
        lastSave = DateTime.Now;
    }

    private static string Clean(string text)
    {
        return text.Replace(";", ",").Replace("\n", " ").Replace("\r", " ").Trim();
    }
}

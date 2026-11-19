using System.Globalization;

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
}

// Remembers every listing the bot sees (for 30 days), saved in history.txt.
// Used for: price drops, the typical price of a search, and spotting reposted listings.
public class ListingHistory
{
    private const int KeepDays = 30;
    private const int MinListingsForTypical = 8;

    private Dictionary<string, HistoryEntry> entries = new Dictionary<string, HistoryEntry>();
    private DateTime lastSave = DateTime.Now;

    // Save() can be called from another thread when the bot is stopped,
    // so only one piece of code may touch the list at a time.
    private object listLock = new object();

    public void Load()
    {
        if (!File.Exists("history.txt"))
        {
            return;
        }

        foreach (string line in File.ReadAllLines("history.txt"))
        {
            // id;search;category;price;first seen;last seen;seller;title
            string[] parts = line.Split(';');
            if (parts.Length < 8)
            {
                continue;
            }

            try
            {
                HistoryEntry entry = new HistoryEntry();
                entry.Id = parts[0];
                entry.Search = parts[1];
                entry.Category = int.Parse(parts[2]);
                entry.Price = double.Parse(parts[3], CultureInfo.InvariantCulture);
                entry.FirstSeen = DateTime.Parse(parts[4], CultureInfo.InvariantCulture);
                entry.LastSeen = DateTime.Parse(parts[5], CultureInfo.InvariantCulture);
                entry.Seller = parts[6];
                entry.Title = parts[7];
                entries[entry.Id] = entry;
            }
            catch
            {
                // a broken line: skip it
            }
        }

        Console.WriteLine($"History: remembered {entries.Count} listings.");
    }

    // Remembers a listing. Gives back the price from last time, or 0 if we've never seen it.
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
            double oldPrice = entry.Price;
            entry.Price = ad.Price;
            entry.LastSeen = DateTime.Now;
            return oldPrice;
        }

        HistoryEntry newEntry = new HistoryEntry();
        newEntry.Id = ad.Id;
        newEntry.Search = search;
        newEntry.Category = ad.CategoryId;
        newEntry.Price = ad.Price;
        newEntry.FirstSeen = DateTime.Now;
        newEntry.LastSeen = DateTime.Now;
        newEntry.Seller = Clean(ad.SellerName);
        newEntry.Title = Clean(ad.Title);
        entries[ad.Id] = newEntry;
        return 0;
    }

    // The typical (middle) price of listings from this search AND this category, or 0 if we haven't seen enough yet.
    // The category matters: a "ps5" search also finds games and controllers, and those shouldn't count for a console.
    public double TypicalPrice(string search, int category, out int count)
    {
        List<double> prices = new List<double>();
        foreach (HistoryEntry entry in entries.Values)
        {
            if (entry.Search == search && entry.Category == category && entry.Price > 0)
            {
                prices.Add(entry.Price);
            }
        }

        count = prices.Count;
        if (count < MinListingsForTypical)
        {
            return 0;
        }

        // The median: sort the prices and take the one in the middle.
        // Better than the average, because one silly €1 or €999 listing doesn't mess it up.
        prices.Sort();
        return prices[count / 2];
    }

    // Looks for the same item posted before by the same seller (a different listing ID)
    public HistoryEntry? FindRepost(Listing ad)
    {
        string title = Clean(ad.Title).ToLower();
        string seller = Clean(ad.SellerName).ToLower();
        HistoryEntry? oldest = null;

        foreach (HistoryEntry entry in entries.Values)
        {
            if (entry.Id == ad.Id || entry.Title.ToLower() != title || entry.Seller.ToLower() != seller)
            {
                continue;
            }

            // only count it if it was posted at least a day earlier
            if ((DateTime.Now - entry.FirstSeen).TotalDays < 1)
            {
                continue;
            }

            if (oldest == null || entry.FirstSeen < oldest.FirstSeen)
            {
                oldest = entry;
            }
        }

        return oldest;
    }

    // Saves at most every 10 minutes, so the disk isn't written all the time
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
                entry.Id,
                entry.Search,
                entry.Category.ToString(),
                entry.Price.ToString(CultureInfo.InvariantCulture),
                entry.FirstSeen.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                entry.LastSeen.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                entry.Seller,
                entry.Title));
        }

        foreach (string id in tooOld)
        {
            entries.Remove(id);
        }

        File.WriteAllLines("history.txt", lines);
        lastSave = DateTime.Now;
    }

    // ; and new lines would break the file, so swap them for safe characters
    private static string Clean(string text)
    {
        return text.Replace(";", ",").Replace("\n", " ").Replace("\r", " ").Trim();
    }
}
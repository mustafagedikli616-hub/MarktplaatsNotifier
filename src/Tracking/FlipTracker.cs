using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MarktplaatsNotifier;

public class FlipItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public double BuyPrice { get; set; }
    public double PartsCost { get; set; }
    public double? SellPrice { get; set; }
    public DateTime BoughtAt { get; set; } = DateTime.Now;
    public DateTime? SoldAt { get; set; }

    public double TotalCost => BuyPrice + PartsCost;
    public double? Profit => SellPrice.HasValue ? SellPrice.Value - TotalCost : null;
}

public class FlipTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

    private readonly string filePath;
    private readonly List<FlipItem> items = new List<FlipItem>();
    private int nextId = 1;

    public IReadOnlyList<FlipItem> OpenItems => items.Where(i => !i.SellPrice.HasValue).ToList();
    public IReadOnlyList<FlipItem> AllItems => items.ToList();

    public FlipTracker(string? dataDir = null)
    {
        string dir = dataDir ?? AppFiles.DataDirectory;
        filePath = Path.Combine(dir, "flips.json");
    }

    public void Load()
    {
        items.Clear();
        nextId = 1;
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            List<FlipItem>? loaded = JsonSerializer.Deserialize<List<FlipItem>>(File.ReadAllText(filePath));
            if (loaded != null)
            {
                items.AddRange(loaded);
                foreach (FlipItem item in items)
                {
                    if (item.Id >= nextId)
                    {
                        nextId = item.Id + 1;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            BotLog.Write($"Couldn't read flips.json ({ex.Message}), starting empty.");
        }
    }

    public void Save()
    {
        string tempPath = filePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(items, JsonOptions));
        File.Move(tempPath, filePath, overwrite: true);
    }

    public FlipItem? GetById(int id) => items.FirstOrDefault(i => i.Id == id);

    public FlipItem AddPurchase(string name, double buyPrice)
    {
        FlipItem item = new FlipItem
        {
            Id = nextId++,
            Name = name.Trim(),
            BuyPrice = buyPrice,
            BoughtAt = DateTime.Now
        };
        items.Add(item);
        Save();
        return item;
    }

    public void Restore(FlipItem item)
    {
        if (items.Any(i => i.Id == item.Id))
        {
            item.Id = nextId++;
        }
        else if (item.Id >= nextId)
        {
            nextId = item.Id + 1;
        }

        items.Add(item);
        Save();
    }

    public FlipItem? Delete(int id)
    {
        FlipItem? existing = GetById(id);
        if (existing == null)
        {
            return null;
        }

        items.Remove(existing);
        Save();
        return existing;
    }

    public FlipItem? Find(string query, bool preferUnsold = true)
    {
        string clean = query.Trim().TrimStart('#');
        if (int.TryParse(clean, out int id))
        {
            return items.FirstOrDefault(i => i.Id == id);
        }

        IEnumerable<FlipItem> ordered = preferUnsold
            ? items.OrderBy(i => i.SellPrice.HasValue ? 1 : 0).ThenByDescending(i => i.Id)
            : items.OrderByDescending(i => i.Id);

        FlipItem? exact = ordered.FirstOrDefault(i => i.Name.Equals(clean, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        return ordered.FirstOrDefault(i => i.Name.Contains(clean, StringComparison.OrdinalIgnoreCase));
    }

    public FlipItem? AddParts(string query, double partsCost)
    {
        FlipItem? item = Find(query, preferUnsold: true);
        if (item == null)
        {
            return null;
        }

        item.PartsCost += partsCost;
        Save();
        return item;
    }

    public FlipItem? MarkSold(string query, double sellPrice)
    {
        FlipItem? item = Find(query, preferUnsold: true);
        if (item == null)
        {
            return null;
        }

        item.SellPrice = sellPrice;
        item.SoldAt = DateTime.Now;
        Save();
        return item;
    }

    public string BuildProfitSummary(bool dutch = false)
    {
        DateTime now = DateTime.Now;
        List<FlipItem> soldAll = items.Where(i => i.SellPrice.HasValue).ToList();
        List<FlipItem> soldMonth = soldAll
            .Where(i => i.SoldAt.HasValue && i.SoldAt.Value.Year == now.Year && i.SoldAt.Value.Month == now.Month)
            .ToList();
        List<FlipItem> open = items.Where(i => !i.SellPrice.HasValue).ToList();

        double monthProfit = soldMonth.Sum(i => i.Profit ?? 0);
        double allProfit = soldAll.Sum(i => i.Profit ?? 0);
        double allCost = soldAll.Sum(i => i.TotalCost);
        double inventoryCost = open.Sum(i => i.TotalCost);

        StringBuilder sb = new StringBuilder();
        sb.AppendLine(dutch ? "💰 <b>Flips & Winstoverzicht</b>\n" : "💰 <b>Flip & Profit Summary</b>\n");
        sb.AppendLine(dutch
            ? $"📅 Deze maand: <b>{FormatSignedEuro(monthProfit)}</b> ({soldMonth.Count} verkocht)"
            : $"📅 This month ({now.ToString("MMM yyyy", CultureInfo.InvariantCulture)}): <b>{FormatSignedEuro(monthProfit)}</b> ({soldMonth.Count} sold)");
        string roiText = allCost > 0 ? $" ({Math.Round(allProfit / allCost * 100)}% ROI)" : "";
        sb.AppendLine(dutch
            ? $"🏆 Totale winst: <b>{FormatSignedEuro(allProfit)}{roiText}</b> ({soldAll.Count} verkocht)"
            : $"🏆 All-time profit: <b>{FormatSignedEuro(allProfit)}{roiText}</b> ({soldAll.Count} sold)");
        sb.AppendLine(dutch
            ? $"📦 Op voorraad: <b>{open.Count}</b> item(s) ({Listing.FormatEuro(inventoryCost)} geïnvesteerd)"
            : $"📦 Open inventory: <b>{open.Count}</b> item(s) ({Listing.FormatEuro(inventoryCost)} invested)");

        if (soldAll.Count > 0)
        {
            sb.AppendLine(dutch ? "\n✅ <b>Recente verkopen:</b>" : "\n✅ <b>Recent sales:</b>");
            foreach (FlipItem item in soldAll.TakeLast(5).Reverse())
            {
                sb.AppendLine(
                    $"• #{item.Id} {System.Net.WebUtility.HtmlEncode(item.Name)} — {Listing.FormatEuro(item.TotalCost)} → {Listing.FormatEuro(item.SellPrice!.Value)} ({FormatSignedEuro(item.Profit ?? 0)})");
            }
        }

        if (open.Count > 0)
        {
            sb.Append(dutch
                ? "\n👇 Tik op een item om onderdelen toe te voegen of als verkocht te markeren:"
                : "\n👇 Tap an open item below to add parts or mark it sold:");
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatSignedEuro(double amount)
    {
        return amount >= 0 ? "+" + Listing.FormatEuro(amount) : "-" + Listing.FormatEuro(-amount);
    }
}

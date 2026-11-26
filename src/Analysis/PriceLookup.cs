using System.Net;
using System.Text;

namespace MarktplaatsNotifier;

public class PriceCheckResult
{
    public string Query { get; set; } = "";
    public int SampleCount { get; set; }
    public double MinPrice { get; set; }
    public double MedianPrice { get; set; }
    public double MaxPrice { get; set; }
    public int SuggestedBrokenBuy { get; set; }
    public int SuggestedWorkingBuy { get; set; }
}

public static class PriceLookup
{
    public static PriceCheckResult Compute(string query, IEnumerable<double> livePrices, IEnumerable<double> historyPrices)
    {
        List<double> all = new List<double>();
        foreach (double p in livePrices)
        {
            if (p >= 2 && p <= 50000)
            {
                all.Add(p);
            }
        }
        foreach (double p in historyPrices)
        {
            if (p >= 2 && p <= 50000)
            {
                all.Add(p);
            }
        }

        all.Sort();

        // Trim extreme top/bottom 10% outliers when we have enough samples
        if (all.Count >= 10)
        {
            int trim = Math.Max(1, all.Count / 10);
            all = all.GetRange(trim, all.Count - trim * 2);
        }

        if (all.Count == 0)
        {
            return new PriceCheckResult { Query = query };
        }

        double median = all.Count % 2 == 1
            ? all[all.Count / 2]
            : (all[all.Count / 2 - 1] + all[all.Count / 2]) / 2.0;

        return new PriceCheckResult
        {
            Query = query,
            SampleCount = all.Count,
            MinPrice = all[0],
            MedianPrice = Math.Round(median, 2),
            MaxPrice = all[^1],
            SuggestedBrokenBuy = (int)Math.Round(median * 0.35),
            SuggestedWorkingBuy = (int)Math.Round(median * 0.55)
        };
    }

    public static string FormatHtml(PriceCheckResult result)
    {
        if (result.SampleCount == 0)
        {
            return $"💶 No priced listings found for <b>{WebUtility.HtmlEncode(result.Query)}</b> right now.";
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"💶 <b>Market price: {WebUtility.HtmlEncode(result.Query)}</b> ({result.SampleCount} priced listings)\n");
        sb.AppendLine($"• Lowest: <b>{Listing.FormatEuro(result.MinPrice)}</b>");
        sb.AppendLine($"• Median: <b>{Listing.FormatEuro(result.MedianPrice)}</b>");
        sb.AppendLine($"• Highest: <b>{Listing.FormatEuro(result.MaxPrice)}</b>\n");
        sb.AppendLine("<b>Buy targets</b>");
        sb.AppendLine($"• Broken (≤35%): <b>≤ €{result.SuggestedBrokenBuy}</b>");
        sb.Append($"• Working (≤55%): <b>≤ €{result.SuggestedWorkingBuy}</b>");
        return sb.ToString();
    }
}

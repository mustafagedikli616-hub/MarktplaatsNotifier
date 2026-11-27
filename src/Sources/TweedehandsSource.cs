using System.Net;
using System.Text.Json;

namespace MarktplaatsNotifier;

// Polls 2dehands.be (Belgium's Marktplaats sibling, same JSON API) in slow round-robin across active searches.
public class TweedehandsSource
{
    private static readonly string[] AllowedComponentTokens =
    {
        "rtx", "gtx", "rx ", "radeon", "geforce", "videokaart", "gpu",
        "ryzen", "5800x3d", "5700x", "5600", "7800x3d", "9800x3d",
        "i5-", "i7-", "i9-", "core i5", "core i7", "core i9", "cpu", "processor",
        "ddr4", "ddr5", "ram"
    };

    private static readonly string[] ExcludedBulkyTokens =
    {
        "monitor", "pc", "desktop", "behuizing", "kast", "tv", "ultrawide",
        "odyssey", "ultragear", "laptop", "ps5", "ps4", "xbox", "switch", "voeding"
    };

    private DateTime lastFetch = DateTime.MinValue;
    private DateTime blockedUntil = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> lastCheckPerQuery = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> seenPerQuery = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

    // Only check expensive, small, robust components (GPUs, CPUs, RAM) on 2dehands.be
    public static bool IsEligibleRule(SearchRule rule)
    {
        if (rule.MaxPrice < 40)
        {
            return false;
        }

        string q = " " + rule.Query.ToLowerInvariant() + " ";
        foreach (string bulky in ExcludedBulkyTokens)
        {
            if (q.Contains(" " + bulky + " ", StringComparison.Ordinal) ||
                q.Contains(bulky, StringComparison.Ordinal) && bulky.Length >= 4)
            {
                return false;
            }
        }

        foreach (string token in AllowedComponentTokens)
        {
            if (token == "ram")
            {
                if (q.Contains(" ram ", StringComparison.Ordinal)) return true;
                continue;
            }
            if (q.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // Stricter price cap for cross-border Belgian listings (15% buffer for shipping & risk)
    public static int StrictMaxPrice(int ruleMaxPrice)
    {
        return Math.Max(1, (int)Math.Floor(ruleMaxPrice * 0.85));
    }

    public async Task<(string Query, List<Listing> NewAds)> FetchNextDueAsync(
        HttpClient client,
        IReadOnlyList<SearchRule> rules,
        TimeSpan minGlobalGap,
        TimeSpan perQueryInterval)
    {
        List<Listing> empty = new List<Listing>();
        if (rules.Count == 0 || DateTime.Now < blockedUntil || DateTime.Now - lastFetch < minGlobalGap)
        {
            return ("", empty);
        }

        SearchRule? picked = PickDueRule(rules, perQueryInterval);
        if (picked == null)
        {
            return ("", empty);
        }

        lastFetch = DateTime.Now;
        lastCheckPerQuery[picked.Query] = DateTime.Now;

        string url =
            $"https://www.2dehands.be/lrp/api/search?query={Uri.EscapeDataString(picked.Query)}&limit=20&sortBy=SORT_INDEX&sortOrder=DECREASING";

        try
        {
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            req.Headers.TryAddWithoutValidation("Referer", "https://www.2dehands.be/");

            using HttpResponseMessage resp = await client.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                blockedUntil = DateTime.Now.AddMinutes(30);
                BotLog.Write($"2dehands.be returned {(int)resp.StatusCode}: pausing 2dehands checks for 30 min.");
                return (picked.Query, empty);
            }

            if (!resp.IsSuccessStatusCode)
            {
                return (picked.Query, empty);
            }

            string json = await resp.Content.ReadAsStringAsync();
            List<Listing> parsed = ParseJson(json);
            return (picked.Query, FilterNewForQuery(picked.Query, parsed));
        }
        catch (Exception ex)
        {
            BotLog.Write($"2dehands.be fetch failed ({picked.Query}): {ex.Message}");
            return (picked.Query, empty);
        }
    }

    public static List<Listing> ParseJson(string json)
    {
        List<Listing> ads = new List<Listing>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("listings", out JsonElement listingsEl) ||
                listingsEl.ValueKind != JsonValueKind.Array)
            {
                return ads;
            }

            foreach (JsonElement item in listingsEl.EnumerateArray())
            {
                Listing ad = Listing.FromJson(item);
                if (string.IsNullOrWhiteSpace(ad.Id) || string.IsNullOrWhiteSpace(ad.Title))
                {
                    continue;
                }

                ad.Id = "2dh-" + ad.Id;
                ad.Source = "2dehands.be";
                if (!string.IsNullOrWhiteSpace(ad.Link) && !ad.Link.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    ad.Link = "https://www.2dehands.be" + ad.Link;
                }
                if (ad.City != "" && !ad.City.EndsWith("(BE)", StringComparison.OrdinalIgnoreCase))
                {
                    ad.City += " (BE)";
                }
                else if (ad.City == "")
                {
                    ad.City = "België (BE)";
                }

                ads.Add(ad);
            }
        }
        catch (Exception ex)
        {
            BotLog.Write($"Couldn't parse 2dehands.be JSON: {ex.Message}");
        }

        return ads;
    }

    private SearchRule? PickDueRule(IReadOnlyList<SearchRule> rules, TimeSpan perQueryInterval)
    {
        DateTime now = DateTime.Now;
        SearchRule? best = null;
        DateTime oldest = DateTime.MaxValue;

        foreach (SearchRule rule in rules)
        {
            DateTime last = lastCheckPerQuery.GetValueOrDefault(rule.Query, DateTime.MinValue);
            TimeSpan wait = rule.Urgent ? TimeSpan.FromSeconds(Math.Max(90, perQueryInterval.TotalSeconds / 2)) : perQueryInterval;
            if (now - last >= wait && last < oldest)
            {
                oldest = last;
                best = rule;
            }
        }

        return best;
    }

    private List<Listing> FilterNewForQuery(string query, List<Listing> parsed)
    {
        List<Listing> fresh = new List<Listing>();
        if (!seenPerQuery.TryGetValue(query, out HashSet<string>? seen))
        {
            // Quiet first pass so startup doesn't flood with old Belgian ads
            seen = new HashSet<string>(parsed.Select(a => a.Id));
            seenPerQuery[query] = seen;
            return fresh;
        }

        foreach (Listing ad in parsed)
        {
            if (seen.Add(ad.Id))
            {
                fresh.Add(ad);
            }
        }

        if (seen.Count > 2000)
        {
            seen.Clear();
            foreach (Listing ad in parsed)
            {
                seen.Add(ad.Id);
            }
        }

        return fresh;
    }
}

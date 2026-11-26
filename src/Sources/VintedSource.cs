using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace MarktplaatsNotifier;

// Polls Vinted.nl SSR catalog sorted by newest first in slow round-robin across active searches.
public class VintedSource
{
    private static readonly Regex CardIdRegex = new Regex(
        @"data-testid=""product-item-id-(\d+)""",
        RegexOptions.Compiled);

    private static readonly Regex EuroPriceRegex = new Regex(
        @"(\d{1,5}(?:[.,]\d{2})?)\s*€",
        RegexOptions.Compiled);

    private static readonly Regex BrandRegex = new Regex(
        @",\s*Merk:\s*([^,]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StateRegex = new Regex(
        @",\s*Staat:\s*([^,]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private DateTime lastFetch = DateTime.MinValue;
    private DateTime blockedUntil = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> lastCheckPerQuery = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> seenPerQuery = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

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
            $"https://www.vinted.nl/catalog?search_text={Uri.EscapeDataString(picked.Query)}&order=newest_first";
        if (picked.MaxPrice > 0)
        {
            url += $"&price_to={picked.MaxPrice}";
        }

        try
        {
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            req.Headers.TryAddWithoutValidation("Accept-Language", "nl-NL,nl;q=0.9,en;q=0.8");
            req.Headers.TryAddWithoutValidation("Referer", "https://www.vinted.nl/");

            using HttpResponseMessage resp = await client.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                blockedUntil = DateTime.Now.AddMinutes(30);
                BotLog.Write($"Vinted.nl returned {(int)resp.StatusCode}: pausing Vinted checks for 30 min.");
                return (picked.Query, empty);
            }

            if (!resp.IsSuccessStatusCode)
            {
                return (picked.Query, empty);
            }

            string html = await resp.Content.ReadAsStringAsync();
            List<Listing> parsed = ParseHtml(html);
            return (picked.Query, FilterNewForQuery(picked.Query, parsed));
        }
        catch (Exception ex)
        {
            BotLog.Write($"Vinted.nl fetch failed ({picked.Query}): {ex.Message}");
            return (picked.Query, empty);
        }
    }

    public static List<Listing> ParseHtml(string html)
    {
        List<Listing> ads = new List<Listing>();
        HashSet<string> batchIds = new HashSet<string>();

        foreach (Match idMatch in CardIdRegex.Matches(html))
        {
            string itemId = idMatch.Groups[1].Value;
            if (!batchIds.Add(itemId))
            {
                continue;
            }

            int start = idMatch.Index;
            int len = Math.Min(3500, html.Length - start);
            string chunk = html.Substring(start, len);

            Match imgMatch = Regex.Match(
                chunk,
                $@"<img\s+[^>]*src=""([^""]+)""[^>]*alt=""([^""]+)""[^>]*data-testid=""product-item-id-{itemId}--image--img""",
                RegexOptions.IgnoreCase);

            if (!imgMatch.Success)
            {
                imgMatch = Regex.Match(
                    chunk,
                    $@"<img\s+[^>]*alt=""([^""]+)""[^>]*src=""([^""]+)""[^>]*data-testid=""product-item-id-{itemId}--image--img""",
                    RegexOptions.IgnoreCase);
            }

            Match hrefMatch = Regex.Match(
                chunk,
                $@"<a\s+[^>]*href=""(/items/{itemId}[^""?]*)[^""]*""[^>]*data-testid=""product-item-id-{itemId}--overlay-link""",
                RegexOptions.IgnoreCase);

            if (!imgMatch.Success || !hrefMatch.Success)
            {
                continue;
            }

            bool srcFirst = imgMatch.Value.IndexOf("src=", StringComparison.OrdinalIgnoreCase) <
                            imgMatch.Value.IndexOf("alt=", StringComparison.OrdinalIgnoreCase);
            string photo = WebUtility.HtmlDecode(srcFirst ? imgMatch.Groups[1].Value : imgMatch.Groups[2].Value).Trim();
            string alt = WebUtility.HtmlDecode(srcFirst ? imgMatch.Groups[2].Value : imgMatch.Groups[1].Value).Trim();
            string href = WebUtility.HtmlDecode(hrefMatch.Groups[1].Value).Trim();

            if (alt == "")
            {
                continue;
            }

            // Typical Vinted NL alt: "Vendo nintendo switch, Merk: Nintendo, Staat: Heel goed, 100.00 €, 105.70 €"
            string title = alt;
            int cut = alt.IndexOf(", Merk:", StringComparison.OrdinalIgnoreCase);
            if (cut < 0)
            {
                cut = alt.IndexOf(", Staat:", StringComparison.OrdinalIgnoreCase);
            }
            if (cut > 0)
            {
                title = alt.Substring(0, cut).Trim();
            }
            else
            {
                Match firstPrice = EuroPriceRegex.Match(alt);
                if (firstPrice.Success && firstPrice.Index > 2)
                {
                    title = alt.Substring(0, firstPrice.Index).Trim().TrimEnd(',');
                }
            }

            if (title == "")
            {
                continue;
            }

            string brand = "";
            Match bm = BrandRegex.Match(alt);
            if (bm.Success)
            {
                brand = bm.Groups[1].Value.Trim();
            }

            string condition = "";
            Match sm = StateRegex.Match(alt);
            if (sm.Success)
            {
                condition = sm.Groups[1].Value.Trim();
            }

            MatchCollection prices = EuroPriceRegex.Matches(alt);
            double price = 0;
            double totalWithProtection = 0;
            if (prices.Count >= 1)
            {
                double.TryParse(prices[0].Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out price);
            }
            if (prices.Count >= 2)
            {
                double.TryParse(prices[1].Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out totalWithProtection);
            }

            List<string> descParts = new List<string>();
            if (brand != "") descParts.Add($"Merk: {brand}");
            if (condition != "") descParts.Add($"Staat: {condition}");
            if (totalWithProtection > price && price > 0)
            {
                descParts.Add($"Incl. kopersbescherming: {Listing.FormatEuro(totalWithProtection)}");
            }

            ads.Add(new Listing
            {
                Id = "vinted-" + itemId,
                Source = "Vinted",
                Title = title,
                Description = string.Join(" · ", descParts),
                Price = price,
                PriceType = "FIXED",
                PriceText = price > 0 ? Listing.FormatEuro(price) : "Vinted",
                Condition = condition,
                City = "Vinted EU",
                Delivery = "Verzenden via Vinted",
                CanShip = true,
                SellerName = "Vinted seller",
                SellerType = "CONSUMER",
                Photo = photo,
                Link = "https://www.vinted.nl" + href
            });
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
            TimeSpan wait = rule.Urgent ? TimeSpan.FromSeconds(Math.Max(120, perQueryInterval.TotalSeconds / 2)) : perQueryInterval;
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

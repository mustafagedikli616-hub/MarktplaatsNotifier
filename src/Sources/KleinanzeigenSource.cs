using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MarktplaatsNotifier;

// Polls Kleinanzeigen.de (Germany) sorted by newest first in slow round-robin across active searches.
public class KleinanzeigenSource
{
    private static readonly Regex ArticleRegex = new Regex(
        @"<article\b[^>]*\bdata-adid=""(\d+)""[^>]*\bdata-href=""([^""]+)""[^>]*>(.*?)</article>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex JsonLdRegex = new Regex(
        @"<script[^>]*type=""application/ld\+json""[^>]*>(.*?)</script>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex PriceRegex = new Regex(
        @"(\d{1,3}(?:\.\d{3})*|\d+)\s*€(?:\s*(VB))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CityRegex = new Regex(
        @"\b(\d{5}\s+[A-Za-zÄÖÜäöüß][A-Za-zÄÖÜäöüß\s\-/\.]{1,40})",
        RegexOptions.Compiled);

    private static readonly Regex HtmlTags = new Regex("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NonSlug = new Regex(@"[^a-zA-Z0-9äöüßÄÖÜ]+", RegexOptions.Compiled);

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

        string slug = BuildSlug(picked.Query);
        if (slug == "")
        {
            return (picked.Query, empty);
        }

        string url = $"https://www.kleinanzeigen.de/s-sortierung:datum/{Uri.EscapeDataString(slug)}/k0";

        try
        {
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            req.Headers.TryAddWithoutValidation("Accept-Language", "de-DE,de;q=0.9,nl;q=0.8,en;q=0.7");
            req.Headers.TryAddWithoutValidation("Referer", "https://www.kleinanzeigen.de/");

            using HttpResponseMessage resp = await client.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                blockedUntil = DateTime.Now.AddMinutes(30);
                BotLog.Write($"Kleinanzeigen.de returned {(int)resp.StatusCode}: pausing Kleinanzeigen checks for 30 min.");
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
            BotLog.Write($"Kleinanzeigen.de fetch failed ({picked.Query}): {ex.Message}");
            return (picked.Query, empty);
        }
    }

    public static List<Listing> ParseHtml(string html)
    {
        List<Listing> ads = new List<Listing>();
        HashSet<string> batchIds = new HashSet<string>();

        foreach (Match m in ArticleRegex.Matches(html))
        {
            string rawId = m.Groups[1].Value;
            string href = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
            string body = m.Groups[3].Value;

            if (rawId == "" || !batchIds.Add(rawId))
            {
                continue;
            }

            string title = "";
            string description = "";
            string photo = "";

            Match ldMatch = JsonLdRegex.Match(body);
            if (ldMatch.Success)
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(ldMatch.Groups[1].Value);
                    JsonElement root = doc.RootElement;
                    if (root.TryGetProperty("title", out JsonElement tEl) && tEl.ValueKind == JsonValueKind.String)
                    {
                        title = WebUtility.HtmlDecode(tEl.GetString() ?? "").Trim();
                    }
                    if (root.TryGetProperty("description", out JsonElement dEl) && dEl.ValueKind == JsonValueKind.String)
                    {
                        description = WebUtility.HtmlDecode(dEl.GetString() ?? "").Trim();
                    }
                    if (root.TryGetProperty("contentUrl", out JsonElement imgEl) && imgEl.ValueKind == JsonValueKind.String)
                    {
                        photo = (imgEl.GetString() ?? "").Trim();
                    }
                }
                catch
                {
                }
            }

            string noScript = JsonLdRegex.Replace(body, " ");
            string plain = Whitespace.Replace(WebUtility.HtmlDecode(HtmlTags.Replace(noScript, " | ")), " ").Trim();

            if (title == "")
            {
                Match h2 = Regex.Match(noScript, @"<h2[^>]*>(.*?)</h2>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (h2.Success)
                {
                    title = Whitespace.Replace(WebUtility.HtmlDecode(HtmlTags.Replace(h2.Groups[1].Value, " ")), " ").Trim();
                }
            }

            if (title == "")
            {
                continue;
            }

            double price = 0;
            string priceText = "VB";
            string priceType = "FAST_BID";

            if (plain.Contains("Zu verschenken", StringComparison.OrdinalIgnoreCase))
            {
                price = 0;
                priceText = "Gratis (Zu verschenken)";
                priceType = "FREE";
            }
            else
            {
                Match pm = PriceRegex.Match(plain);
                if (pm.Success)
                {
                    string numStr = pm.Groups[1].Value.Replace(".", "");
                    if (double.TryParse(numStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsedPrice))
                    {
                        price = parsedPrice;
                        bool vb = pm.Groups[2].Success;
                        priceText = Listing.FormatEuro(price) + (vb ? " VB" : "");
                        priceType = vb ? "MIN_BID" : "FIXED";
                    }
                }
            }

            bool canShip =
                plain.Contains("Versand möglich", StringComparison.OrdinalIgnoreCase) ||
                plain.Contains("Direkt kaufen", StringComparison.OrdinalIgnoreCase);

            string city = "Duitsland (DE)";
            Match cm = CityRegex.Match(plain);
            if (cm.Success)
            {
                city = cm.Groups[1].Value.Trim() + " (DE)";
            }

            string fullLink = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? href
                : "https://www.kleinanzeigen.de" + href;

            ads.Add(new Listing
            {
                Id = "ka-" + rawId,
                Source = "Kleinanzeigen",
                Title = title,
                Description = description,
                Price = price,
                PriceType = priceType,
                PriceText = priceText,
                City = city,
                Delivery = canShip ? "Versand möglich" : "Nur Abholung (DE)",
                CanShip = canShip,
                SellerName = "Kleinanzeigen seller",
                SellerType = "CONSUMER",
                Photo = photo,
                Link = fullLink
            });
        }

        return ads;
    }

    public static string BuildSlug(string query)
    {
        return NonSlug.Replace(query.Trim().ToLowerInvariant(), "-").Trim('-');
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

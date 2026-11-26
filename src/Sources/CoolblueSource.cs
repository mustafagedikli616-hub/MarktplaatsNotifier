using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MarktplaatsNotifier;

// Polls Coolblue Tweedekans tech categories (returned/open-box deals with warranty) in slow rotation.
public class CoolblueSource
{
    private static readonly string[] CategoryUrls =
    {
        "https://www.coolblue.nl/tweedekans/monitoren",
        "https://www.coolblue.nl/tweedekans/laptops",
        "https://www.coolblue.nl/tweedekans/desktops",
        "https://www.coolblue.nl/tweedekans/videokaarten",
        "https://www.coolblue.nl/tweedekans/spelcomputers",
        "https://www.coolblue.nl/tweedekans/koptelefoons",
        "https://www.coolblue.nl/tweedekans/smartphones",
        "https://www.coolblue.nl/tweedekans/tablets"
    };

    private static readonly Regex JsonLdRegex = new Regex(
        @"<script[^>]*type=""application/ld\+json""[^>]*>(.*?)</script>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex TweedekansLinkRegex = new Regex(
        @"https://www\.coolblue\.nl/product-tweedekans/(\d+)/(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NieuwprijsRegex = new Regex(
        @"Nieuwprijs\s*(\d{1,5})\s*(?:,\s*-|[.,](\d{2}))?\s*(\d{1,5})\s*(?:,\s*-|[.,](\d{2}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ConditionRegex = new Regex(
        @"\b(Onbeschadigd|Lichte gebruikssporen|Zichtbare gebruikssporen|Beschadigd)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SvgTags = new Regex(@"<svg\b.*?</svg>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex HtmlTags = new Regex("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

    private DateTime lastFetch = DateTime.MinValue;
    private DateTime blockedUntil = DateTime.MinValue;
    private int nextCategoryIndex = 0;
    private readonly Dictionary<string, HashSet<string>> seenPerCategory = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

    public async Task<List<Listing>> FetchNextCategoryIfDueAsync(HttpClient client, TimeSpan interval)
    {
        List<Listing> empty = new List<Listing>();
        if (DateTime.Now < blockedUntil || DateTime.Now - lastFetch < interval)
        {
            return empty;
        }

        lastFetch = DateTime.Now;
        string url = CategoryUrls[nextCategoryIndex % CategoryUrls.Length];
        nextCategoryIndex = (nextCategoryIndex + 1) % CategoryUrls.Length;

        try
        {
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            req.Headers.TryAddWithoutValidation("Accept-Language", "nl-NL,nl;q=0.9,en;q=0.8");
            req.Headers.TryAddWithoutValidation("Referer", "https://www.coolblue.nl/tweedekans");

            using HttpResponseMessage resp = await client.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                blockedUntil = DateTime.Now.AddMinutes(30);
                BotLog.Write($"Coolblue Tweedekans returned {(int)resp.StatusCode}: pausing Coolblue checks for 30 min.");
                return empty;
            }

            if (!resp.IsSuccessStatusCode)
            {
                return empty;
            }

            string html = await resp.Content.ReadAsStringAsync();
            List<Listing> parsed = ParseHtml(html);

            if (!seenPerCategory.TryGetValue(url, out HashSet<string>? seen))
            {
                seen = new HashSet<string>(parsed.Select(a => a.Id));
                seenPerCategory[url] = seen;
                BotLog.Write($"Coolblue Tweedekans ({url.Split('/').Last()}): baseline remembered ({parsed.Count} offers).");
                return empty;
            }

            List<Listing> fresh = new List<Listing>();
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
        catch (Exception ex)
        {
            BotLog.Write($"Coolblue Tweedekans fetch failed ({url}): {ex.Message}");
            return empty;
        }
    }

    public static List<Listing> ParseHtml(string html)
    {
        List<Listing> ads = new List<Listing>();

        // 1. Read JSON-LD ItemList for product names, images, and retail prices keyed by productID
        Dictionary<string, (string Name, string Image, double RetailPrice)> products =
            new Dictionary<string, (string, string, double)>();

        foreach (Match ld in JsonLdRegex.Matches(html))
        {
            string json = ld.Groups[1].Value;
            if (!json.Contains("ItemList", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("itemListElement", out JsonElement items) ||
                    items.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement item in items.EnumerateArray())
                {
                    string pid = "";
                    if (item.TryGetProperty("productID", out JsonElement pidEl))
                    {
                        pid = pidEl.ValueKind == JsonValueKind.Number
                            ? pidEl.GetInt64().ToString(CultureInfo.InvariantCulture)
                            : (pidEl.GetString() ?? "");
                    }
                    if (pid == "")
                    {
                        continue;
                    }

                    string name = item.TryGetProperty("name", out JsonElement nEl) ? (nEl.GetString() ?? "").Trim() : "";
                    string img = "";
                    if (item.TryGetProperty("image", out JsonElement imgEl))
                    {
                        if (imgEl.ValueKind == JsonValueKind.Array && imgEl.GetArrayLength() > 0)
                        {
                            img = imgEl[0].GetString() ?? "";
                        }
                        else if (imgEl.ValueKind == JsonValueKind.String)
                        {
                            img = imgEl.GetString() ?? "";
                        }
                    }

                    double retailPrice = 0;
                    if (item.TryGetProperty("offers", out JsonElement offersEl) &&
                        offersEl.TryGetProperty("price", out JsonElement pEl))
                    {
                        if (pEl.ValueKind == JsonValueKind.Number)
                        {
                            retailPrice = pEl.GetDouble();
                        }
                        else if (pEl.ValueKind == JsonValueKind.String)
                        {
                            double.TryParse(pEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out retailPrice);
                        }
                    }

                    if (name != "")
                    {
                        products[pid] = (WebUtility.HtmlDecode(name), img, retailPrice);
                    }
                }
            }
            catch
            {
            }
        }

        // 2. Find unique product-tweedekans/<productId>/<offerId> cards and extract exact Tweedekans price + condition
        List<(string ProductId, string OfferId, string Url, int Index)> firstOccurrences =
            new List<(string, string, string, int)>();
        HashSet<string> seenOffers = new HashSet<string>();

        foreach (Match m in TweedekansLinkRegex.Matches(html))
        {
            string pid = m.Groups[1].Value;
            string oid = m.Groups[2].Value;
            if (seenOffers.Add(oid))
            {
                firstOccurrences.Add((pid, oid, m.Value, m.Index));
            }
        }

        for (int i = 0; i < firstOccurrences.Count; i++)
        {
            var (pid, oid, url, start) = firstOccurrences[i];
            int end = i + 1 < firstOccurrences.Count
                ? Math.Min(firstOccurrences[i + 1].Index, start + 40000)
                : Math.Min(html.Length, start + 40000);

            string cardHtml = html.Substring(start, end - start);
            string cardText = Whitespace.Replace(
                WebUtility.HtmlDecode(HtmlTags.Replace(SvgTags.Replace(cardHtml, " "), " ")),
                " ").Trim();

            products.TryGetValue(pid, out var meta);
            string title = meta.Name ?? "";
            if (title == "")
            {
                continue;
            }

            double retailPrice = meta.RetailPrice;
            double price = retailPrice;

            Match pm = NieuwprijsRegex.Match(cardText);
            if (pm.Success)
            {
                double parsedRetail = ParseEuroParts(pm.Groups[1].Value, pm.Groups[2].Value);
                double parsedTk = ParseEuroParts(pm.Groups[3].Value, pm.Groups[4].Value);
                if (parsedRetail > 0)
                {
                    retailPrice = parsedRetail;
                }
                if (parsedTk > 0)
                {
                    price = parsedTk;
                }
            }

            string condition = "Tweedekans";
            Match cm = ConditionRegex.Match(cardText);
            if (cm.Success)
            {
                condition = cm.Groups[1].Value;
            }

            string desc = $"Coolblue Tweedekans ({condition}) met garantie.";
            if (retailPrice > price && price > 0)
            {
                int pctOff = (int)Math.Round((retailPrice - price) / retailPrice * 100);
                desc = $"Coolblue Tweedekans ({condition}) · Nieuwprijs {Listing.FormatEuro(retailPrice)} (-{pctOff}% korting, incl. Coolblue garantie).";
            }

            ads.Add(new Listing
            {
                Id = "cb-" + oid,
                Source = "Coolblue Tweedekans",
                Title = title,
                Description = desc,
                Price = price,
                OldPrice = retailPrice > price ? retailPrice : 0,
                PriceType = "FIXED",
                PriceText = price > 0 ? Listing.FormatEuro(price) : "Tweedekans",
                Condition = condition,
                City = "Coolblue NL",
                Delivery = "Gratis verzending (morgen in huis)",
                CanShip = true,
                SellerName = "Coolblue Tweedekans",
                SellerType = "COOLBLUE",
                Photo = meta.Image ?? "",
                Link = url
            });
        }

        return ads;
    }

    private static double ParseEuroParts(string eurosStr, string centsStr)
    {
        if (!int.TryParse(eurosStr, out int euros))
        {
            return 0;
        }

        double val = euros;
        if (!string.IsNullOrEmpty(centsStr) && int.TryParse(centsStr, out int cents))
        {
            val += cents / 100.0;
        }

        return val;
    }
}

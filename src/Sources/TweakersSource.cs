using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MarktplaatsNotifier;

// Polls Tweakers Vraag & Aanbod RSS feed as a second listing source for PC hardware, monitors, and consoles.
public class TweakersSource
{
    private static readonly string[] FeedUrls =
    {
        "https://tweakers.net/feeds/va/aanbod.xml",
        "https://tweakers.net/feeds/va.xml"
    };

    private static readonly Regex PriceRegex = new Regex(
        @"€\s*(\d{1,5})(?:[.,](\d{2}|-))?",
        RegexOptions.Compiled);

    private static readonly Regex IdRegex = new Regex(
        @"/aanbod/(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HtmlTags = new Regex("<[^>]+>", RegexOptions.Compiled);

    private DateTime lastFetch = DateTime.MinValue;
    private DateTime blockedUntil = DateTime.MinValue;
    private bool firstRunDone = false;
    private readonly HashSet<string> seenFeedIds = new HashSet<string>();

    public async Task<List<Listing>> FetchNewIfDueAsync(HttpClient client, TimeSpan interval)
    {
        List<Listing> result = new List<Listing>();
        if (DateTime.Now < blockedUntil || DateTime.Now - lastFetch < interval)
        {
            return result;
        }

        lastFetch = DateTime.Now;
        string xml = "";

        foreach (string url in FeedUrls)
        {
            try
            {
                using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Accept", "application/rss+xml, application/xml, text/xml, */*");
                using HttpResponseMessage resp = await client.SendAsync(req);
                if (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    blockedUntil = DateTime.Now.AddMinutes(30);
                    BotLog.Write($"Tweakers RSS returned {(int)resp.StatusCode}: pausing Tweakers checks for 30 min.");
                    return result;
                }

                if (resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    if (body.Contains("<rss", StringComparison.OrdinalIgnoreCase) ||
                        body.Contains("<feed", StringComparison.OrdinalIgnoreCase))
                    {
                        xml = body;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                BotLog.Write($"Tweakers RSS fetch failed ({url}): {ex.Message}");
            }
        }

        if (xml == "")
        {
            return result;
        }

        List<Listing> parsed = ParseRss(xml);
        if (!firstRunDone)
        {
            // Quiet first pass so startup doesn't flood with old Tweakers ads
            foreach (Listing ad in parsed)
            {
                seenFeedIds.Add(ad.Id);
            }
            firstRunDone = true;
            BotLog.Write($"Tweakers V&A: baseline remembered ({parsed.Count} listings).");
            return result;
        }

        foreach (Listing ad in parsed)
        {
            if (seenFeedIds.Add(ad.Id))
            {
                result.Add(ad);
            }
        }

        if (seenFeedIds.Count > 5000)
        {
            seenFeedIds.Clear();
            foreach (Listing ad in parsed)
            {
                seenFeedIds.Add(ad.Id);
            }
        }

        return result;
    }

    public static List<Listing> ParseRss(string xml)
    {
        List<Listing> ads = new List<Listing>();
        try
        {
            XDocument doc = XDocument.Parse(xml);
            foreach (XElement item in doc.Descendants("item"))
            {
                string title = WebUtility.HtmlDecode((item.Element("title")?.Value ?? "").Trim());
                string link = (item.Element("link")?.Value ?? "").Trim();
                string rawDesc = item.Element("description")?.Value ?? "";
                string desc = WebUtility.HtmlDecode(HtmlTags.Replace(rawDesc, " ")).Trim();
                string pubDate = (item.Element("pubDate")?.Value ?? "").Trim();
                string guid = (item.Element("guid")?.Value ?? link).Trim();

                if (title == "" || link == "")
                {
                    continue;
                }

                // Skip "Vraag" / "Gevraagd" (wanted) ads on Tweakers and strip leading "A: " prefix
                if (title.StartsWith("Gevraagd:", StringComparison.OrdinalIgnoreCase) ||
                    title.StartsWith("V:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (title.StartsWith("A:", StringComparison.OrdinalIgnoreCase))
                {
                    title = title.Substring(2).Trim();
                }

                Match idMatch = IdRegex.Match(link);
                string id = idMatch.Success
                    ? "tw" + idMatch.Groups[1].Value
                    : "tw" + Math.Abs(guid.GetHashCode()).ToString(CultureInfo.InvariantCulture);

                double price = ExtractPrice(title + " " + desc);
                DateTime postedAt = DateTime.MinValue;
                if (DateTimeOffset.TryParse(pubDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dto))
                {
                    postedAt = dto.LocalDateTime;
                }

                ads.Add(new Listing
                {
                    Id = id,
                    Source = "Tweakers",
                    Title = title,
                    Description = desc,
                    Price = price,
                    PriceType = price > 0 ? "FIXED" : "FAST_BID",
                    PriceText = price > 0 ? Listing.FormatEuro(price) : "Bieden",
                    City = "Tweakers V&A",
                    Delivery = "Verzenden / Ophalen",
                    CanShip = true,
                    SellerName = "Tweakers member",
                    SellerType = "CONSUMER",
                    PostedAt = postedAt,
                    Date = pubDate,
                    Link = link
                });
            }
        }
        catch (Exception ex)
        {
            BotLog.Write($"Couldn't parse Tweakers RSS: {ex.Message}");
        }

        return ads;
    }

    private static double ExtractPrice(string text)
    {
        Match m = PriceRegex.Match(text);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out int euros))
        {
            return 0;
        }

        double price = euros;
        if (m.Groups[2].Success && m.Groups[2].Value != "-" && int.TryParse(m.Groups[2].Value, out int cents))
        {
            price += cents / 100.0;
        }

        return price;
    }
}

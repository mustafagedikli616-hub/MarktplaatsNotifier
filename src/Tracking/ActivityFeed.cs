using System.Globalization;

namespace MarktplaatsNotifier;

public class FeedEvent
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Kind { get; set; } = "sent"; // "sent" or "skipped"
    public string ListingId { get; set; } = "";
    public string Source { get; set; } = "Marktplaats";
    public string Title { get; set; } = "";
    public string PriceText { get; set; } = "";
    public double Price { get; set; }
    public double OldPrice { get; set; }
    public int TypicalPct { get; set; }
    public string Search { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Alert { get; set; } = "";
    public string AiVerdict { get; set; } = "";
    public string Seller { get; set; } = "";
    public string SellerRating { get; set; } = "";
    public bool Verified { get; set; }
    public string City { get; set; } = "";
    public string Photo { get; set; } = "";
    public string Link { get; set; } = "";
}

public class SkipBreakdown
{
    public int Price { get; set; }
    public int SkipWord { get; set; }
    public int Business { get; set; }
    public int Outbid { get; set; }
}

public class ActivityFeed
{
    private const int MaxEvents = 150;
    private readonly LinkedList<FeedEvent> events = new LinkedList<FeedEvent>();
    private readonly object feedLock = new object();

    private DateOnly breakdownDate = DateOnly.FromDateTime(DateTime.Now);
    private SkipBreakdown breakdown = new SkipBreakdown();

    public void RecordSent(Listing ad, string alert)
    {
        int typicalPct = 0;
        if (ad.Price > 0 && ad.TypicalPrice > 0)
        {
            typicalPct = (int)Math.Round((ad.Price - ad.TypicalPrice) / ad.TypicalPrice * 100);
        }

        string rating = ad.SellerRating >= 0
            ? ad.SellerRating.ToString("0.0", CultureInfo.InvariantCulture) +
              (ad.SellerReviewCount >= 0 ? $" ({ad.SellerReviewCount})" : "")
            : "";

        string cityLine = ad.City;
        if (ad.DistanceKm >= 0)
        {
            cityLine += $" ({ad.DistanceKm} km)";
        }
        if (ad.CanShip)
        {
            cityLine += " · Shipping";
        }

        string reason = "";
        if (ad.AiVerdict != "")
        {
            reason = $"AI {ad.AiVerdict} · {ad.AiReason}";
            if (ad.AiResale != "")
            {
                reason += $" · Resale {ad.AiResale}";
            }
        }
        else if (ad.Hint != "")
        {
            reason = ad.Hint;
        }

        Add(new FeedEvent
        {
            Timestamp = DateTime.Now,
            Kind = "sent",
            ListingId = ad.Id,
            Source = ad.Source,
            Title = ad.Title,
            PriceText = ad.PriceText,
            Price = ad.Price,
            OldPrice = ad.OldPrice,
            TypicalPct = typicalPct,
            Search = ad.MatchedSearch,
            Alert = alert,
            Reason = reason,
            AiVerdict = ad.AiVerdict,
            Seller = ad.SellerName,
            SellerRating = rating,
            Verified = ad.SellerVerified,
            City = cityLine,
            Photo = ad.Photo,
            Link = BuildLink(ad.Link)
        });
    }

    public void RecordSkipped(Listing ad, string search, string reason, string category = "other")
    {
        lock (feedLock)
        {
            RolloverDayUnlocked();
            switch (category)
            {
                case "price": breakdown.Price++; break;
                case "skipWord": breakdown.SkipWord++; break;
                case "business": breakdown.Business++; break;
                default: breakdown.Outbid++; break;
            }
        }

        Add(new FeedEvent
        {
            Timestamp = DateTime.Now,
            Kind = "skipped",
            ListingId = ad.Id,
            Source = ad.Source,
            Title = ad.Title,
            PriceText = ad.PriceText,
            Price = ad.Price,
            Search = search,
            Reason = reason,
            Seller = ad.SellerName,
            Verified = ad.SellerVerified,
            City = ad.DistanceKm >= 0 ? $"{ad.City} ({ad.DistanceKm} km)" : ad.City,
            Photo = ad.Photo,
            Link = BuildLink(ad.Link)
        });
    }

    public SkipBreakdown GetSkipBreakdown()
    {
        lock (feedLock)
        {
            RolloverDayUnlocked();
            return new SkipBreakdown
            {
                Price = breakdown.Price,
                SkipWord = breakdown.SkipWord,
                Business = breakdown.Business,
                Outbid = breakdown.Outbid
            };
        }
    }

    public List<FeedEvent> GetRecent(string? kind = null, int limit = 80)
    {
        lock (feedLock)
        {
            IEnumerable<FeedEvent> query = events;
            if (!string.IsNullOrWhiteSpace(kind) && kind != "all")
            {
                query = query.Where(e => e.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
            }

            return query.Take(limit).ToList();
        }
    }

    private void Add(FeedEvent item)
    {
        lock (feedLock)
        {
            events.AddFirst(item);
            while (events.Count > MaxEvents)
            {
                events.RemoveLast();
            }
        }
    }

    private void RolloverDayUnlocked()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        if (today != breakdownDate)
        {
            breakdownDate = today;
            breakdown = new SkipBreakdown();
        }
    }

    private static string BuildLink(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return "";
        }

        return link.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? link
            : "https://www.marktplaats.nl" + link;
    }
}

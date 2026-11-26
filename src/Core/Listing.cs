using System.Globalization;
using System.Text.Json;

namespace MarktplaatsNotifier;

public class Listing
{
    public string Source { get; set; } = "Marktplaats";
    public string Title { get; set; } = "";
    public string Id { get; set; } = "";
    public string PhotoHash { get; set; } = "";
    public string PriceText { get; set; } = "";
    public double Price { get; set; } = 0;
    public string City { get; set; } = "";
    public int DistanceKm { get; set; } = -1;
    public bool CanShip { get; set; } = false;
    public string Date { get; set; } = "";
    public string Link { get; set; } = "";
    public string Description { get; set; } = "";
    public string Photo { get; set; } = "";
    public List<string> PhotoUrls { get; set; } = new List<string>();
    public bool IsBumped { get; set; } = false;
    public string Condition { get; set; } = "";
    public string Delivery { get; set; } = "";
    public string SellerName { get; set; } = "";
    public bool SellerVerified { get; set; } = false;
    public int PhotoCount { get; set; } = 0;
    public string MatchedSearch { get; set; } = "";
    public string PriceType { get; set; } = "";
    public int Views { get; set; } = -1;
    public int Saves { get; set; } = -1;
    public DateTime PostedAt { get; set; } = DateTime.MinValue;
    public int BidCount { get; set; } = 0;
    public double HighestBid { get; set; } = 0;
    public int SellerYears { get; set; } = -1;
    public double SellerRating { get; set; } = -1;
    public int SellerReviewCount { get; set; } = -1;
    public string SellerType { get; set; } = "";
    public bool HasWebsiteLink { get; set; } = false;
    public bool LikelyBusiness { get; set; } = false;
    public double OldPrice { get; set; } = 0;
    public double MentionedPrice { get; set; } = 0;
    public string Hint { get; set; } = "";
    public string HintQuestion { get; set; } = "";
    public double TypicalPrice { get; set; } = 0;
    public int TypicalCount { get; set; } = 0;
    public string RepostInfo { get; set; } = "";
    public int CategoryId { get; set; } = 0;
    public string CategoryName { get; set; } = "";
    public string AiVerdict { get; set; } = "";
    public string AiReason { get; set; } = "";
    public string AiResale { get; set; } = "";
    public string AiMaxBid { get; set; } = "";

    public static string FormatEuro(double amount)
    {
        long cents = (long)Math.Round(amount * 100);
        if (cents % 100 == 0)
        {
            return "€" + (cents / 100).ToString(CultureInfo.InvariantCulture);
        }

        return "€" + (cents / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
    }

    public static Listing FromJson(JsonElement json)
    {
        Listing ad = new Listing();

        if (json.TryGetProperty("itemId", out JsonElement idElement))
        {
            ad.Id = idElement.GetString() ?? "";
        }

        if (json.TryGetProperty("title", out JsonElement titleElement))
        {
            ad.Title = titleElement.GetString() ?? "";
        }

        if (json.TryGetProperty("description", out JsonElement descriptionElement))
        {
            ad.Description = descriptionElement.GetString() ?? "";
        }

        if (json.TryGetProperty("categoryId", out JsonElement categoryElement) &&
            categoryElement.ValueKind == JsonValueKind.Number &&
            categoryElement.TryGetInt32(out int categoryId))
        {
            ad.CategoryId = categoryId;
        }

        if (json.TryGetProperty("priorityProduct", out JsonElement priorityElement))
        {
            string priority = priorityElement.GetString() ?? "NONE";
            ad.IsBumped = priority != "NONE";
        }

        if (json.TryGetProperty("traits", out JsonElement traits) && traits.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement trait in traits.EnumerateArray())
            {
                if (trait.GetString() == "URL")
                {
                    ad.HasWebsiteLink = true;
                }
            }
        }

        if (json.TryGetProperty("priceInfo", out JsonElement priceInfo) && priceInfo.ValueKind == JsonValueKind.Object)
        {
            long priceCents = 0;
            if (priceInfo.TryGetProperty("priceCents", out JsonElement priceCentsElement) &&
                priceCentsElement.ValueKind == JsonValueKind.Number &&
                priceCentsElement.TryGetInt64(out long parsedCents))
            {
                priceCents = parsedCents;
            }

            ad.Price = priceCents / 100.0;
            string priceType = priceInfo.TryGetProperty("priceType", out JsonElement priceTypeElement)
                ? priceTypeElement.GetString() ?? ""
                : "";
            ad.PriceType = priceType;
            ad.PriceText = FormatEuro(ad.Price);

            if (priceType == "FAST_BID")
            {
                ad.PriceText = "Bieden";
            }
            else if (priceType == "MIN_BID" && ad.Price == 0)
            {
                ad.PriceText = "Bieden";
            }
            else if (priceType == "MIN_BID")
            {
                ad.PriceText = $"Bieden vanaf {FormatEuro(ad.Price)}";
            }
            else if (priceType == "FREE")
            {
                ad.PriceText = "Gratis";
            }
            else if (priceType == "SEE_DESCRIPTION")
            {
                ad.PriceText = "Zie omschrijving";
            }
        }

        ad.City = "Onbekend";
        if (json.TryGetProperty("location", out JsonElement location) && location.ValueKind == JsonValueKind.Object)
        {
            if (location.TryGetProperty("distanceMeters", out JsonElement distanceElement) &&
                distanceElement.ValueKind == JsonValueKind.Number &&
                distanceElement.TryGetInt32(out int distanceMeters))
            {
                ad.DistanceKm = distanceMeters / 1000;
            }

            if (location.TryGetProperty("cityName", out JsonElement cityElement))
            {
                string? city = cityElement.GetString();
                if (!string.IsNullOrWhiteSpace(city))
                {
                    ad.City = city;
                }
            }
        }

        if (json.TryGetProperty("sellerInformation", out JsonElement seller) && seller.ValueKind == JsonValueKind.Object)
        {
            if (seller.TryGetProperty("sellerName", out JsonElement nameElement))
            {
                ad.SellerName = nameElement.GetString() ?? "";
            }

            if (seller.TryGetProperty("isVerified", out JsonElement verifiedElement) &&
                (verifiedElement.ValueKind == JsonValueKind.True || verifiedElement.ValueKind == JsonValueKind.False))
            {
                ad.SellerVerified = verifiedElement.GetBoolean();
            }
        }

        if (json.TryGetProperty("date", out JsonElement dateElement))
        {
            ad.Date = dateElement.GetString() ?? "";
        }

        if (json.TryGetProperty("vipUrl", out JsonElement vipUrlElement))
        {
            ad.Link = vipUrlElement.GetString() ?? "";
        }

        if (json.TryGetProperty("attributes", out JsonElement attributes) && attributes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement attribute in attributes.EnumerateArray())
            {
                string key = attribute.TryGetProperty("key", out JsonElement keyEl) ? keyEl.GetString() ?? "" : "";
                string value = attribute.TryGetProperty("value", out JsonElement valEl) ? valEl.GetString() ?? "" : "";
                if (key == "delivery")
                {
                    ad.Delivery = value;
                    if (value.Contains("Verzenden", StringComparison.OrdinalIgnoreCase))
                    {
                        ad.CanShip = true;
                    }
                }
                else if (key == "condition")
                {
                    ad.Condition = value;
                }
            }
        }

        if (json.TryGetProperty("imageUrls", out JsonElement images) &&
            images.ValueKind == JsonValueKind.Array &&
            images.GetArrayLength() > 0)
        {
            ad.PhotoCount = images.GetArrayLength();
            int limit = Math.Min(3, ad.PhotoCount);
            for (int i = 0; i < limit; i++)
            {
                string rawPhoto = images[i].GetString() ?? "";
                if (rawPhoto != "")
                {
                    string normalized = rawPhoto.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? rawPhoto
                        : "https:" + rawPhoto;
                    normalized = normalized.Replace("$_82", "$_85");
                    ad.PhotoUrls.Add(normalized);
                }
            }

            if (ad.PhotoUrls.Count > 0)
            {
                ad.Photo = ad.PhotoUrls[0];
            }
        }

        return ad;
    }
}

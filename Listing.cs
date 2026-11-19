using System.Text.Json;


public class Listing
{
    public string Title { get; set; } = "";
    public string Id { get; set; } = "";
    public string PriceText { get; set; } = "";
    public double Price { get; set; } = 0;
    public string City { get; set; } = "";
    public int DistanceKm { get; set; } = -1;
    public bool CanShip { get; set; } = false;
    public string Date { get; set; } = "";
    public string Link { get; set; } = "";
    public string Description { get; set; } = "";
    public string Photo { get; set; } = "";

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
    
    public string SellerType { get; set; } = "";
    
    public bool HasWebsiteLink { get; set; } = false;

    public bool LikelyBusiness { get; set; } = false;
    
    public double OldPrice { get; set; } = 0;

    public string Hint { get; set; } = "";

    public string HintQuestion { get; set; } = "";
    
    public double TypicalPrice { get; set; } = 0;

    public int TypicalCount { get; set; } = 0;

    public string RepostInfo { get; set; } = "";
    
    public int CategoryId { get; set; } = 0;

    public string CategoryName { get; set; } = "";

    public static Listing FromJson(JsonElement json)
    {
        Listing ad = new Listing(); // grab a blank form

        ad.Id = json.GetProperty("itemId").GetString() ?? "";
        ad.Title = json.GetProperty("title").GetString() ?? "";

        if (json.TryGetProperty("description", out JsonElement descriptionElement))
        {
            ad.Description = descriptionElement.GetString() ?? "";
        }
        
        if (json.TryGetProperty("categoryId", out JsonElement categoryElement))
        {
            ad.CategoryId = categoryElement.GetInt32();
        }

        if (json.TryGetProperty("priorityProduct", out JsonElement priorityElement))
        {
            ad.IsBumped = priorityElement.GetString() != "NONE";
        }
        
        if (json.TryGetProperty("traits", out JsonElement traits))
        {
            foreach (JsonElement trait in traits.EnumerateArray())
            {
                if (trait.GetString() == "URL")
                {
                    ad.HasWebsiteLink = true;
                }
            }
        }


        int priceCents = json.GetProperty("priceInfo").GetProperty("priceCents").GetInt32();
        ad.Price = priceCents / 100.0;
        string priceType = json.GetProperty("priceInfo").GetProperty("priceType").GetString();
        ad.PriceType = priceType;
        ad.PriceText = $"€{ad.Price}";
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
            ad.PriceText = $"Bieden vanaf €{ad.Price}";
        }
        else if (priceType == "FREE")
        {
            ad.PriceText = "Gratis";
        }
        else if (priceType == "SEE_DESCRIPTION")
        {
            ad.PriceText = "Zie omschrijving";
        }


        ad.City = "Onbekend";
        JsonElement location = json.GetProperty("location");
        if (location.TryGetProperty("distanceMeters", out JsonElement distanceElement))
        {
            ad.DistanceKm = distanceElement.GetInt32() / 1000;
        }

        if (location.TryGetProperty("cityName", out JsonElement cityElement))
        {
            ad.City = cityElement.GetString();
        }
        if (json.TryGetProperty("sellerInformation", out JsonElement seller))
        {
            if (seller.TryGetProperty("sellerName", out JsonElement nameElement))
            {
                ad.SellerName = nameElement.GetString() ?? "";
            }

            if (seller.TryGetProperty("isVerified", out JsonElement verifiedElement))
            {
                ad.SellerVerified = verifiedElement.GetBoolean();
            }
        }


        if (json.TryGetProperty("date", out JsonElement dateElement))
        {
            ad.Date = dateElement.GetString();
        }

        ad.Link = json.GetProperty("vipUrl").GetString();
        if (json.TryGetProperty("attributes", out JsonElement attributes))
        {
            foreach (JsonElement attribute in attributes.EnumerateArray())
            {
                string key = attribute.GetProperty("key").GetString();
                string value = attribute.GetProperty("value").GetString();
                if (key == "delivery" && value.Contains("Verzenden"))
                {
                    ad.CanShip = true;
                }
                if (key == "condition")
                {
                    ad.Condition = value;
                }

                if (key == "delivery")
                {
                    ad.Delivery = value;
                }
            }
            
        }


        if (json.TryGetProperty("imageUrls", out JsonElement images) && images.GetArrayLength() > 0)
        {
            ad.Photo = "https:" + images[0].GetString();
            ad.Photo = ad.Photo.Replace("$_82", "$_85");
            ad.PhotoCount = images.GetArrayLength();
        }


        return ad;
    }
}
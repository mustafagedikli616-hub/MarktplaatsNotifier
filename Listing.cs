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

public string PriceType { get; set; } = "";
    public static Listing FromJson(JsonElement json)
    {
        Listing ad = new Listing(); // grab a blank form

        ad.Id = json.GetProperty("itemId").GetString();
        ad.Title = json.GetProperty("title").GetString();

        if (json.TryGetProperty("description", out JsonElement descriptionElement))
        {
            ad.Description = descriptionElement.GetString() ?? "";
        }


        int priceCents = json.GetProperty("priceInfo").GetProperty("priceCents").GetInt32();
        ad.Price = priceCents / 100.0;
        string priceType = json.GetProperty("priceInfo").GetProperty("priceType").GetString();
        ad.PriceType = priceType;
        ad.PriceText = $"€{ad.Price}";
        if (priceType == "FAST_BID")
        {
            ad.PriceText= "Bieden";
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
            }
        }

        
        if (json.TryGetProperty("imageUrls", out JsonElement images) && images.GetArrayLength() > 0)
        {
            ad.Photo = "https:" + images[0].GetString();
            ad.Photo = ad.Photo.Replace("$_82", "$_85");
        }


        return ad;
    }
}
// See https://aka.ms/new-console-template for more information

using System.ComponentModel;
using System.Text.Json;

HttpClient client = new HttpClient();
client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
string[] secrets = File.ReadAllLines("secrets.txt");
string token = secrets[0];
string chatId = secrets[1];
Dictionary<string, int> searches = new Dictionary<string, int>
{
    { "defect monitor", 60 },
    { "monitor geen beeld", 60 },
    { "monitor kapot", 60 },
    { "4k 144hz monitor", 120 },
    { "1440p 144hz monitor", 80 },
    { "ultrawide monitor", 100 },
    { "pc defect", 100 },
    { "game pc defect", 150 },
    { "gaming pc moet weg", 150 },
    { "pc start niet op", 100 },
    { "videokaart defect", 60 },
    { "rtx 3060", 110 },
    { "rx 6600", 90 },
    { "ddr4 32gb", 70 },
    { "moederbord b550", 45 },
    { "gaming laptop defect", 150 }
};

HashSet<string> seenIds = new HashSet<string>();
if (File.Exists("seen.txt"))
{
    string[] savedIds = File.ReadAllLines("seen.txt");
    foreach (string id in savedIds)
    {
        seenIds.Add(id);
    }
}

bool firstRound = !File.Exists("seen.txt");
while (true)
{
    Console.WriteLine("Checking...");
    foreach (KeyValuePair<string, int> pair in searches)
    {
        string search = pair.Key;
        int maxPrice = pair.Value;
        try
        {
            string url = $"https://www.marktplaats.nl/lrp/api/search?query={Uri.EscapeDataString(search)}&limit=30";
            string json = await client.GetStringAsync(url);
            JsonDocument doc = JsonDocument.Parse(json);
            JsonElement listings = doc.RootElement.GetProperty("listings");

            foreach (JsonElement listing in listings.EnumerateArray())

            {
                string itemId = listing.GetProperty("itemId").GetString();
                if (seenIds.Contains(itemId))
                {
                    continue;
                }
                else
                {
                    seenIds.Add(itemId);
                }

                File.AppendAllText("seen.txt", itemId + "\n");
                if (firstRound)
                {
                    continue;
                }

                string title = listing.GetProperty("title").GetString();
                int priceCents = listing.GetProperty("priceInfo").GetProperty("priceCents").GetInt32();
                double price = priceCents / 100.0;
                string priceType = listing.GetProperty("priceInfo").GetProperty("priceType").GetString();

                string priceText = $"€{price}";
                if (priceType == "FAST_BID")
                {
                    priceText = "Bieden";
                }
                else if (priceType == "MIN_BID")
                {
                    priceText = $"Bieden vanaf €{price}";
                }
                else if (priceType == "FREE")
                {
                    priceText = "Gratis";
                }
                else if (priceType == "SEE_DESCRIPTION")
                {
                    priceText = "Zie omschrijving";
                }

                if (price > maxPrice)
                {
                    continue;
                }

                string city = "Onbekend";
                JsonElement location = listing.GetProperty("location");

                if (location.TryGetProperty("cityName", out JsonElement cityElement))
                {
                    city = cityElement.GetString();
                }

                string link = listing.GetProperty("vipUrl").GetString();
                bool canShip = false;
                if (listing.TryGetProperty("attributes", out JsonElement attributes))
                {
                    foreach (JsonElement attribute in attributes.EnumerateArray())
                    {
                        string key = attribute.GetProperty("key").GetString();
                        string value = attribute.GetProperty("value").GetString();
                        if (key == "delivery" && value.Contains("Verzenden"))
                        {
                            canShip = true;
                        }
                    }
                }


                if (canShip == false)
                {
                    continue;
                }

                string photo = "";
                if (listing.TryGetProperty("imageUrls", out JsonElement images) && images.GetArrayLength() > 0)
                {
                    photo = "https:" + images[0].GetString();
                    photo = photo.Replace("$_82", "$_85");
                }

                string safeTitle = System.Net.WebUtility.HtmlEncode(title);
                string message =
                    $"<b>{safeTitle}</b>\n💶 {priceText}\n📍 {city}\n\n🔗 <a href=\"https://www.marktplaats.nl{link}\">Bekijk advertentie</a>";
                string encoded = Uri.EscapeDataString(message);
                if (photo != "")
                {
                    string encodedPhoto = Uri.EscapeDataString(photo);
                    await client.GetStringAsync(
                        $"https://api.telegram.org/bot{token}/sendPhoto?chat_id={chatId}&photo={encodedPhoto}&caption={encoded}&parse_mode=HTML");
                }
                else
                {
                    await client.GetStringAsync(
                        $"https://api.telegram.org/bot{token}/sendMessage?chat_id={chatId}&text={encoded}&parse_mode=HTML");
                }

                await Task.Delay(1000);
                Console.WriteLine(message);
                Console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error with '{search}': {ex.Message}");
        }

        await Task.Delay(2000);
    }

    firstRound = false;
    await Task.Delay(30000);
}

using System.Text.Json;
AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    Exception error = (Exception)e.ExceptionObject;
    Console.WriteLine("Something went wrong: " + error.Message);
    Console.WriteLine("Press Enter to close.");
    Console.ReadLine();
};

HttpClient client = new HttpClient();
client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

Dictionary<string, int> searches = new Dictionary<string, int>();
if (!File.Exists("searches.txt"))
{
    Console.WriteLine("Can't find searches.txt. Put it in the same folder as the app.");
    Console.WriteLine("Press Enter to close.");
    Console.ReadLine();
    return;
}
Dictionary<string, string> categories = new Dictionary<string, string>(); 

string[] lines = File.ReadAllLines("searches.txt");
if (!File.Exists("secrets.txt"))
{
    string newToken = "";
    while (newToken == "")
    {
        Console.Write("Paste your bot token: ");
        string input = Console.ReadLine().Trim();

        try
        {
            await client.GetStringAsync($"https://api.telegram.org/bot{input}/getMe");
            newToken = input;
        }
        catch
        {
            Console.WriteLine("That token doesn't work (or there's no internet). Check it and try again.");
        }
    }

    Console.WriteLine("Now open your bot in Telegram and send it any message (like 'hi').");
    Console.WriteLine("Waiting for your message...");

    string newChatId = "";
    while (newChatId == "")
    {
        string updatesJson = await client.GetStringAsync($"https://api.telegram.org/bot{newToken}/getUpdates");
        JsonDocument updates = JsonDocument.Parse(updatesJson);

        foreach (JsonElement update in updates.RootElement.GetProperty("result").EnumerateArray())
        {
            if (update.TryGetProperty("message", out JsonElement msg))
            {
                newChatId = msg.GetProperty("chat").GetProperty("id").GetInt64().ToString();
            }
        }

        await Task.Delay(2000);
    }

    File.WriteAllLines("secrets.txt", new string[] { newToken, newChatId });
    Console.Clear();
    Console.WriteLine("Setup done! You won't be asked again.");
}
string[] secrets = File.ReadAllLines("secrets.txt");
string token = secrets[0];
string chatId = secrets[1];

foreach (string line in lines)
{
    if (line.Trim() == "" || line.StartsWith("#"))
    {
        continue;
    }

    string[] parts = line.Split(';');
    string searchText = parts[0].Trim();
    int limit = int.Parse(parts[1].Trim());
    searches.Add(searchText, limit);
    string category = "";
    if (parts.Length > 2)
    {
        category = parts[2].Trim();
    }
    categories.Add(searchText, category);
}

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
            if (categories[search] != "")
            {
                url += $"&l1CategoryId={categories[search]}";
            }
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
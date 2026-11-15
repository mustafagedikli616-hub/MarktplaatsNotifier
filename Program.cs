
using System.Reflection.Emit;
using System.Text.Json;
AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    Exception error = (Exception)e.ExceptionObject;
    Console.WriteLine("Something went wrong: " + error.Message);
    Console.WriteLine("Press Enter to close.");
    Console.ReadLine();
};

void StopWithError(string message)
{
    Console.WriteLine(message);
    Console.WriteLine("Press Enter to close.");
    Console.ReadLine();
    Environment.Exit(1);
}

HttpClient client = new HttpClient();
client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

Dictionary<string, int> searches = new Dictionary<string, int>();
if (!File.Exists("searches.txt"))
{
    File.WriteAllText("searches.txt", "# One search per line: search words;max price;category\n# Example: defect monitor;60;322\n");
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
        using JsonDocument updates = JsonDocument.Parse(updatesJson);

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
if (secrets.Length < 2)
{
    StopWithError("secrets.txt is damaged. Delete it and start the app again to redo the setup.");
}
string token = secrets[0];
string chatId = secrets[1];

int lineNumber = 0;
foreach (string line in lines)
{
    lineNumber++;
    if (line.Trim() == "" || line.Trim().StartsWith("#"))
    {
        continue;
    }

    string[] parts = line.Split(';');
    if (parts.Length < 2 || parts[0].Trim() == "")
    {
        StopWithError($"Line {lineNumber} in searches.txt is wrong: \"{line}\"\nUse: search words;max price (for example: defect monitor;60)");
    }

    string searchText = parts[0].Trim();

    if (!int.TryParse(parts[1].Trim(), out int limit))
    {
        StopWithError($"Line {lineNumber} in searches.txt: \"{parts[1].Trim()}\" is not a valid price. Use whole euros, like 60.");
    }

    string category = "";
    if (parts.Length > 2)
    {
        category = parts[2].Trim();
    }

    if (category != "" && !int.TryParse(category, out _))
    {
        StopWithError($"Line {lineNumber} in searches.txt: \"{category}\" is not a valid category. Use a number like 322, or leave it out.");
    }

    if (searches.ContainsKey(searchText))
    {
        StopWithError($"Line {lineNumber} in searches.txt: \"{searchText}\" is in the file twice. Remove one of them.");
    }

    searches.Add(searchText, limit);
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


string[] skipWords = { "gebarsten", "barst", "strepen", "lijnen", "waterschade" };


string FindBadFault(string text)
{
  
    string[] pieces = text.ToLower().Split('.', ',', '!', '?', '\n');

    foreach (string piece in pieces)
    {
      
        if (piece.Contains("geen") || piece.Contains("zonder"))
        {
            continue;
        }

        foreach (string word in skipWords)
        {
            if (piece.Contains(word))
            {
                return word;
            }
        }
    }
    return "";
}


long lastUpdateId = 0;
HashSet<string> quietSearches = new HashSet<string>();

async Task SendText(string text, string keyboard = "")
{
    string url = $"https://api.telegram.org/bot{token}/sendMessage?chat_id={chatId}&text={Uri.EscapeDataString(text)}";
    if (keyboard != "")
    {
        url += $"&reply_markup={Uri.EscapeDataString(keyboard)}";
    }
    await client.GetStringAsync(url);
}


string addStep = "";       
string pendingSearch = "";
int pendingPrice = 0;


Dictionary<string, string> categoryButtons = new Dictionary<string, string>
{
    { "💻 Computers en Software", "322" },
    { "📺 Audio, Tv en Foto", "31" },
    { "🎮 Spelcomputers en Games", "356" },
    { "📱 Telecommunicatie", "820" },
    { "🚗 Auto-onderdelen", "2600" },
    { "🚲 Fietsen en Brommers", "445" },
    { "🧺 Witgoed en Apparatuur", "537" },
    { "🏠 Huis en Inrichting", "504" },
    { "🔎 All of Marktplaats", "" }
};


try
{
    
    List<object> commands = new List<object>
    {
        new { command = "add", description = "Add a search" },
        new { command = "list", description = "Show your searches" },
        new { command = "remove", description = "Remove a search" },
        new { command = "cancel", description = "Stop adding a search" },
        new { command = "help", description = "Show all commands" }
    };
    string commandsJson = JsonSerializer.Serialize(commands);
    await client.GetStringAsync($"https://api.telegram.org/bot{token}/setMyCommands?commands={Uri.EscapeDataString(commandsJson)}");

    if (searches.Count == 0)
    {
        await SendText("The bot is running, Send /add to add your first search.");
    }
    else
    {
        await SendText($"✅ Bot started, watching {searches.Count} searches.");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Couldn't reach Telegram at startup: {ex.Message}");
}

List<string> labels = categoryButtons.Keys.ToList();
List<List<object>> rows = new List<List<object>>();
for (int i = 0; i < labels.Count; i += 2)
{
    List<object> row = new List<object>();
    row.Add(new { text = labels[i] });
    if (i + 1 < labels.Count)
    {
        row.Add(new { text = labels[i + 1] });
    }
    rows.Add(row);
}
string categoryKeyboard = JsonSerializer.Serialize(new { keyboard = rows, one_time_keyboard = true, resize_keyboard = true });
string removeKeyboard = "{\"remove_keyboard\":true}";

async Task StartAdd(string searchText)
{
    if (searches.ContainsKey(searchText))
    {
        addStep = "";
        await SendText($"⚠️ \"{searchText}\" is already in your list.");
        return;
    }

    pendingSearch = searchText;
    addStep = "price";
    await SendText($"💶 What's the max price for \"{searchText}\"?\nJust the number, like 140. (or /cancel)");
}

async Task HandleAddStep(string text)
{
    if (addStep == "search")
    {
        await StartAdd(text);
    }
    else if (addStep == "price")
    {
        string priceInput = text.Replace("€", "").Trim();
        if (!int.TryParse(priceInput, out int price))
        {
            await SendText("❌ Send just a number, like 140. (or /cancel)");
            return;
        }

        pendingPrice = price;
        addStep = "category";
        await SendText("📂 Which category? Tap a button, or send a category number.", categoryKeyboard);
    }
    else if (addStep == "category")
    {
        string category;
        if (categoryButtons.ContainsKey(text))
        {
            category = categoryButtons[text];
        }
        else if (int.TryParse(text, out _))
        {
            category = text;
        }
        else
        {
            await SendText("❌ Tap one of the buttons, or send a category number. (or /cancel)", categoryKeyboard);
            return;
        }

        string line = $"{pendingSearch};{pendingPrice}";
        if (category != "")
        {
            line += $";{category}";
        }

        addStep = "";
        await SendText(AddSearch(line), removeKeyboard);
    }
}

string AddSearch(string line)
{
    string[] parts = line.Split(';');
    if (parts.Length < 2 || parts[0].Trim() == "")
    {
        return "❌ Use: /add search words;max price;category (category is optional)\nExample: /add rtx 3070;140;322";
    }

    string searchText = parts[0].Trim();

    if (!int.TryParse(parts[1].Trim(), out int limit))
    {
        return $"❌ \"{parts[1].Trim()}\" is not a valid price. Use whole euros, like 140.";
    }

    string category = "";
    if (parts.Length > 2)
    {
        category = parts[2].Trim();
    }

    if (category != "" && !int.TryParse(category, out _))
    {
        return $"❌ \"{category}\" is not a valid category. Use a number like 322, or leave it out.";
    }

    if (searches.ContainsKey(searchText))
    {
        return $"⚠️ \"{searchText}\" is already in your list.";
    }

    searches.Add(searchText, limit);
    categories.Add(searchText, category);
    quietSearches.Add(searchText);

    string newLine = $"{searchText};{limit}";
    if (category != "")
    {
        newLine += $";{category}";
    }

    string fileText = File.ReadAllText("searches.txt");
    if (fileText != "" && !fileText.EndsWith("\n"))
    {
        File.AppendAllText("searches.txt", "\n");
    }
    File.AppendAllText("searches.txt", newLine + "\n");

    return $"✅ Added: {searchText} (max €{limit})";
}

string RemoveSearch(string searchText)
{
    if (!searches.ContainsKey(searchText))
    {
        return $"❌ \"{searchText}\" is not in your list. Send /list to see your searches.";
    }

    searches.Remove(searchText);
    categories.Remove(searchText);

    List<string> keptLines = new List<string>();
    foreach (string fileLine in File.ReadAllLines("searches.txt"))
    {
        bool isComment = fileLine.Trim().StartsWith("#");
        string lineSearch = fileLine.Split(';')[0].Trim();
        if (!isComment && lineSearch == searchText)
        {
            continue;
        }
        keptLines.Add(fileLine);
    }
    File.WriteAllLines("searches.txt", keptLines);

    return $"🗑️ Removed: {searchText}";
}

string ListSearches()
{
    string text = $"📋 Your searches ({searches.Count}):\n";
    foreach (KeyValuePair<string, int> pair in searches)
    {
        text += $"\n• {pair.Key} (max €{pair.Value})";
    }
    return text;
}

string helpText = "🤖 Commands:\n\n" +
                  "/add - add a search, the bot asks you the price and category\n" +
                  "   quick version: /add rtx 3070;140;322\n\n" +
                  "/remove search\n   example: /remove rtx 3070\n\n" +
                  "/list - show all your searches\n" +
                  "/cancel - stop adding a search\n" +
                  "/help - show this message";

async Task CheckCommands()
{
    try
    {
        string updatesJson = await client.GetStringAsync($"https://api.telegram.org/bot{token}/getUpdates?offset={lastUpdateId + 1}");
        using JsonDocument updates = JsonDocument.Parse(updatesJson);

        foreach (JsonElement update in updates.RootElement.GetProperty("result").EnumerateArray())
        {
            lastUpdateId = update.GetProperty("update_id").GetInt64();

            if (!update.TryGetProperty("message", out JsonElement msg))
            {
                continue;
            }
            if (!msg.TryGetProperty("text", out JsonElement textElement))
            {
                continue;
            }

            // Only YOU can control the bot, messages from other people are ignored
            string fromChat = msg.GetProperty("chat").GetProperty("id").GetInt64().ToString();
            if (fromChat != chatId)
            {
                continue;
            }

            string text = textElement.GetString().Trim();

            // Any command stops a half-finished /add
            if (text.StartsWith("/"))
            {
                addStep = "";
            }
            else if (addStep != "")
            {
                await HandleAddStep(text);
                continue;
            }

            if (text == "/cancel")
            {
                await SendText("👍 Cancelled.", removeKeyboard);
            }
            else if (text == "/add" || text.StartsWith("/add "))
            {
                string rest = text.Substring(4).Trim();
                if (rest.Contains(";"))
                {
                    // Quick version: /add rtx 3070;140;322
                    await SendText(AddSearch(rest));
                }
                else if (rest == "")
                {
                    addStep = "search";
                    await SendText("🔎 What do you want to search for? (or /cancel)");
                }
                else
                {
                    await StartAdd(rest);
                }
            }
            else if (text == "/remove")
            {
                await SendText("Type the search after /remove, like: /remove rtx 3070\n\n" + ListSearches());
            }
            else if (text.StartsWith("/remove "))
            {
                await SendText(RemoveSearch(text.Substring(8).Trim()));
            }
            else if (text == "/list")
            {
                await SendText(ListSearches());
            }
            else if (text == "/help" || text == "/start")
            {
                await SendText(helpText);
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error while checking commands: {ex.Message}");
    }
}

bool firstRound = true;
while (true)
{
    Console.WriteLine("Checking...");
    foreach (KeyValuePair<string, int> pair in searches.ToList())
    {
        string search = pair.Key;
        int maxPrice = pair.Value;


        if (!searches.ContainsKey(search))
        {
            continue;
        }


        bool quiet = firstRound || quietSearches.Contains(search);

        try
        {
            string url = $"https://www.marktplaats.nl/lrp/api/search?query={Uri.EscapeDataString(search)}&limit=30&sortBy=SORT_INDEX&sortOrder=DECREASING";
            if (categories[search] != "")
            {
                url += $"&l1CategoryId={categories[search]}";
            }
            string json = await client.GetStringAsync(url);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement listings = doc.RootElement.GetProperty("listings");

            foreach (JsonElement listing in listings.EnumerateArray())

            {
                string itemId = listing.GetProperty("itemId").GetString();

      
                if (itemId.StartsWith("a"))
                {
                    continue;
                }
                if (seenIds.Contains(itemId))
                {
                    continue;
                }
                else
                {
                    seenIds.Add(itemId);
                }

                File.AppendAllText("seen.txt", itemId + "\n");
                if (quiet)
                {
                    continue;
                }

                string title = listing.GetProperty("title").GetString();
                string lowerTitle = title.ToLower();
                if (lowerTitle.Contains("gezocht") || lowerTitle.Contains("reparatie") || lowerTitle.Contains("inkoop"))
                {
                    continue;
                }

  
                string description = "";
                if (listing.TryGetProperty("description", out JsonElement descriptionElement))
                {
                    description = descriptionElement.GetString() ?? "";
                }

                string badWord = FindBadFault(title + ". " + description);
                if (badWord != "")
                {
                    Console.WriteLine($"Skipped ({badWord}): {title}");
                    continue;
                }

                int priceCents = listing.GetProperty("priceInfo").GetProperty("priceCents").GetInt32();
                double price = priceCents / 100.0;
                string priceType = listing.GetProperty("priceInfo").GetProperty("priceType").GetString();

                string priceText = $"€{price}";
                if (priceType == "FAST_BID")
                {
                    priceText = "Bieden";
                }
                else if (priceType == "MIN_BID" && price == 0)
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
                

                string date = "";
                if (listing.TryGetProperty("date", out JsonElement dateElement))
                {
                    date = dateElement.GetString();
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
                    $"<b>{safeTitle}</b>\n💶 {priceText}\n📍 {city}\n📅 {date}\n\n🔗 <a href=\"https://www.marktplaats.nl{link}\">Bekijk advertentie</a>";
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

        quietSearches.Remove(search);
        await CheckCommands();
        await Task.Delay(2000);
    }

    firstRound = false;


    for (int i = 0; i < 15; i++)
    {
        await CheckCommands();
        await Task.Delay(2000);
    }
}
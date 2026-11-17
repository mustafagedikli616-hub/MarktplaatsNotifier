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

SearchList searchList = new SearchList();
string loadError = searchList.Load();
if (loadError != "")
{
    StopWithError(loadError);
}

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
Telegram telegram = new Telegram(client, token, chatId);


HashSet<string> seenIds = new HashSet<string>();
if (File.Exists("seen.txt"))
{
    string[] savedIds = File.ReadAllLines("seen.txt");
    foreach (string id in savedIds)
    {
        seenIds.Add(id);
    }
}


string postcode = "1055NE";
int pickupKm = 5;


long lastUpdateId = 0;


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
    await client.GetStringAsync(
        $"https://api.telegram.org/bot{token}/setMyCommands?commands={Uri.EscapeDataString(commandsJson)}");

    if (searchList.Prices.Count == 0)
    {
        await telegram.SendText("The bot is running, Send /add to add your first search.");
    }
    else
    {
        await telegram.SendText($"✅ Bot started, watching {searchList.Prices.Count} searches.");
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

string categoryKeyboard =
    JsonSerializer.Serialize(new { keyboard = rows, one_time_keyboard = true, resize_keyboard = true });
string removeKeyboard = "{\"remove_keyboard\":true}";

async Task StartAdd(string searchText)
{
    if (searchList.Prices.ContainsKey(searchText))
    {
        addStep = "";
        await telegram.SendText($"⚠️ \"{searchText}\" is already in your list.");
        return;
    }

    pendingSearch = searchText;
    addStep = "price";
    await telegram.SendText($"💶 What's the max price for \"{searchText}\"?\nJust the number, like 140. (or /cancel)");
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
            await telegram.SendText("❌ Send just a number, like 140. (or /cancel)");
            return;
        }

        pendingPrice = price;
        addStep = "category";
        await telegram.SendText("📂 Which category? Tap a button, or send a category number.", categoryKeyboard);
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
            await telegram.SendText("❌ Tap one of the buttons, or send a category number. (or /cancel)",
                categoryKeyboard);
            return;
        }

        string line = $"{pendingSearch};{pendingPrice}";
        if (category != "")
        {
            line += $";{category}";
        }

        addStep = "";
        await telegram.SendText(searchList.Add(line), removeKeyboard);
    }
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
        string updatesJson =
            await client.GetStringAsync($"https://api.telegram.org/bot{token}/getUpdates?offset={lastUpdateId + 1}");
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
                await telegram.SendText("👍 Cancelled.", removeKeyboard);
            }
            else if (text == "/add" || text.StartsWith("/add "))
            {
                string rest = text.Substring(4).Trim();
                if (rest.Contains(";"))
                {
                    // Quick version: /add rtx 3070;140;322
                    await telegram.SendText(searchList.Add(rest));
                }
                else if (rest == "")
                {
                    addStep = "search";
                    await telegram.SendText("🔎 What do you want to search for? (or /cancel)");
                }
                else
                {
                    await StartAdd(rest);
                }
            }
            else if (text == "/remove")
            {
                await telegram.SendText("Type the search after /remove, like: /remove rtx 3070\n\n" +
                                        searchList.ListText());
            }
            else if (text.StartsWith("/remove "))
            {
                await telegram.SendText(searchList.Remove(text.Substring(8).Trim()));
            }
            else if (text == "/list")
            {
                await telegram.SendText(searchList.ListText());
            }
            else if (text == "/help" || text == "/start")
            {
                await telegram.SendText(helpText);
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
    foreach (KeyValuePair<string, int> pair in searchList.Prices.ToList())
    {
        string search = pair.Key;
        int maxPrice = pair.Value;


        if (!searchList.Prices.ContainsKey(search))
        {
            continue;
        }


        bool quiet = firstRound || searchList.NewSearches.Contains(search);

        try
        {
            string url =
                $"https://www.marktplaats.nl/lrp/api/search?query={Uri.EscapeDataString(search)}&limit=30&sortBy=SORT_INDEX&sortOrder=DECREASING&postcode={postcode}";
            if (searchList.Categories[search] != "")
            {
                url += $"&l1CategoryId={searchList.Categories[search]}";
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

                Listing ad = Listing.FromJson(listing);


                if (Filters.IsNotForSale(ad.Title))
                {
                    continue;
                }


                string badWord = Filters.FindBadFault(ad.Title + ". " + ad.Description, searchList.SkipWords[search]);
                if (badWord != "")
                {
                    Console.WriteLine($"Skipped ({badWord}): {ad.Title}");
                    continue;
                }


                if (ad.Price > maxPrice)
                {
                    continue;
                }


                bool isNearby = ad.DistanceKm >= 0 && ad.DistanceKm <= pickupKm;
                if (!ad.CanShip && !isNearby)
                {
                    continue;
                }


                string alert = "";
                if (Filters.IsSuperDeal(ad, maxPrice))
                {
                    alert = "SUPER DEAL";
                }
                else if (searchList.UrgentSearches.Contains(search))
                {
                    alert = "URGENT";
                }

                await telegram.SendListing(ad, Filters.IsNight(), alert);
                await Task.Delay(1000);
                Console.WriteLine($"Sent: {ad.Title}");
                Console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error with '{search}': {ex.Message}");
        }

        searchList.NewSearches.Remove(search);
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
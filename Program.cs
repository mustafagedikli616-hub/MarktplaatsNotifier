using System.Text.Json;
using System.Text.RegularExpressions;
using MarktplaatsNotifier;
using System.Runtime.InteropServices;

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
client.DefaultRequestHeaders.Add("User-Agent",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36");
client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
client.DefaultRequestHeaders.Add("Accept-Language", "nl-NL,nl;q=0.9,en;q=0.8");

SearchList searchList = new SearchList();
string loadError = searchList.Load();
if (loadError != "")
{
    StopWithError(loadError);
}

RepairHints hints = new RepairHints();
hints.Load();
ListingHistory history = new ListingHistory();
history.Load();
using PosixSignalRegistration ctrlC = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => history.Save());
using PosixSignalRegistration stop = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => history.Save());

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
if (secrets.Length < 4)
{
    string newPostcode = "";
    while (newPostcode == "")
    {
        Console.Write("Your postcode (like 1234AB): ");
        string input = Console.ReadLine().Trim().ToUpper().Replace(" ", "");

        if (input.Length == 6 && int.TryParse(input.Substring(0, 4), out _) && char.IsLetter(input[4]) &&
            char.IsLetter(input[5]))
        {
            newPostcode = input;
        }
        else
        {
            Console.WriteLine("That's not a Dutch postcode. Use 4 numbers and 2 letters, like 1234AB.");
        }
    }

    int newPickupKm = -1;
    while (newPickupKm < 0)
    {
        Console.Write("How many km will you travel to pick something up? (0 = shipping only): ");
        if (int.TryParse(Console.ReadLine().Trim(), out int km) && km >= 0)
        {
            newPickupKm = km;
        }
        else
        {
            Console.WriteLine("Type a whole number, like 5.");
        }
    }

    File.WriteAllLines("secrets.txt", new string[] { token, chatId, newPostcode, newPickupKm.ToString() });
    secrets = File.ReadAllLines("secrets.txt");
}

string postcode = secrets[2];
int pickupKm = int.Parse(secrets[3]);
Telegram telegram = new Telegram(client, token, chatId);


HashSet<string> seenIds = new HashSet<string>();
if (File.Exists("seen.txt"))
{
    string[] savedIds = File.ReadAllLines("seen.txt");
    if (savedIds.Length > 50000)
    {
        savedIds = savedIds[^50000..];
        File.WriteAllLines("seen.txt", savedIds);
    }

    foreach (string id in savedIds)
    {
        seenIds.Add(id);
    }
}


long lastUpdateId = 0;
RequestLimiter limiter = new RequestLimiter();
SearchScheduler scheduler = new SearchScheduler();
bool paused = false;
DateTime skipDetailsUntil = DateTime.MinValue;
DateTime startedAt = DateTime.Now;
int checksDone = 0;
int listingsSent = 0;


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
        new { command = "pause", description = "Pause searching" },
        new { command = "resume", description = "Start searching again" },
        new { command = "status", description = "Show how the bot is doing" },
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
                  "/pause - pause searching\n" +
                  "/resume - start searching again\n" +
                  "/status - show how the bot is doing\n" +
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


            string fromChat = msg.GetProperty("chat").GetProperty("id").GetInt64().ToString();
            if (fromChat != chatId)
            {
                continue;
            }

            string text = textElement.GetString().Trim();


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
            else if (text == "/pause")
            {
                paused = true;
                await telegram.SendText("⏸️ Paused. Send /resume to start searching again.");
            }
            else if (text == "/resume")
            {
                paused = false;
                await telegram.SendText("▶️ Searching again.");
            }
            else if (text == "/status")
            {
                TimeSpan running = DateTime.Now - startedAt;
                string state = paused ? "⏸️ paused" : "▶️ running";
                await telegram.SendText(
                    $"🤖 Status: {state}\n" +
                    $"⏱️ Running for {(int)running.TotalHours} h {running.Minutes} min\n" +
                    $"🔎 {searchList.Prices.Count} searches, {checksDone} checks done\n" +
                    $"📨 {listingsSent} listings sent\n" +
                    $"🐢 {limiter.DelayMs} ms between requests");
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

int errorsInARow = 0;

bool IsNew(string itemId)
{
    if (seenIds.Contains(itemId))
    {
        return false;
    }

    seenIds.Add(itemId);
    File.AppendAllText("seen.txt", itemId + "\n");
    return true;
}


Dictionary<int, string> categoryNames = new Dictionary<int, string>();

async Task<List<Listing>> FetchListings(string url)
{
    string json = await client.GetStringAsync(url);
    if (errorsInARow >= 5)
    {
        try
        {
            await telegram.SendText("✅ Back to normal, searches work again.");
        }
        catch
        {
        }
    }

    errorsInARow = 0;
    limiter.Success();

    List<Listing> ads = new List<Listing>();
    using JsonDocument doc = JsonDocument.Parse(json);
    if (doc.RootElement.TryGetProperty("searchCategoryOptions", out JsonElement options))
    {
        foreach (JsonElement option in options.EnumerateArray())
        {
            categoryNames[option.GetProperty("id").GetInt32()] = option.GetProperty("fullName").GetString() ?? "";
        }
    }

    foreach (JsonElement listing in doc.RootElement.GetProperty("listings").EnumerateArray())
    {
        string itemId = listing.GetProperty("itemId").GetString();
        if (itemId.StartsWith("a"))
        {
            continue;
        }

        try
        {
            ads.Add(Listing.FromJson(listing));
        }
        catch
        {
        }
    }

    return ads;
}

string FindInText(string text, string pattern)
{
    Match match = Regex.Match(text, pattern);
    if (match.Success)
    {
        return match.Groups[1].Value;
    }

    return "";
}

async Task AddPageDetails(Listing ad)
{
    if (DateTime.Now < skipDetailsUntil)
    {
        return;
    }

    try
    {
        string html = await client.GetStringAsync("https://www.marktplaats.nl" + ad.Link);

        string stats = FindInText(html, """stats":\{([^}]*)\}""");
        string views = FindInText(stats, """viewCount":(\d+)""");
        string saves = FindInText(stats, """favoritedCount":(\d+)""");
        string since = FindInText(stats, """since":"([^"]+)""");
        string years = FindInText(html, """activeYears":(\d+)""");
        string bids = FindInText(html, """bidsInfo":\{.*?"bids":\[(.*?)\]""");
        ad.SellerType = FindInText(html, """sellerType":"([A-Z_]+)""");

        if (views != "")
        {
            ad.Views = int.Parse(views);
        }

        if (saves != "")
        {
            ad.Saves = int.Parse(saves);
        }

        if (since != "")
        {
            ad.PostedAt = DateTime.Parse(since);
        }

        if (years != "")
        {
            ad.SellerYears = int.Parse(years);
        }

        Match description =
            Regex.Match(html, """data-collapsable="description">(.*?)</div>""", RegexOptions.Singleline);
        if (description.Success)
        {
            string text = Regex.Replace(description.Groups[1].Value, "<[^>]+>", "\n");
            ad.Description = System.Net.WebUtility.HtmlDecode(text).Trim();
        }

        foreach (Match bid in Regex.Matches(bids, """value":(\d+)"""))
        {
            ad.BidCount++;
            double amount = int.Parse(bid.Groups[1].Value) / 100.0;
            if (amount > ad.HighestBid)
            {
                ad.HighestBid = amount;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Couldn't get extra info for '{ad.Title}': {ex.Message}");
        if (ex is HttpRequestException httpError &&
            (httpError.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
             httpError.StatusCode == System.Net.HttpStatusCode.Forbidden))
        {
            skipDetailsUntil = DateTime.Now.AddMinutes(30);
            Console.WriteLine("Listing pages are blocked: skipping extra info for 30 minutes.");
        }
    }
}

async Task<bool> TrySend(Listing ad, string search, string forcedAlert = "")
{
    if (!searchList.Prices.ContainsKey(search))
    {
        return false;
    }

    int maxPrice = searchList.Prices[search];

    if (Filters.IsNotForSale(ad.Title))
    {
        return false;
    }

    string badWord = Filters.FindBadFault(ad.Title + ". " + ad.Description, searchList.SkipWords[search]);
    if (badWord != "")
    {
        Console.WriteLine($"Skipped ({badWord}): {ad.Title}");
        return false;
    }

    if (ad.Price > maxPrice)
    {
        return false;
    }

    bool isNearby = ad.DistanceKm >= 0 && ad.DistanceKm <= pickupKm;
    if (!ad.CanShip && !isNearby)
    {
        return false;
    }

    string alert = "";
    if (forcedAlert != "")
    {
        alert = forcedAlert;
    }
    else if (Filters.IsSuperDeal(ad, maxPrice))
    {
        alert = "SUPER DEAL";
    }
    else if (searchList.UrgentSearches.Contains(search))
    {
        alert = "URGENT";
    }

    ad.MatchedSearch = search;
    ad.CategoryName = categoryNames.GetValueOrDefault(ad.CategoryId, "");
    await AddPageDetails(ad);

    string lateBadWord = Filters.FindBadFault(ad.Description, searchList.SkipWords[search]);
    if (lateBadWord != "")
    {
        Console.WriteLine($"Skipped ({lateBadWord}, in full description): {ad.Title}");
        return false;
    }

    if (ad.SellerType == "TRADER")
    {
        Console.WriteLine($"Skipped (business): {ad.Title}");
        return false;
    }

    ad.LikelyBusiness = BusinessFilter.Score(ad) >= 2;
    RepairHint? hint = hints.Find(ad.Title + ". " + ad.Description);
    if (hint != null)
    {
        ad.Hint = hint.Text;
        ad.HintQuestion = hint.Question;
    }

    if (ad.Price > 0)
    {
        ad.TypicalPrice = history.TypicalPrice(search, ad.CategoryId, out int count);
        ad.TypicalCount = count;
    }


    HistoryEntry? earlier = history.FindRepost(ad);
    if (earlier != null)
    {
        ad.RepostInfo =
            $"Posted before on {earlier.FirstSeen.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture)}";
        if (earlier.Price > 0)
        {
            ad.RepostInfo += $" for €{earlier.Price}";
        }
    }

    IsNew(ad.Id);
    await telegram.SendListing(ad, Filters.IsNight(), alert);
    await Task.Delay(1000);
    listingsSent++;
    Console.WriteLine($"Sent ({search}): {ad.Title}");
    Console.WriteLine();
    return true;
}

async Task HandleError(string what, Exception ex)
{
    {
        Console.WriteLine($"Error with '{what}': {ex.Message}");
        errorsInARow++;
        if (errorsInARow == 5)
        {
            try
            {
                await telegram.SendText(
                    $"⚠️ 5 errors in a row. Marktplaats might be blocking me, or the internet is down.\nLast error: {ex.Message}");
            }
            catch
            {
            }
        }

        if (ex is HttpRequestException httpError &&
            (httpError.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
             httpError.StatusCode == System.Net.HttpStatusCode.Forbidden))
        {
            limiter.Blocked();
        }
    }
}

Dictionary<string, long> newestIdPerSearch = new Dictionary<string, long>();

while (true)
{
    if (paused)
    {
        await CheckCommands();
        await Task.Delay(2000);
        continue;
    }


    string search = scheduler.NextDue(searchList.Prices.Keys);
    if (search == "")
    {
        await CheckCommands();
        await Task.Delay(1000);
        continue;
    }


    bool quiet = !newestIdPerSearch.ContainsKey(search);
    bool foundNew = false;

    try
    {
        string url =
            $"https://www.marktplaats.nl/lrp/api/search?query={Uri.EscapeDataString(search)}&limit=30&sortBy=SORT_INDEX&sortOrder=DECREASING&postcode={postcode}";
        if (searchList.Categories[search] != "")
        {
            url += $"&l1CategoryId={searchList.Categories[search]}";
        }

        List<Listing> ads = await FetchListings(url);
        checksDone++;

        long newest = newestIdPerSearch.GetValueOrDefault(search, 0);
        long highestThisCheck = newest;

        foreach (Listing ad in ads)
        {
            long.TryParse(ad.Id.Substring(1), out long idNumber);
            if (idNumber > highestThisCheck)
            {
                highestThisCheck = idNumber;
            }

            double oldPrice = history.Remember(ad, search);
            if (oldPrice > 0 && ad.Price > 0 && ad.Price < oldPrice)
            {
                ad.OldPrice = oldPrice;
                await TrySend(ad, search, "PRICE DROP");
                continue;
            }

            if (quiet || idNumber <= newest)
            {
                continue;
            }

            foundNew = true;

            await TrySend(ad, search);
        }

        newestIdPerSearch[search] = highestThisCheck;

        history.SaveIfNeeded();
    }
    catch (Exception ex)
    {
        await HandleError(search, ex);
    }

    scheduler.Done(search, foundNew, searchList.UrgentSearches.Contains(search), Filters.IsNight());
    await CheckCommands();
    await Task.Delay(limiter.DelayMs);
}
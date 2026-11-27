using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarktplaatsNotifier;

bool isHeadless = Console.IsInputRedirected;

AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    Exception error = (Exception)e.ExceptionObject;
    BotLog.Write("Something went wrong: " + error.Message);
    if (!isHeadless)
    {
        Console.WriteLine("Press Enter to close.");
        Console.ReadLine();
    }
};

void StopWithError(string message)
{
    BotLog.Write(message);
    if (!isHeadless)
    {
        Console.WriteLine("Press Enter to close.");
        Console.ReadLine();
    }

    Environment.Exit(1);
}

SocketsHttpHandler marktplaatsHandler = new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.All,
    UseCookies = true,
    CookieContainer = new CookieContainer(),
    PooledConnectionLifetime = TimeSpan.FromMinutes(10)
};

HttpClient marktplaatsClient = new HttpClient(marktplaatsHandler)
{
    Timeout = TimeSpan.FromSeconds(25)
};
marktplaatsClient.DefaultRequestHeaders.Add("User-Agent",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36");
marktplaatsClient.DefaultRequestHeaders.Add("Accept-Language", "nl-NL,nl;q=0.9,en;q=0.8");
marktplaatsClient.DefaultRequestHeaders.Add("Referer", "https://www.marktplaats.nl/");

HttpClient telegramClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(25)
};

string secretsPath = AppFiles.PathFor("secrets.txt");
long lastUpdateId = 0;

string envToken = (Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? "").Trim();
string envChatId = (Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID") ?? "").Trim();
string envPostcode = (Environment.GetEnvironmentVariable("MARKTPLAATS_POSTCODE") ?? "").Trim().ToUpperInvariant().Replace(" ", "");
string envPickupKm = (Environment.GetEnvironmentVariable("MARKTPLAATS_PICKUP_KM") ?? "").Trim();

string token;
string ownerChatId;
string ownerPostcode;
int ownerPickupKm;

if (envToken != "" && envChatId != "" && envPostcode != "" && envPickupKm != "")
{
    token = envToken;
    ownerChatId = envChatId;
    ownerPostcode = envPostcode;
    if (!int.TryParse(envPickupKm, out ownerPickupKm) || ownerPickupKm < 0)
    {
        StopWithError($"MARKTPLAATS_PICKUP_KM (\"{envPickupKm}\") is not a valid whole number (0 or higher).");
    }

    if (!File.Exists(secretsPath))
    {
        File.WriteAllLines(secretsPath, new[] { token, ownerChatId, ownerPostcode, ownerPickupKm.ToString(CultureInfo.InvariantCulture) });
    }
}
else
{
    if (!File.Exists(secretsPath))
    {
        if (isHeadless)
        {
            StopWithError(
                "No secrets.txt found and running headless. Mount secrets.txt in the data folder or set TELEGRAM_BOT_TOKEN, TELEGRAM_CHAT_ID, MARKTPLAATS_POSTCODE, and MARKTPLAATS_PICKUP_KM.");
        }

        string newToken = "";
        while (newToken == "")
        {
            Console.Write("Paste your bot token: ");
            string input = (Console.ReadLine() ?? "").Trim();
            if (input == "")
            {
                continue;
            }

            try
            {
                await telegramClient.GetStringAsync($"https://api.telegram.org/bot{input}/getMe");
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
            try
            {
                string updatesJson =
                    await telegramClient.GetStringAsync($"https://api.telegram.org/bot{newToken}/getUpdates");
                using JsonDocument updates = JsonDocument.Parse(updatesJson);

                foreach (JsonElement update in updates.RootElement.GetProperty("result").EnumerateArray())
                {
                    lastUpdateId = update.GetProperty("update_id").GetInt64();
                    if (update.TryGetProperty("message", out JsonElement msg))
                    {
                        newChatId = msg.GetProperty("chat").GetProperty("id").GetInt64().ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Waiting for Telegram failed ({ex.Message}), retrying...");
            }

            if (newChatId == "")
            {
                await Task.Delay(2000);
            }
        }

        File.WriteAllLines(secretsPath, new[] { newToken, newChatId });
        Console.Clear();
        Console.WriteLine("Setup done! You won't be asked again.");
    }

    string[] secrets = File.ReadAllLines(secretsPath);
    if (secrets.Length < 2)
    {
        StopWithError("secrets.txt is damaged. Delete it and start the app again to redo the setup.");
    }

    token = secrets[0].Trim();
    ownerChatId = secrets[1].Trim();
    if (token == "" || ownerChatId == "")
    {
        StopWithError("secrets.txt has an empty token or chat ID. Delete it and start the app again.");
    }

    if (secrets.Length < 4)
    {
        if (isHeadless)
        {
            StopWithError("secrets.txt is missing postcode/pickup distance (lines 3 and 4).");
        }

        string newPostcode = "";
        while (newPostcode == "")
        {
            Console.Write("Your postcode (like 1234AB): ");
            string input = (Console.ReadLine() ?? "").Trim().ToUpperInvariant().Replace(" ", "");

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
            string input = (Console.ReadLine() ?? "").Trim();
            if (int.TryParse(input, out int km) && km >= 0)
            {
                newPickupKm = km;
            }
            else
            {
                Console.WriteLine("Type a whole number, like 5.");
            }
        }

        File.WriteAllLines(secretsPath, new[] { token, ownerChatId, newPostcode, newPickupKm.ToString(CultureInfo.InvariantCulture) });
        secrets = File.ReadAllLines(secretsPath);
    }

    ownerPostcode = secrets[2].Trim().ToUpperInvariant().Replace(" ", "");
    if (!int.TryParse(secrets[3].Trim(), out ownerPickupKm) || ownerPickupKm < 0)
    {
        StopWithError(
            $"Line 4 in secrets.txt (\"{secrets[3].Trim()}\") is not a valid pickup distance. Use a whole number like 5, or delete secrets.txt.");
    }
}

RepairHints hints = new RepairHints();
hints.Load();

RequestLimiter limiter = new RequestLimiter();
TweakersSource tweakersSource = new TweakersSource();
TweedehandsSource tweedehandsSource = new TweedehandsSource();
CoolblueSource coolblueSource = new CoolblueSource();
VintedSource vintedSource = new VintedSource();
DateTime skipDetailsUntil = DateTime.MinValue;
DateTime lastDetailsFetch = DateTime.MinValue;
DateTime lastWatchFetch = DateTime.MinValue;
DateTime startedAt = DateTime.Now;
int errorsInARow = 0;

Dictionary<int, string> categoryNames = new Dictionary<int, string>();
Dictionary<string, (DateTime FetchedAt, string Json)> searchCache =
    new Dictionary<string, (DateTime, string)>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, long> newestIdPerCategory = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, DateTime> lastCategoryCheck = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

UserSession owner = new UserSession(
    ownerChatId,
    isOwner: true,
    name: "Owner",
    dataDir: AppFiles.DataDirectory,
    token: token,
    postcode: ownerPostcode,
    pickupKm: ownerPickupKm,
    onboardingComplete: true,
    telegramClient: telegramClient,
    marktplaatsClient: marktplaatsClient);

string ownerLoadError = owner.LoadData();
if (ownerLoadError != "")
{
    StopWithError(ownerLoadError);
}

string BuildStatusText(UserSession user)
{
    TimeSpan running = DateTime.Now - startedAt;
    string state = user.Paused
        ? (user.PausedUntil.HasValue ? $"⏰ snoozed until {user.PausedUntil.Value:HH:mm}" : "⏸️ paused")
        : "▶️ running";
    string detailsStatus = DateTime.Now < skipDetailsUntil
        ? $"\n⚠️ Extra listing details paused until {skipDetailsUntil:HH:mm}"
        : "";
    string aiStatus = user.Settings.AiEnabled
        ? $"\n🤖 AI: {user.Settings.AiProvider}/{user.Settings.AiModel} ({user.AiGrader.CallsToday}/{user.Settings.AiDailyLimit} today)"
        : "";
    string pauseNote = DateTime.Now < limiter.PausedUntil
        ? $"\n🛑 Cooling down until {limiter.PausedUntil:HH:mm}"
        : "";
    return
        $"🤖 Status: {state}\n" +
        $"⏱️ Running for {(int)running.TotalHours} h {running.Minutes} min\n" +
        $"🔎 {user.SearchList.Rules.Count} searches, {user.ChecksDone} checks done\n" +
        $"📨 {user.ListingsSent} listings sent · 👀 {user.Watchlist.Count} watched\n" +
        $"📚 {user.History.Count} listings in history\n" +
        $"⚡ {limiter.RequestsPerMinute:0.0} req/min · 🐢 {limiter.DelayMs} ms · 🚫 {limiter.Blocks403Today}x 403 today" +
        pauseNote +
        aiStatus +
        detailsStatus;
}

UserManager userManager = null!;

async Task<Listing?> FetchSingleListingForWatch(string url, string itemId)
{
    Listing ad = new Listing { Id = itemId, Link = url };
    await AddPageDetails(ad, alsoParseTitleAndPrice: true);
    return ad;
}

async Task<string> CheckSearchNow(UserSession user, SearchRule rule)
{
    try
    {
        string url =
            $"https://www.marktplaats.nl/lrp/api/search?query={Uri.EscapeDataString(rule.Query)}&limit=30&sortBy=SORT_INDEX&sortOrder=DECREASING&postcode={Uri.EscapeDataString(user.Postcode)}";
        if (rule.Category != "")
        {
            url += $"&l1CategoryId={rule.Category}";
        }
        if (rule.SubCategory != 0)
        {
            url += $"&l2CategoryId={rule.SubCategory}";
        }

        string cacheKey = $"{rule.Query.ToLowerInvariant()}|{rule.Category}|{user.Postcode}";
        List<Listing> ads = await FetchListings(url, cacheKey, bypassCache: true);
        string[] globalSkip = user.Blocklist.GetGlobalSkipWordsArray();

        List<Listing> matches = new List<Listing>();
        double cheapestOverall = double.MaxValue;

        foreach (Listing ad in ads)
        {
            if (rule.SubCategory != 0 && ad.CategoryId != rule.SubCategory)
            {
                continue;
            }

            if (user.Blocklist.IsSellerBlocked(ad.SellerName) || Filters.IsNotForSale(ad.Title))
            {
                continue;
            }

            string text = ad.Title + ". " + ad.Description;
            if (Filters.FindBadFault(text, rule.SkipWords) != "" || Filters.FindBadFault(text, globalSkip) != "")
            {
                continue;
            }

            bool isNearby = ad.DistanceKm >= 0 && ad.DistanceKm <= user.PickupKm;
            if (!ad.CanShip && !isNearby)
            {
                continue;
            }

            if (ad.Price > 0 && ad.Price < cheapestOverall)
            {
                cheapestOverall = ad.Price;
            }

            if (ad.Price <= rule.MaxPrice)
            {
                matches.Add(ad);
            }
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"⚡ <b>Live check: {WebUtility.HtmlEncode(rule.Query)}</b> (max €{rule.MaxPrice})");
        sb.AppendLine($"Checked {ads.Count} listings → <b>{matches.Count}</b> match right now.\n");

        if (matches.Count == 0)
        {
            if (cheapestOverall < double.MaxValue)
            {
                sb.Append($"💡 Cheapest matching listing right now is <b>{Listing.FormatEuro(cheapestOverall)}</b>.");
            }
            else
            {
                sb.Append("💡 No priced listings matched your filters right now.");
            }

            return sb.ToString();
        }

        foreach (Listing ad in matches.Take(4))
        {
            string fullUrl = ad.Link.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? ad.Link
                : "https://www.marktplaats.nl" + ad.Link;
            sb.AppendLine($"• <a href=\"{WebUtility.HtmlEncode(fullUrl)}\">{WebUtility.HtmlEncode(ad.Title)}</a> — {WebUtility.HtmlEncode(ad.PriceText)} ({WebUtility.HtmlEncode(ad.City)})");
        }

        return sb.ToString().TrimEnd();
    }
    catch (Exception ex)
    {
        return $"❌ Live check failed: {WebUtility.HtmlEncode(ex.Message)}";
    }
}

async Task<PriceCheckResult> LookupMarketPrice(UserSession user, string query)
{
    List<double> livePrices = new List<double>();
    try
    {
        string url =
            $"https://www.marktplaats.nl/lrp/api/search?query={Uri.EscapeDataString(query)}&limit=30&sortBy=SORT_INDEX&sortOrder=DECREASING&postcode={Uri.EscapeDataString(user.Postcode)}";
        string cacheKey = $"price|{query.ToLowerInvariant()}|{user.Postcode}";
        List<Listing> ads = await FetchListings(url, cacheKey);
        foreach (Listing ad in ads)
        {
            if (ad.Price > 0 && !Filters.IsNotForSale(ad.Title))
            {
                livePrices.Add(ad.Price);
            }
        }
    }
    catch (Exception ex)
    {
        BotLog.Write($"Price check fetch failed for '{query}': {ex.Message}");
    }

    List<double> historyPrices = user.History.GetPricesForQuery(query);
    return PriceLookup.Compute(query, livePrices, historyPrices);
}

userManager = new UserManager(
    owner,
    token,
    telegramClient,
    marktplaatsClient,
    wireSession: (session,manager) =>
    {
        session.Commands = new CommandHandler(
            session,
            userManager,
            categoryNames,
            BuildStatusText,
            FetchSingleListingForWatch,
            CheckSearchNow,
            LookupMarketPrice,
            reloadHints: () => hints.Load());
    });

userManager.LoadInvitedUsers();

using WebDashboard webDashboard = new WebDashboard(owner, hints, limiter, startedAt, CheckSearchNow, LookupMarketPrice);
webDashboard.Start();
BotLog.OnWrite += () => webDashboard.BroadcastUpdate();

using PosixSignalRegistration ctrlC = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => userManager.SaveAllUsers());
using PosixSignalRegistration stop = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => userManager.SaveAllUsers());
using PosixSignalRegistration hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => userManager.SaveAllUsers());
AppDomain.CurrentDomain.ProcessExit += (_, _) => userManager.SaveAllUsers();

try
{
    await owner.Telegram.SetCommands(CommandHandler.GetMenuCommands());

    if (lastUpdateId == 0)
    {
        string pendingJson = await owner.Telegram.GetUpdatesJson(0);
        using JsonDocument pendingDoc = JsonDocument.Parse(pendingJson);
        foreach (JsonElement update in pendingDoc.RootElement.GetProperty("result").EnumerateArray())
        {
            lastUpdateId = update.GetProperty("update_id").GetInt64();
        }
    }

    if (owner.SearchList.Rules.Count == 0)
    {
        await owner.Telegram.SendText("The bot is running. Send /menu or paste a Marktplaats URL to get started.");
    }
    else
    {
        string usersNote = userManager.AllUsers.Count > 1 ? $" ({userManager.AllUsers.Count} users)" : "";
        await owner.Telegram.SendText($"✅ Bot started, watching {owner.SearchList.Rules.Count} searches{usersNote}. Send /menu for options.");
    }
}
catch (Exception ex)
{
    BotLog.Write($"Couldn't reach Telegram at startup: {ex.Message}");
}

BotLog.Write($"Data folder: {AppFiles.DataDirectory}");
BotLog.Write($"Watching {owner.SearchList.Rules.Count} searches for owner ({userManager.AllUsers.Count} total user(s)).");

async Task PollTelegramUpdates()
{
    try
    {
        string updatesJson = await owner.Telegram.GetUpdatesJson(lastUpdateId + 1);
        using JsonDocument updates = JsonDocument.Parse(updatesJson);

        foreach (JsonElement update in updates.RootElement.GetProperty("result").EnumerateArray())
        {
            lastUpdateId = update.GetProperty("update_id").GetInt64();

            if (update.TryGetProperty("callback_query", out JsonElement callback))
            {
                string callbackId = callback.TryGetProperty("id", out JsonElement cbIdEl) ? cbIdEl.GetString() ?? "" : "";
                string data = callback.TryGetProperty("data", out JsonElement dataEl) ? dataEl.GetString() ?? "" : "";
                if (!callback.TryGetProperty("message", out JsonElement cbMsg))
                {
                    await owner.Telegram.AnswerCallbackQuery(callbackId);
                    continue;
                }

                string fromChat = cbMsg.GetProperty("chat").GetProperty("id").GetInt64().ToString();
                long messageId = cbMsg.GetProperty("message_id").GetInt64();

                UserSession? cbUser = userManager.GetByChatId(fromChat);
                if (cbUser?.Commands != null)
                {
                    await cbUser.Commands.HandleCallback(callbackId, messageId, data);
                }
                else
                {
                    await owner.Telegram.AnswerCallbackQuery(callbackId);
                }

                continue;
            }

            if (!update.TryGetProperty("message", out JsonElement msg))
            {
                continue;
            }

            string msgChatId = msg.GetProperty("chat").GetProperty("id").GetInt64().ToString();
            UserSession? user = userManager.GetByChatId(msgChatId);

            // Handle uploaded .zip backup or searches.txt documents
            if (msg.TryGetProperty("document", out JsonElement docEl) && user?.Commands != null)
            {
                string fileId = docEl.TryGetProperty("file_id", out JsonElement fidEl) ? fidEl.GetString() ?? "" : "";
                string fileName = docEl.TryGetProperty("file_name", out JsonElement fnameEl) ? fnameEl.GetString() ?? "" : "";
                if (fileId != "" && fileName != "")
                {
                    await user.Commands.HandleDocument(fileId, fileName);
                }

                continue;
            }

            if (!msg.TryGetProperty("text", out JsonElement textElement))
            {
                continue;
            }

            string text = (textElement.GetString() ?? "").Trim();
            if (text == "")
            {
                continue;
            }

            long? replyToId = null;
            if (msg.TryGetProperty("reply_to_message", out JsonElement replyEl) &&
                replyEl.TryGetProperty("message_id", out JsonElement replyIdEl) &&
                replyIdEl.ValueKind == JsonValueKind.Number &&
                replyIdEl.TryGetInt64(out long parsedReplyId))
            {
                replyToId = parsedReplyId;
            }

            if (user?.Commands != null)
            {
                await user.Commands.HandleMessage(text, replyToId);
                continue;
            }

            if (text.StartsWith("/join ", StringComparison.OrdinalIgnoreCase))
            {
                string code = text.Substring(6).Trim();
                string displayName = "";
                if (msg.TryGetProperty("from", out JsonElement fromEl) &&
                    fromEl.TryGetProperty("first_name", out JsonElement firstNameEl))
                {
                    displayName = firstNameEl.GetString() ?? "";
                }

                if (userManager.TryRedeemInvite(code, msgChatId, displayName, out UserSession? created, out string joinMsg))
                {
                    await created!.Commands!.StartOnboarding();
                    await owner.Telegram.SendText($"👋 New user joined: {created.Name} ({created.ChatId}).", silent: true);
                }
                else
                {
                    Telegram guestTelegram = new Telegram(telegramClient, token, msgChatId);
                    await guestTelegram.SendText(joinMsg);
                }
            }
            else if (text.Equals("/start", StringComparison.OrdinalIgnoreCase))
            {
                Telegram guestTelegram = new Telegram(telegramClient, token, msgChatId);
                await guestTelegram.SendText("🔒 This is a private Marktplaats bot. Ask the owner for an invite code, then send /join CODE.");
            }
        }
    }
    catch (Exception ex)
    {
        BotLog.Write($"Error while checking commands: {ex}");
    }
}

async Task CheckAutoReload()
{
    try
    {
        foreach (UserSession user in userManager.AllUsers)
        {
            if (user.SearchList.ReloadIfChanged(out string reloadError, out List<string> removedKeys))
            {
                if (reloadError != "")
                {
                    BotLog.Write($"Couldn't reload searches.txt for {user.Name}: {reloadError}");
                    await user.Telegram.SendText($"⚠️ Couldn't reload searches.txt:\n{reloadError}\n\nKeeping previous searches.");
                }
                else
                {
                    foreach (string key in removedKeys)
                    {
                        user.NewestIdPerSearch.Remove(key);
                        user.Scheduler.Remove(key);
                        user.Stats.Remove(key);
                    }

                    string msg = $"🔄 Reloaded searches.txt ({user.SearchList.Rules.Count} searches).";
                    BotLog.Write(msg);
                    await user.Telegram.SendText(msg, silent: true);
                }
            }
        }

        if (hints.ReloadIfChanged())
        {
            string msg = $"🔄 Reloaded hints.txt ({hints.Count} repair hints).";
            BotLog.Write(msg);
            await owner.Telegram.SendText(msg, silent: true);
        }
    }
    catch (Exception ex)
    {
        BotLog.Write($"Auto-reload check failed: {ex.Message}");
    }
}

async Task CheckPeriodicTasks()
{
    foreach (UserSession user in userManager.AllUsers)
    {
        try
        {
            // Automatically end timed snooze when it expires
            if (user.Paused && user.PausedUntil.HasValue && DateTime.Now >= user.PausedUntil.Value)
            {
                user.Paused = false;
                user.PausedUntil = null;
                await user.Telegram.SendText(
                    user.Settings.IsDutch ? "▶️ Snooze voorbij, zoeken is weer gestart." : "▶️ Snooze over, searching again.",
                    silent: true);
            }

            string? morningDigest = user.Digest.TryBuildMorningDigestHtml(user.Settings);
            if (morningDigest != null)
            {
                await user.Telegram.SendText(morningDigest, parseMode: "HTML");
                BotLog.Write($"Sent morning digest to {user.Name}.");
            }

            if (DateTime.Now - user.LastHeartbeat >= TimeSpan.FromDays(7))
            {
                int checksThisWeek = user.ChecksDone - user.ChecksAtLastHeartbeat;
                user.ChecksAtLastHeartbeat = user.ChecksDone;
                user.LastHeartbeat = DateTime.Now;
                await user.Telegram.SendText(
                    $"💚 Still running ({checksThisWeek} checks this week, {user.SearchList.Rules.Count} searches).",
                    silent: true);
            }
        }
        catch (Exception ex)
        {
            BotLog.Write($"Periodic task error ({user.Name}): {ex.Message}");
        }
    }
}

async Task DelayWithCommands(int totalMs)
{
    int remaining = totalMs;
    while (remaining > 0)
    {
        int slice = Math.Min(remaining, 2000);
        await Task.Delay(slice);
        remaining -= slice;
        if (remaining > 0)
        {
            await PollTelegramUpdates();
        }
    }
}

async Task<List<Listing>> FetchListings(string url, string cacheKey, bool bypassCache = false)
{
    string json;
    if (!bypassCache &&
        searchCache.TryGetValue(cacheKey, out var cached) &&
        DateTime.Now - cached.FetchedAt < TimeSpan.FromSeconds(45))
    {
        json = cached.Json;
    }
    else
    {
        using HttpRequestMessage searchReq = new HttpRequestMessage(HttpMethod.Get, url);
        searchReq.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        searchReq.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        searchReq.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        searchReq.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");

        limiter.LogRequest();
        using HttpResponseMessage searchResp = await marktplaatsClient.SendAsync(searchReq);
        searchResp.EnsureSuccessStatusCode();
        json = await searchResp.Content.ReadAsStringAsync();

        if (errorsInARow >= 5)
        {
            try
            {
                await owner.Telegram.SendText("✅ Back to normal, searches work again.");
            }
            catch
            {
            }
        }

        errorsInARow = 0;
        limiter.Success();
        searchCache[cacheKey] = (DateTime.Now, json);
        if (searchCache.Count > 200)
        {
            DateTime expire = DateTime.Now.AddSeconds(-45);
            foreach (string k in searchCache.Where(kv => kv.Value.FetchedAt < expire).Select(kv => kv.Key).ToList())
            {
                searchCache.Remove(k);
            }
        }
    }

    List<Listing> ads = new List<Listing>();
    using JsonDocument doc = JsonDocument.Parse(json);
    if (doc.RootElement.TryGetProperty("searchCategoryOptions", out JsonElement options) &&
        options.ValueKind == JsonValueKind.Array)
    {
        foreach (JsonElement option in options.EnumerateArray())
        {
            if (option.TryGetProperty("id", out JsonElement idEl) &&
                idEl.ValueKind == JsonValueKind.Number &&
                idEl.TryGetInt32(out int catId))
            {
                string fullName = option.TryGetProperty("fullName", out JsonElement nameEl)
                    ? nameEl.GetString() ?? ""
                    : "";
                categoryNames[catId] = fullName;
            }
        }
    }

    if (doc.RootElement.TryGetProperty("listings", out JsonElement listingsEl) &&
        listingsEl.ValueKind == JsonValueKind.Array)
    {
        foreach (JsonElement listing in listingsEl.EnumerateArray())
        {
            string itemId = listing.TryGetProperty("itemId", out JsonElement itemIdEl)
                ? itemIdEl.GetString() ?? ""
                : "";
            if (itemId == "" || itemId.StartsWith("a"))
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
    }

    return ads;
}

static string FindInText(string text, string pattern)
{
    Match match = Regex.Match(text, pattern);
    return match.Success ? match.Groups[1].Value : "";
}

static string ExtractJsonArray(string text, string sectionMarker, string arrayMarker)
{
    int sectionStart = text.IndexOf(sectionMarker, StringComparison.Ordinal);
    if (sectionStart < 0)
    {
        return "";
    }

    int markerStart = text.IndexOf(arrayMarker, sectionStart, StringComparison.Ordinal);
    if (markerStart < 0)
    {
        return "";
    }

    int openBracket = text.IndexOf('[', markerStart + arrayMarker.Length);
    if (openBracket < 0)
    {
        return "";
    }

    int depth = 0;
    bool inString = false;
    bool escaped = false;

    for (int i = openBracket; i < text.Length; i++)
    {
        char c = text[i];
        if (inString)
        {
            if (escaped)
            {
                escaped = false;
            }
            else if (c == '\\')
            {
                escaped = true;
            }
            else if (c == '"')
            {
                inString = false;
            }

            continue;
        }

        if (c == '"')
        {
            inString = true;
        }
        else if (c == '[')
        {
            depth++;
        }
        else if (c == ']')
        {
            depth--;
            if (depth == 0)
            {
                return text.Substring(openBracket + 1, i - openBracket - 1);
            }
        }
    }

    return "";
}

static string ExtractDescriptionHtml(string html)
{
    const string marker = "data-collapsable=\"description\"";
    int markerIndex = html.IndexOf(marker, StringComparison.Ordinal);
    if (markerIndex < 0)
    {
        return "";
    }

    int contentStart = html.IndexOf('>', markerIndex + marker.Length);
    if (contentStart < 0)
    {
        return "";
    }

    contentStart++;
    int depth = 1;
    int pos = contentStart;

    while (pos < html.Length && depth > 0)
    {
        int nextOpen = html.IndexOf("<div", pos, StringComparison.OrdinalIgnoreCase);
        int nextClose = html.IndexOf("</div>", pos, StringComparison.OrdinalIgnoreCase);

        if (nextClose < 0)
        {
            return "";
        }

        if (nextOpen >= 0 && nextOpen < nextClose)
        {
            depth++;
            pos = nextOpen + 4;
        }
        else
        {
            depth--;
            if (depth == 0)
            {
                return html.Substring(contentStart, nextClose - contentStart);
            }

            pos = nextClose + 6;
        }
    }

    return "";
}

async Task<(bool NotFound, bool Reserved)> AddPageDetails(Listing ad, bool alsoParseTitleAndPrice = false)
{
    if (DateTime.Now < skipDetailsUntil || ad.Link == "")
    {
        return (false, false);
    }

    TimeSpan sinceLast = DateTime.Now - lastDetailsFetch;
    int minGapMs = Random.Shared.Next(700, 1300);
    if (sinceLast < TimeSpan.FromMilliseconds(minGapMs))
    {
        await Task.Delay(TimeSpan.FromMilliseconds(minGapMs) - sinceLast);
    }

    lastDetailsFetch = DateTime.Now;

    try
    {
        string pageUrl = ad.Link.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? ad.Link
            : "https://www.marktplaats.nl" + ad.Link;

        using HttpRequestMessage pageReq = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        pageReq.Headers.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        pageReq.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        pageReq.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        pageReq.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        pageReq.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");

        limiter.LogRequest();
        using HttpResponseMessage response = await marktplaatsClient.SendAsync(pageReq);
        if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone)
        {
            return (true, false);
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden)
        {
            skipDetailsUntil = DateTime.Now.AddMinutes(30);
            limiter.Blocked((int)response.StatusCode);
            BotLog.Write($"Listing pages returned {(int)response.StatusCode}: skipping extra info for 30 minutes.");
            return (false, false);
        }

        response.EnsureSuccessStatusCode();
        string html = await response.Content.ReadAsStringAsync();

        if (alsoParseTitleAndPrice)
        {
            if (response.RequestMessage?.RequestUri != null)
            {
                ad.Link = response.RequestMessage.RequestUri.ToString();
            }

            string pageTitle = FindInText(html, """<title>([^<]+)</title>""");
            if (pageTitle != "")
            {
                pageTitle = WebUtility.HtmlDecode(pageTitle)
                    .Replace("≥ ", "")
                    .Split(" - ")[0]
                    .Trim();
                if (pageTitle != "")
                {
                    ad.Title = pageTitle;
                }
            }

            string centsStr = FindInText(html, """priceCents":(\d+)""");
            if (long.TryParse(centsStr, out long cents))
            {
                ad.Price = cents / 100.0;
            }
        }

        string stats = FindInText(html, """stats":\{([^}]*)\}""");
        string views = FindInText(stats, """viewCount":(\d+)""");
        string saves = FindInText(stats, """favoritedCount":(\d+)""");
        string since = FindInText(stats, """since":"([^"]+)""");
        string years = FindInText(html, """activeYears":(\d+)""");
        string ratingStr = FindInText(html, """averageScore":(\d+(?:\.\d+)?)""");
        string reviewsStr = FindInText(html, """numberOfReviews":(\d+)""");
        ad.SellerType = FindInText(html, """sellerType":"([A-Z_]+)""");

        if (int.TryParse(views, out int parsedViews))
        {
            ad.Views = parsedViews;
        }

        if (int.TryParse(saves, out int parsedSaves))
        {
            ad.Saves = parsedSaves;
        }

        if (since != "" && DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.None,
                out DateTimeOffset postedAt))
        {
            ad.PostedAt = postedAt.LocalDateTime;
        }

        if (int.TryParse(years, out int parsedYears))
        {
            ad.SellerYears = parsedYears;
        }

        if (double.TryParse(ratingStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedRating) &&
            parsedRating >= 0 && parsedRating <= 5)
        {
            ad.SellerRating = parsedRating;
        }

        if (int.TryParse(reviewsStr, out int parsedReviews))
        {
            ad.SellerReviewCount = parsedReviews;
        }

        string descriptionHtml = ExtractDescriptionHtml(html);
        if (descriptionHtml != "")
        {
            string text = Regex.Replace(descriptionHtml, "<[^>]+>", "\n");
            ad.Description = WebUtility.HtmlDecode(text).Trim();
        }

        string bids = ExtractJsonArray(html, "\"bidsInfo\"", "\"bids\"");
        foreach (Match bid in Regex.Matches(bids, """value":(\d+)"""))
        {
            if (long.TryParse(bid.Groups[1].Value, out long bidCents))
            {
                ad.BidCount++;
                double amount = bidCents / 100.0;
                if (amount > ad.HighestBid)
                {
                    ad.HighestBid = amount;
                }
            }
        }

        bool isReserved = html.Contains("\"reserved\":true", StringComparison.OrdinalIgnoreCase) ||
                          html.Contains("Gereserveerd</span>", StringComparison.OrdinalIgnoreCase);
        return (false, isReserved);
    }
    catch (Exception ex)
    {
        BotLog.Write($"Couldn't get extra info for '{ad.Title}': {ex.Message}");
        return (false, false);
    }
}

async Task CheckNextWatchedListing()
{
    if (DateTime.Now < skipDetailsUntil || DateTime.Now - lastWatchFetch < TimeSpan.FromSeconds(15))
    {
        return;
    }

    foreach (UserSession user in userManager.AllUsers)
    {
        WatchItem? item = user.Watchlist.NextDue(TimeSpan.FromMinutes(5));
        if (item == null)
        {
            continue;
        }

        lastWatchFetch = DateTime.Now;

        item.LastCheckedAt = DateTime.Now;
        Listing probe = new Listing { Id = item.Id, Title = item.Title, Link = item.Link };
        var (notFound, reserved) = await AddPageDetails(probe, alsoParseTitleAndPrice: true);

        if (notFound)
        {
            user.Watchlist.Remove(item.Id);
            await user.Telegram.SendText(
                $"❌ <b>Watched listing removed or sold:</b>\n{WebUtility.HtmlEncode(item.Title)}",
                parseMode: "HTML");
            return;
        }

        if (probe.Title != "" && item.Title.StartsWith("Listing m"))
        {
            item.Title = probe.Title;
        }
        if (probe.Link != "")
        {
            item.Link = probe.Link;
        }

        string fullUrl = item.Link.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? item.Link
            : "https://www.marktplaats.nl" + item.Link;
        string buttons = Telegram.ListingButtons(fullUrl, item.Id, watching: true, user.Settings.IsDutch);

        if (reserved && item.Status != "RESERVED")
        {
            item.Status = "RESERVED";
            await user.Telegram.SendText(
                $"🔒 <b>Watched listing marked Gereserveerd:</b>\n{WebUtility.HtmlEncode(item.Title)}",
                buttons,
                parseMode: "HTML");
        }
        else if (!reserved && item.Status == "RESERVED")
        {
            item.Status = "ACTIVE";
            await user.Telegram.SendText(
                $"🔓 <b>Watched listing available again:</b>\n{WebUtility.HtmlEncode(item.Title)}",
                buttons,
                parseMode: "HTML");
        }

        if (probe.Price > 0 && item.LastPrice > 0 && probe.Price < item.LastPrice)
        {
            double oldPrice = item.LastPrice;
            item.LastPrice = probe.Price;
            bool hitTarget = item.TargetPrice > 0 && probe.Price <= item.TargetPrice;
            string header = hitTarget ? "🎯 <b>WATCH TARGET HIT!</b>" : "📉 <b>Watched listing price drop!</b>";
            await user.Telegram.SendText(
                $"{header}\n{WebUtility.HtmlEncode(item.Title)}\n💶 {Listing.FormatEuro(oldPrice)} → <b>{Listing.FormatEuro(probe.Price)}</b>",
                buttons,
                parseMode: "HTML");
        }
        else if (probe.Price > 0 && item.LastPrice == 0)
        {
            item.LastPrice = probe.Price;
        }

        if (probe.HighestBid > item.HighestBid && probe.HighestBid > 0)
        {
            double oldBid = item.HighestBid;
            item.HighestBid = probe.HighestBid;
            item.BidCount = probe.BidCount;
            string bidLine = oldBid > 0
                ? $"{Listing.FormatEuro(oldBid)} → <b>{Listing.FormatEuro(probe.HighestBid)}</b>"
                : $"<b>{Listing.FormatEuro(probe.HighestBid)}</b>";
            await user.Telegram.SendText(
                $"💰 <b>New highest bid on watched listing:</b>\n{WebUtility.HtmlEncode(item.Title)}\n{bidLine} ({probe.BidCount} bids)",
                buttons,
                parseMode: "HTML");
        }

        user.Watchlist.Save();
        return;
    }
}

async Task<bool> TrySend(UserSession user, Listing ad, string search, string forcedAlert = "")
{
    if (!user.SearchList.Rules.TryGetValue(search, out SearchRule? rule))
    {
        return false;
    }

    bool isMarktplaats = ad.Source.Equals("Marktplaats", StringComparison.OrdinalIgnoreCase);

    if (isMarktplaats && rule.SubCategory != 0 && ad.CategoryId != rule.SubCategory)
    {
        user.Digest.RecordFilteredOut();
        user.Stats.RecordFiltered(rule.Query);
        user.Feed.RecordSkipped(ad, rule.Query, $"Wrong subcategory ({ad.CategoryId})", "other");
        return false;
    }

    if (forcedAlert == "" && user.SeenIds.Contains(ad.Id))
    {
        return false;
    }

    if (user.Blocklist.IsSellerBlocked(ad.SellerName))
    {
        BotLog.Write($"Skipped (blocked seller '{ad.SellerName}'): {ad.Title}");
        user.Digest.RecordFilteredOut();
        user.Stats.RecordFiltered(rule.Query);
        user.Feed.RecordSkipped(ad, rule.Query, $"Blocked seller: {ad.SellerName}", "business");
        return false;
    }

    if (Filters.IsNotForSale(ad.Title))
    {
        user.Digest.RecordFilteredOut();
        user.Stats.RecordFiltered(rule.Query);
        user.Feed.RecordSkipped(ad, rule.Query, "Not for sale (gezocht/reparatie/inkoop)", "other");
        return false;
    }

    string[] globalSkip = user.Blocklist.GetGlobalSkipWordsArray();
    string initialText = ad.Title + ". " + ad.Description;
    string badWord = Filters.FindBadFault(initialText, rule.SkipWords);
    if (badWord == "")
    {
        badWord = Filters.FindBadFault(initialText, globalSkip);
    }
    if (badWord != "")
    {
        BotLog.Write($"Skipped ({badWord}): {ad.Title}");
        user.Digest.RecordFilteredOut();
        user.Stats.RecordFiltered(rule.Query);
        user.Feed.RecordSkipped(ad, rule.Query, $"Skip word: {badWord}", "skipWord");
        return false;
    }

    if (ad.Price > rule.MaxPrice)
    {
        user.Digest.RecordFilteredOut();
        user.Stats.RecordFiltered(rule.Query);
        user.Feed.RecordSkipped(ad, rule.Query, $"Over max price ({Listing.FormatEuro(ad.Price)} > €{rule.MaxPrice})", "price");
        return false;
    }

    bool isNearby = ad.DistanceKm >= 0 && ad.DistanceKm <= user.PickupKm;
    if (!ad.CanShip && !isNearby)
    {
        user.Digest.RecordFilteredOut();
        user.Stats.RecordFiltered(rule.Query);
        user.Feed.RecordSkipped(ad, rule.Query, $"Pickup only ({ad.DistanceKm} km > {user.PickupKm} km)", "other");
        return false;
    }

    string alert = "";
    if (forcedAlert != "")
    {
        alert = forcedAlert;
    }
    else if (Filters.IsSuperDeal(ad, rule.MaxPrice))
    {
        alert = "SUPER DEAL";
    }
    else if (rule.Urgent)
    {
        alert = "URGENT";
    }

    ad.MatchedSearch = rule.Query;
    ad.CategoryName = categoryNames.GetValueOrDefault(ad.CategoryId, "");
    if (isMarktplaats)
    {
        await AddPageDetails(ad);
    }

    if (ad.HighestBid > rule.MaxPrice && rule.MaxPrice > 0)
    {
        BotLog.Write($"Skipped (highest bid {Listing.FormatEuro(ad.HighestBid)} > max €{rule.MaxPrice}): {ad.Title}");
        user.Digest.RecordFilteredOut();
        user.Stats.RecordFiltered(rule.Query);
        user.Feed.RecordSkipped(ad, rule.Query, $"Highest bid {Listing.FormatEuro(ad.HighestBid)} > max €{rule.MaxPrice}", "other");
        return false;
    }

    if (ad.Price == 0)
    {
        ad.MentionedPrice = Filters.ExtractMentionedPrice(ad.Description);
        if (ad.MentionedPrice > 0 && rule.MaxPrice > 0 &&
            user.Settings.SkipHighDescriptionPrice &&
            ad.MentionedPrice > rule.MaxPrice * 1.35)
        {
            BotLog.Write($"Skipped (description mentions {Listing.FormatEuro(ad.MentionedPrice)} > max €{rule.MaxPrice}): {ad.Title}");
            user.Digest.RecordFilteredOut();
            user.Stats.RecordFiltered(rule.Query);
            user.Feed.RecordSkipped(ad, rule.Query, $"Description mentions {Listing.FormatEuro(ad.MentionedPrice)}", "price");
            return false;
        }
    }

    string lateBadWord = Filters.FindBadFault(ad.Description, rule.SkipWords);
    if (lateBadWord == "")
    {
        lateBadWord = Filters.FindBadFault(ad.Description, globalSkip);
    }
    if (lateBadWord != "")
    {
        BotLog.Write($"Skipped ({lateBadWord}, in full description): {ad.Title}");
        user.Digest.RecordFilteredOut();
        user.Stats.RecordFiltered(rule.Query);
        user.Feed.RecordSkipped(ad, rule.Query, $"Skip word (description): {lateBadWord}", "skipWord");
        return false;
    }

    if (ad.SellerType == "TRADER")
    {
        BotLog.Write($"Skipped (business): {ad.Title}");
        user.Digest.RecordFilteredOut();
        user.Stats.RecordFiltered(rule.Query);
        user.Feed.RecordSkipped(ad, rule.Query, "Business account (TRADER)", "business");
        return false;
    }

    bool isRetailOutlet = ad.Source.Equals("Coolblue Tweedekans", StringComparison.OrdinalIgnoreCase);
    ad.LikelyBusiness = !isRetailOutlet && BusinessFilter.Score(ad) >= 2;
    if (ad.LikelyBusiness && user.Settings.SkipLikelyBusinesses)
    {
        BotLog.Write($"Skipped (likely business): {ad.Title}");
        user.Digest.RecordFilteredOut();
        user.Stats.RecordFiltered(rule.Query);
        user.Feed.RecordSkipped(ad, rule.Query, "Likely business (BusinessFilter)", "business");
        return false;
    }

    RepairHint? hint = hints.Find(ad.Title + ". " + ad.Description);
    if (hint != null)
    {
        ad.Hint = hint.Text;
        ad.HintQuestion = hint.Question;
    }

    if (ad.Price > 0)
    {
        ad.TypicalPrice = user.History.TypicalPrice(rule.Query, ad.CategoryId, out int count);
        ad.TypicalCount = count;
    }

    if (rule.Defect)
    {
        string faultWord = Filters.FindFaultWord(ad);
        if (faultWord != "")
        {
            ad.FaultReason = $"🛠️ Fault: {faultWord}";
        }
        else if (ad.Price > 0 && ad.TypicalPrice > 0 && ad.Price <= ad.TypicalPrice * 0.4)
        {
            ad.FaultReason = "🤔 No fault mentioned, but very cheap: ask what's wrong";
        }
        else
        {
            BotLog.Write($"Skipped (doesn't look broken): {ad.Title}");
            user.Digest.RecordFilteredOut();
            user.Stats.RecordFiltered(rule.Query);
            user.Feed.RecordSkipped(ad, rule.Query, "Doesn't look broken (defect flag)", "other");
            return false;
        }
    }

    string oldCpu = Filters.FindOldCpu(ad, rule.Query);
    if (oldCpu != "")
    {
        BotLog.Write($"Skipped (old CPU '{oldCpu}'): {ad.Title}");
        user.Digest.RecordFilteredOut();
        user.Stats.RecordFiltered(rule.Query);
        user.Feed.RecordSkipped(ad, rule.Query, $"Old CPU: {oldCpu}", "other");
        return false;
    }

    ad.ScamWarning = Filters.FindScamSigns(ad);

    // Compute perceptual thumbnail hash to catch reposts even when the seller changes the title
    if (ad.Photo != "" && ad.PhotoHash == "")
    {
        try
        {
            using HttpResponseMessage imgResp = await marktplaatsClient.GetAsync(ad.Photo);
            if (imgResp.IsSuccessStatusCode)
            {
                byte[] imgBytes = await imgResp.Content.ReadAsByteArrayAsync();
                if (imgBytes.Length > 0 && imgBytes.Length <= 1024 * 1024)
                {
                    ad.PhotoHash = ImageHasher.ComputeHash(imgBytes);
                    user.History.SetPhotoHash(ad.Id, ad.PhotoHash);
                }
            }
        }
        catch
        {
        }
    }

    HistoryEntry? earlier = user.History.FindRepost(ad, out bool matchedByPhoto);
    if (earlier != null)
    {
        ad.RepostInfo = matchedByPhoto
            ? $"Same photo posted on {earlier.FirstSeen.ToString("d MMM", CultureInfo.InvariantCulture)} (\"{earlier.Title}\")"
            : $"Posted before on {earlier.FirstSeen.ToString("d MMM", CultureInfo.InvariantCulture)}";
        if (earlier.Price > 0)
        {
            ad.RepostInfo += $" for {Listing.FormatEuro(earlier.Price)}";
        }
    }

    AiGradeResult? aiGrade = await user.AiGrader.GradeAsync(ad, rule, user.Settings);
    if (aiGrade != null)
    {
        ad.AiVerdict = aiGrade.Verdict;
        ad.AiReason = aiGrade.Reason;
        ad.AiResale = aiGrade.Resale;
        ad.AiMaxBid = aiGrade.MaxBid;

        if (user.Settings.AiAutoSkip && aiGrade.Verdict == "SKIP")
        {
            BotLog.Write($"Skipped (AI: {aiGrade.Reason}): {ad.Title}");
            user.Digest.RecordFilteredOut();
            user.Stats.RecordFiltered(rule.Query);
            user.Feed.RecordSkipped(ad, rule.Query, $"AI SKIP: {aiGrade.Reason}", "other");
            return false;
        }
    }

    bool night = Filters.IsNight(user.Settings);
    bool isWatched = user.Watchlist.Contains(ad.Id);
    long sentMessageId = await user.Telegram.SendListing(ad, night, alert, isWatched, user.Settings.IsDutch);
    user.MarkSeen(ad.Id);
    user.RememberSentMessage(ad, sentMessageId);
    user.History.MarkPriceAlerted(ad.Id);
    user.Digest.RecordSent(ad, alert, wasSilent: night && alert == "");
    user.Feed.RecordSent(ad, alert);
    webDashboard.BroadcastUpdate();
    user.Stats.RecordSent(rule.Query, ad.Price);
    await Task.Delay(1000);
    user.ListingsSent++;
    BotLog.Write($"Sent ({user.Name} / {rule.Query}): {ad.Title}");
    return true;
}

async Task HandleError(string what, Exception ex)
{
    BotLog.Write($"Error with '{what}': {ex.Message}");
    errorsInARow++;
    if (errorsInARow == 5)
    {
        try
        {
            await owner.Telegram.SendText(
                $"⚠️ 5 errors in a row. Marktplaats might be blocking me, or the internet is down.\nLast error: {ex.Message}");
        }
        catch
        {
        }
    }

    if (ex is HttpRequestException httpError &&
        (httpError.StatusCode == HttpStatusCode.TooManyRequests ||
         httpError.StatusCode == HttpStatusCode.Forbidden))
    {
        limiter.Blocked((int)httpError.StatusCode.Value);
    }
}

int userTurnOffset = 0;

(UserSession? User, string Search) PickNextDueSearch()
{
    List<UserSession> users = userManager.AllUsers.ToList();
    if (users.Count == 0)
    {
        return (null, "");
    }

    for (int i = 0; i < users.Count; i++)
    {
        UserSession candidate = users[(userTurnOffset + i) % users.Count];
        if (candidate.Paused || !candidate.OnboardingComplete)
        {
            continue;
        }

        string due = candidate.Scheduler.NextDue(candidate.SearchList.GetKeysSnapshot());
        if (due != "")
        {
            userTurnOffset = (userTurnOffset + i + 1) % users.Count;
            return (candidate, due);
        }
    }

    return (null, "");
}

// 30-40s Category Firehose: polls the newest 100 listings in each active L1 category
// and matches all user searches locally in memory for fast detection with fewer requests.
async Task CheckCategoryFirehose()
{
    HashSet<string> activeCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (UserSession u in userManager.AllUsers)
    {
        if (u.Paused || !u.OnboardingComplete || !u.Settings.FastCategoryMode)
        {
            continue;
        }
        foreach (SearchRule r in u.SearchList.GetRulesSnapshot())
        {
            if (r.Category != "")
            {
                activeCategories.Add(r.Category);
            }
        }
    }

    foreach (string cat in activeCategories)
    {
        DateTime lastCheck = lastCategoryCheck.GetValueOrDefault(cat, DateTime.MinValue);
        if (DateTime.Now - lastCheck < TimeSpan.FromSeconds(38))
        {
            continue;
        }

        lastCategoryCheck[cat] = DateTime.Now;
        bool quiet = !newestIdPerCategory.ContainsKey(cat);
        long newest = newestIdPerCategory.GetValueOrDefault(cat, 0);
        long highest = newest;

        try
        {
            string url =
                $"https://www.marktplaats.nl/lrp/api/search?l1CategoryId={cat}&limit=100&sortBy=SORT_INDEX&sortOrder=DECREASING&postcode={Uri.EscapeDataString(owner.Postcode)}";
            List<Listing> ads = await FetchListings(url, $"cat|{cat}|{owner.Postcode}", bypassCache: true);

            foreach (Listing ad in ads)
            {
                long idNumber = 0;
                bool hasNum = ad.Id.Length > 1 && long.TryParse(ad.Id.AsSpan(1), out idNumber);
                if (hasNum && idNumber > highest)
                {
                    highest = idNumber;
                }

                if (quiet || (hasNum && idNumber <= newest))
                {
                    continue;
                }

                foreach (UserSession u in userManager.AllUsers)
                {
                    if (u.Paused || !u.OnboardingComplete || !u.Settings.FastCategoryMode || u.SeenIds.Contains(ad.Id))
                    {
                        continue;
                    }

                    foreach (SearchRule rule in u.SearchList.GetRulesSnapshot())
                    {
                        if (!string.Equals(rule.Category, cat, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!Filters.MatchesSearch(rule.Query, ad))
                        {
                            continue;
                        }

                        u.History.Remember(ad, rule.Query);
                        u.Stats.RecordNew(rule.Query);
                        if (await TrySend(u, ad, rule.Query))
                        {
                            break; // Already sent to this user
                        }
                    }
                }
            }

            newestIdPerCategory[cat] = highest;
        }
        catch (Exception ex)
        {
            await HandleError($"category {cat}", ex);
        }

        // At most one category firehose fetch per loop cycle so we never burst Marktplaats
        return;
    }
}

async Task CheckTweakersFeed()
{
    bool anyEnabled = userManager.AllUsers.Any(u => !u.Paused && u.OnboardingComplete && u.Settings.EnableTweakers);
    if (!anyEnabled)
    {
        return;
    }

    List<Listing> newAds = await tweakersSource.FetchNewIfDueAsync(marktplaatsClient, TimeSpan.FromSeconds(60));
    foreach (Listing ad in newAds)
    {
        foreach (UserSession u in userManager.AllUsers)
        {
            if (u.Paused || !u.OnboardingComplete || !u.Settings.EnableTweakers || u.SeenIds.Contains(ad.Id))
            {
                continue;
            }

            foreach (SearchRule rule in u.SearchList.GetRulesSnapshot())
            {
                if (!Filters.MatchesSearch(rule.Query, ad))
                {
                    continue;
                }

                u.History.Remember(ad, rule.Query);
                u.Stats.RecordNew(rule.Query);
                if (await TrySend(u, ad, rule.Query))
                {
                    break;
                }
            }
        }
    }
}

async Task CheckExtraSources()
{
    // 1. Coolblue Tweedekans (slow category rotation, strict price-error filter only)
    if (userManager.AllUsers.Any(u => !u.Paused && u.OnboardingComplete && u.Settings.EnableCoolblue))
    {
        List<Listing> cbAds = await coolblueSource.FetchNextCategoryIfDueAsync(marktplaatsClient, TimeSpan.FromSeconds(75));
        foreach (Listing ad in cbAds)
        {
            foreach (UserSession u in userManager.AllUsers)
            {
                if (u.Paused || !u.OnboardingComplete || !u.Settings.EnableCoolblue || u.SeenIds.Contains(ad.Id))
                {
                    continue;
                }

                foreach (SearchRule rule in u.SearchList.GetRulesSnapshot())
                {
                    if (!Filters.MatchesSearch(rule.Query, ad) || !CoolblueSource.IsPriceErrorDeal(ad, rule.MaxPrice))
                    {
                        continue;
                    }

                    u.History.Remember(ad, rule.Query);
                    u.Stats.RecordNew(rule.Query);
                    if (await TrySend(u, ad, rule.Query, "PRICE ERROR"))
                    {
                        break;
                    }
                }
            }
        }
    }

    // Helper for per-query round-robin sources (2dehands.be, Vinted.nl)
    List<SearchRule> CollectActiveRules(Func<BotSettings, bool> isEnabled, Func<SearchRule, bool>? ruleFilter = null)
    {
        Dictionary<string, SearchRule> merged = new Dictionary<string, SearchRule>(StringComparer.OrdinalIgnoreCase);
        foreach (UserSession u in userManager.AllUsers)
        {
            if (u.Paused || !u.OnboardingComplete || !isEnabled(u.Settings))
            {
                continue;
            }

            foreach (SearchRule r in u.SearchList.GetRulesSnapshot())
            {
                if (ruleFilter != null && !ruleFilter(r))
                {
                    continue;
                }

                if (!merged.TryGetValue(r.Query, out SearchRule? existing) || r.MaxPrice > existing.MaxPrice || (r.Urgent && !existing.Urgent))
                {
                    merged[r.Query] = r;
                }
            }
        }
        return merged.Values.ToList();
    }

    async Task DispatchQueryResults(
        string query,
        List<Listing> ads,
        Func<BotSettings, bool> isEnabled,
        Func<Listing, SearchRule, bool>? extraFilter = null)
    {
        if (query == "" || ads.Count == 0)
        {
            return;
        }

        foreach (Listing ad in ads)
        {
            foreach (UserSession u in userManager.AllUsers)
            {
                if (u.Paused || !u.OnboardingComplete || !isEnabled(u.Settings) || u.SeenIds.Contains(ad.Id))
                {
                    continue;
                }

                if (!u.SearchList.Rules.TryGetValue(query, out SearchRule? userRule))
                {
                    continue;
                }

                if (extraFilter != null && !extraFilter(ad, userRule))
                {
                    continue;
                }

                u.History.Remember(ad, userRule.Query);
                u.Stats.RecordNew(userRule.Query);
                await TrySend(u, ad, userRule.Query);
            }
        }
    }

    // 2. 2dehands.be (GPUs, CPUs, RAM only, shipped, strict 85% cap; at most 1 request every 25s)
    List<SearchRule> beRules = CollectActiveRules(s => s.EnableTweedehands, TweedehandsSource.IsEligibleRule);
    if (beRules.Count > 0)
    {
        var (beQuery, beAds) = await tweedehandsSource.FetchNextDueAsync(
            marktplaatsClient, beRules, TimeSpan.FromSeconds(25), TimeSpan.FromMinutes(4));
        await DispatchQueryResults(
            beQuery,
            beAds,
            s => s.EnableTweedehands,
            (ad, r) => TweedehandsSource.IsEligibleRule(r) &&
                       ad.CanShip &&
                       ad.Price > 0 &&
                       ad.Price <= TweedehandsSource.StrictMaxPrice(r.MaxPrice));
    }

    // 3. Vinted.nl (at most 1 request every 30s, each query every ~5 min)
    List<SearchRule> vintedRules = CollectActiveRules(s => s.EnableVinted);
    if (vintedRules.Count > 0)
    {
        var (vQuery, vAds) = await vintedSource.FetchNextDueAsync(
            marktplaatsClient, vintedRules, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
        await DispatchQueryResults(vQuery, vAds, s => s.EnableVinted);
    }
}

while (true)
{
    await CheckAutoReload();
    await CheckPeriodicTasks();

    if (DateTime.Now < limiter.PausedUntil)
    {
        await PollTelegramUpdates();
        await Task.Delay(1000);
        continue;
    }

    await CheckCategoryFirehose();
    await CheckTweakersFeed();
    await CheckExtraSources();
    await CheckNextWatchedListing();

    var (activeUser, search) = PickNextDueSearch();
    if (activeUser == null || search == "")
    {
        await PollTelegramUpdates();
        await Task.Delay(1000);
        continue;
    }

    if (!activeUser.SearchList.Rules.TryGetValue(search, out SearchRule? rule))
    {
        continue;
    }

    bool quiet = !activeUser.NewestIdPerSearch.ContainsKey(search);
    bool foundNew = false;

    try
    {
        string url =
            $"https://www.marktplaats.nl/lrp/api/search?query={Uri.EscapeDataString(rule.Query)}&limit=30&sortBy=SORT_INDEX&sortOrder=DECREASING&postcode={Uri.EscapeDataString(activeUser.Postcode)}";
        if (rule.Category != "")
        {
            url += $"&l1CategoryId={rule.Category}";
        }
        if (rule.SubCategory != 0)
        {
            url += $"&l2CategoryId={rule.SubCategory}";
        }

        string cacheKey = $"{rule.Query.ToLowerInvariant()}|{rule.Category}|{activeUser.Postcode}";
        List<Listing> ads = await FetchListings(url, cacheKey);
        activeUser.ChecksDone++;
        activeUser.Digest.RecordCheck();
        activeUser.Stats.RecordCheck(rule.Query);

        long newest = activeUser.NewestIdPerSearch.GetValueOrDefault(search, 0);
        long highestThisCheck = newest;

        foreach (Listing ad in ads)
        {
            long idNumber = 0;
            bool hasNumericId = ad.Id.Length > 1 && long.TryParse(ad.Id.AsSpan(1), out idNumber);
            if (hasNumericId && idNumber > highestThisCheck)
            {
                highestThisCheck = idNumber;
            }

            double oldPrice = activeUser.History.Remember(ad, rule.Query);
            if (oldPrice > 0 && ad.Price > 0 && ad.Price < oldPrice)
            {
                ad.OldPrice = oldPrice;
                bool sentDrop = await TrySend(activeUser, ad, rule.Query, "PRICE DROP");
                if (!sentDrop)
                {
                    activeUser.History.MarkPriceDropChecked(ad.Id, rule.Query);
                }

                continue;
            }

            if (quiet)
            {
                continue;
            }

            if (hasNumericId ? idNumber <= newest : activeUser.SeenIds.Contains(ad.Id))
            {
                continue;
            }

            foundNew = true;
            activeUser.Stats.RecordNew(rule.Query);

            await TrySend(activeUser, ad, rule.Query);
        }

        activeUser.NewestIdPerSearch[search] = highestThisCheck;

        activeUser.History.SaveIfNeeded();
        activeUser.Stats.SaveIfNeeded();
    }
    catch (Exception ex)
    {
        await HandleError(rule.Query, ex);
    }

    activeUser.Scheduler.Done(search, foundNew, rule.Urgent, Filters.IsNight(activeUser.Settings), quiet);
    await PollTelegramUpdates();
    await DelayWithCommands(limiter.NextDelayWithJitterMs());
}

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MarktplaatsNotifier;

public partial class CommandHandler
{
    private const int PageSize = 8;

    private static readonly Regex NaturalSearchPattern1 = new Regex(
        @"^(.+?)\s+(?:under|onder|max|below|tot|<=)\s*€?\s*(\d{1,5})\s*(?:euro|eur)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NaturalSearchPattern2 = new Regex(
        @"^(.+?)\s+(?:€\s*(\d{1,5})|(\d{1,5})\s*(?:euro|eur))$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SnoozeDurationPattern = new Regex(
        @"^(\d+)\s*(m|min|mins|h|hr|hrs|u|uur|d|day|days)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly (string Label, string Id)[] Categories =
    {
        ("💻 Computers en Software", "322"),
        ("📺 Audio, Tv en Foto", "31"),
        ("🎮 Spelcomputers en Games", "356"),
        ("📱 Telecommunicatie", "820"),
        ("🚗 Auto-onderdelen", "2600"),
        ("🚲 Fietsen en Brommers", "445"),
        ("🧺 Witgoed en Apparatuur", "537"),
        ("🏠 Huis en Inrichting", "504"),
        ("🔎 All of Marktplaats", "")
    };

    private static readonly string[] MonitorSkipPreset = { "barst", "gebarsten", "strepen", "lijnen", "waterschade" };
    private static readonly string[] WaterSkipPreset = { "waterschade" };
    private static readonly string[] JunkGlobalPreset = { "waterschade", "alleen doos", "lege doos", "handleiding" };

    private static readonly Dictionary<string, (string Title, SearchRule[] Rules)> StarterPacks =
        new Dictionary<string, (string, SearchRule[])>(StringComparer.OrdinalIgnoreCase)
        {
            ["monitors"] = ("🖥️ Broken Monitors", new[]
            {
                new SearchRule { Query = "defect monitor", MaxPrice = 80, Category = "322", SkipWords = MonitorSkipPreset },
                new SearchRule { Query = "monitor geen beeld", MaxPrice = 60, Category = "322", SkipWords = MonitorSkipPreset },
                new SearchRule { Query = "monitor gaat niet aan", MaxPrice = 50, Category = "322", SkipWords = MonitorSkipPreset },
                new SearchRule { Query = "monitor zonder voet", MaxPrice = 50, Category = "322", SkipWords = MonitorSkipPreset },
                new SearchRule { Query = "samsung odyssey defect", MaxPrice = 90, Category = "322", SkipWords = MonitorSkipPreset }
            }),
            ["pc"] = ("💻 PC & GPU Parts", new[]
            {
                new SearchRule { Query = "pc defect", MaxPrice = 100, Category = "322", SkipWords = WaterSkipPreset },
                new SearchRule { Query = "game pc defect", MaxPrice = 150, Category = "322", SkipWords = WaterSkipPreset },
                new SearchRule { Query = "rtx 3060", MaxPrice = 110, Category = "322", SkipWords = WaterSkipPreset },
                new SearchRule { Query = "rtx 3070", MaxPrice = 140, Category = "322", SkipWords = WaterSkipPreset },
                new SearchRule { Query = "ddr4 32gb", MaxPrice = 75, Category = "322", SkipWords = WaterSkipPreset }
            }),
            ["consoles"] = ("🎮 Consoles", new[]
            {
                new SearchRule { Query = "ps5 defect", MaxPrice = 120, Category = "356", SkipWords = WaterSkipPreset },
                new SearchRule { Query = "ps5 geen beeld", MaxPrice = 80, Category = "356", SkipWords = WaterSkipPreset },
                new SearchRule { Query = "ps4 pro defect", MaxPrice = 50, Category = "356", SkipWords = WaterSkipPreset },
                new SearchRule { Query = "joycon drift", MaxPrice = 10, Category = "356", SkipWords = WaterSkipPreset }
            }),
            ["phones"] = ("📱 Phones", new[]
            {
                new SearchRule { Query = "galaxy a55 defect", MaxPrice = 60, Category = "820", SkipWords = WaterSkipPreset },
                new SearchRule { Query = "galaxy a54 defect", MaxPrice = 50, Category = "820", SkipWords = WaterSkipPreset },
                new SearchRule { Query = "iphone scherm kapot", MaxPrice = 80, Category = "820", SkipWords = WaterSkipPreset }
            })
        };

    private readonly UserSession session;
    private readonly UserManager userManager;
    private readonly Dictionary<int, string> categoryNames;
    private readonly Func<UserSession, string> buildStatusText;
    private readonly Func<string, string, Task<Listing?>> fetchSingleListing;
    private readonly Func<UserSession, SearchRule, Task<string>> checkSearchNow;
    private readonly Func<UserSession, string, Task<PriceCheckResult>> lookupMarketPrice;
    private readonly Action reloadHints;

    private string step = "";
    private SearchRule pendingRule = new SearchRule();
    private int editRuleId = 0;
    private int editReturnPage = 0;
    private string targetWatchId = "";
    private string pendingFlipName = "";
    private int editFlipId = 0;
    private bool pendingOnboardingTour = false;

    // 1-tap Undo memory
    private SearchRule? lastDeletedRule;
    private FlipItem? lastDeletedFlip;
    private WatchItem? lastDeletedWatch;

    public CommandHandler(
        UserSession session,
        UserManager userManager,
        Dictionary<int, string> categoryNames,
        Func<UserSession, string> buildStatusText,
        Func<string, string, Task<Listing?>> fetchSingleListing,
        Func<UserSession, SearchRule, Task<string>> checkSearchNow,
        Func<UserSession, string, Task<PriceCheckResult>> lookupMarketPrice,
        Action reloadHints)
    {
        this.session = session;
        this.userManager = userManager;
        this.categoryNames = categoryNames;
        this.buildStatusText = buildStatusText;
        this.fetchSingleListing = fetchSingleListing;
        this.checkSearchNow = checkSearchNow;
        this.lookupMarketPrice = lookupMarketPrice;
        this.reloadHints = reloadHints;
    }

    private string L(string en, string nl) => session.Settings.IsDutch ? nl : en;

    public static object[] GetMenuCommands() => new object[]
    {
        new { command = "menu", description = "Open the main menu" },
        new { command = "add", description = "Add a search or paste a Marktplaats URL" },
        new { command = "list", description = "Browse and edit your searches" },
        new { command = "find", description = "Find a search by keyword" },
        new { command = "price", description = "Check live market price for an item" },
        new { command = "watchlist", description = "View watched listings" },
        new { command = "profit", description = "Flips and profit tracker" },
        new { command = "settings", description = "Settings, language, filters" },
        new { command = "pause", description = "Pause or snooze notifications" },
        new { command = "help", description = "Show all commands" }
    };

    public async Task StartOnboarding()
    {
        step = "onboard_postcode";
        await session.Telegram.SendText(
            "👋 Welcome to Marktplaats Notifier!\n\nLet's set up two quick things so pickup distances work.\n📍 What's your Dutch postcode? (like 1234AB)");
    }

    public async Task HandleDocument(string fileId, string fileName)
    {
        if (!session.OnboardingComplete)
        {
            return;
        }

        string lowerName = fileName.Trim().ToLowerInvariant();
        if (!lowerName.EndsWith(".zip") && lowerName != "searches.txt")
        {
            await session.Telegram.SendText(
                L("ℹ️ Send a <code>marktplaats-backup-*.zip</code> or <code>searches.txt</code> file to restore it.",
                  "ℹ️ Stuur een <code>marktplaats-backup-*.zip</code> of <code>searches.txt</code> bestand om te herstellen."),
                parseMode: "HTML");
            return;
        }

        try
        {
            byte[]? bytes = await session.Telegram.DownloadFileAsync(fileId);
            if (bytes == null || bytes.Length == 0)
            {
                await session.Telegram.SendText("❌ Couldn't download that file from Telegram.");
                return;
            }

            if (lowerName == "searches.txt")
            {
                string target = Path.Combine(session.DataDir, "searches.txt");
                string backup = target + ".before_upload";
                if (File.Exists(target))
                {
                    File.Copy(target, backup, overwrite: true);
                }

                await File.WriteAllBytesAsync(target, bytes);
                string err = session.SearchList.Load();
                if (err != "")
                {
                    if (File.Exists(backup))
                    {
                        File.Move(backup, target, overwrite: true);
                        session.SearchList.Load();
                    }

                    await session.Telegram.SendText($"❌ Invalid searches.txt:\n{err}\n\nKept your previous searches.");
                    return;
                }

                if (File.Exists(backup))
                {
                    File.Delete(backup);
                }

                await session.Telegram.SendText(
                    $"✅ Restored <b>searches.txt</b> ({session.SearchList.Rules.Count} searches loaded)!",
                    Telegram.InlineKeyboard(new[] { new[] { Telegram.CallbackButton("🏠 Menu", "menu:home") } }),
                    parseMode: "HTML");
                return;
            }

            if (session.RestoreFromBackupZip(bytes, out List<string> restored, out string zipError))
            {
                if (restored.Contains("hints.txt", StringComparer.OrdinalIgnoreCase))
                {
                    reloadHints();
                }

                await session.Telegram.SendText(
                    $"✅ <b>Backup restored!</b>\nRestored files: <code>{Encode(string.Join(", ", restored))}</code>\n🔎 {session.SearchList.Rules.Count} searches active.",
                    Telegram.InlineKeyboard(new[] { new[] { Telegram.CallbackButton("🏠 Menu", "menu:home") } }),
                    parseMode: "HTML");
            }
            else
            {
                await session.Telegram.SendText("❌ " + zipError);
            }
        }
        catch (Exception ex)
        {
            await session.Telegram.SendText($"❌ Restore failed: {ex.Message}");
        }
    }

    public async Task HandleMessage(string rawText, long? replyToMessageId)
    {
        string text = NormalizeCommand(rawText.Trim());
        if (text == "")
        {
            return;
        }

        if (!session.OnboardingComplete)
        {
            await HandleOnboardingInput(text);
            return;
        }

        if (replyToMessageId.HasValue &&
            session.RecentByMessageId.TryGetValue(replyToMessageId.Value, out SentListingRef? replied) &&
            await TryHandleReplyShortcut(text, replied))
        {
            return;
        }

        if (text.StartsWith('/'))
        {
            step = "";
        }
        else if (step != "")
        {
            await HandleStepInput(text);
            return;
        }
        else if (MarktplaatsUrlParser.TryExtractListingUrl(text, out string sharedListingUrl, out string sharedItemId))
        {
            await HandleSharedListing(sharedListingUrl, sharedItemId);
            return;
        }
        else if (MarktplaatsUrlParser.IsMarktplaatsSearchUrl(text))
        {
            await StartAddFromUrl(text);
            return;
        }
        else if (text.StartsWith("snooze ", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSnoozeCommand("/" + text);
            return;
        }
        else if (TryParseNaturalSearch(text, out string natQuery, out int natPrice))
        {
            await OfferNaturalSearchAdd(natQuery, natPrice);
            return;
        }

        if (text == "/menu" || text == "/start")
        {
            await SendHomeMenu();
        }
        else if (text == "/cancel")
        {
            step = "";
            await session.Telegram.SendText(L("👍 Cancelled.", "👍 Geannuleerd."));
        }
        else if (text == "/add" || text.StartsWith("/add "))
        {
            string rest = text.Substring(4).Trim();
            if (rest == "")
            {
                await StartInteractiveAdd();
            }
            else if (MarktplaatsUrlParser.IsMarktplaatsSearchUrl(rest))
            {
                await StartAddFromUrl(rest);
            }
            else if (rest.Contains(';'))
            {
                await session.Telegram.SendText(session.SearchList.Add(rest));
            }
            else if (TryParseNaturalSearch(rest, out string q, out int p))
            {
                await OfferNaturalSearchAdd(q, p);
            }
            else
            {
                await StartAddFromText(rest);
            }
        }
        else if (text == "/list")
        {
            await SendListPage(0);
        }
        else if (text == "/find" || text.StartsWith("/find "))
        {
            string query = text.Length > 5 ? text.Substring(5).Trim() : "";
            if (query == "")
            {
                step = "list_find";
                await session.Telegram.SendText(L(
                    "🔍 Type part of a search name to find it (e.g. <code>rtx</code> or <code>monitor</code>). (or /cancel)",
                    "🔍 Typ een deel van een zoekopdracht (bijv. <code>rtx</code> of <code>monitor</code>). (of /cancel)"),
                    parseMode: "HTML");
            }
            else
            {
                await SendFilteredList(query);
            }
        }
        else if (text == "/price" || text.StartsWith("/price "))
        {
            string query = text.Length > 6 ? text.Substring(6).Trim() : "";
            if (query == "")
            {
                step = "price_lookup";
                await session.Telegram.SendText(L(
                    "💶 What item do you want to check the market price for? (e.g. <code>rtx 3070</code>) (or /cancel)",
                    "💶 Van welk item wil je de marktprijs checken? (bijv. <code>rtx 3070</code>) (of /cancel)"),
                    parseMode: "HTML");
            }
            else
            {
                PriceCheckResult result = await lookupMarketPrice(session, query);
                await session.Telegram.SendText(PriceLookup.FormatHtml(result), HomeButtonMarkup(), parseMode: "HTML");
            }
        }
        else if (text == "/watchlist")
        {
            await SendWatchlistMenu();
        }
        else if (text == "/stale" || text.StartsWith("/stale "))
        {
            int minDays = 7;
            string arg = text.Length > 6 ? text.Substring(6).Trim() : "";
            if (arg != "" && int.TryParse(arg, out int parsedDays) && parsedDays >= 1)
            {
                minDays = parsedDays;
            }

            await SendStaleListings(minDays);
        }
        else if (text == "/settings")
        {
            await SendSettingsMenu();
        }
        else if (text == "/ai")
        {
            await SendAiMenu();
        }
        else if (text == "/block" || text.StartsWith("/block "))
        {
            await HandleBlockCommand(text);
        }
        else if (text.StartsWith("/unblock "))
        {
            string target = text.Substring(9).Trim();
            bool removedSeller = session.Blocklist.RemoveSeller(target);
            bool removedWord = session.Blocklist.RemoveGlobalSkipWord(target);
            await session.Telegram.SendText(removedSeller || removedWord
                ? $"✅ Unblocked \"{target}\"."
                : $"❌ \"{target}\" wasn't in your blocklist.");
        }
        else if (text == "/today")
        {
            await session.Telegram.SendText(
                session.Digest.BuildTodaySummaryHtml(),
                HomeButtonMarkup(),
                parseMode: "HTML");
        }
        else if (text == "/stats")
        {
            await session.Telegram.SendText(
                session.Stats.BuildReport(session.SearchList.Rules.Keys),
                HomeButtonMarkup());
        }
        else if (text == "/backup")
        {
            await SendBackupZip();
        }
        else if (text == "/bought" || text.StartsWith("/bought "))
        {
            await HandleBoughtCommand(text);
        }
        else if (text == "/parts" || text.StartsWith("/parts "))
        {
            await HandlePartsCommand(text);
        }
        else if (text == "/sold" || text.StartsWith("/sold "))
        {
            await HandleSoldCommand(text);
        }
        else if (text == "/profit")
        {
            await SendFlipsMenu();
        }
        else if (text == "/test")
        {
            await SendTestListing();
        }
        else if (text == "/logs")
        {
            if (!session.IsOwner)
            {
                await session.Telegram.SendText("🔒 Only the bot owner can view system logs.");
            }
            else
            {
                await session.Telegram.SendText(BotLog.GetRecent(25), HomeButtonMarkup());
            }
        }
        else if (text == "/invite")
        {
            if (!session.IsOwner)
            {
                await session.Telegram.SendText("🔒 Only the bot owner can create invite codes.");
            }
            else
            {
                string code = userManager.CreateInviteCode();
                await session.Telegram.SendText(
                    $"🎟️ <b>One-time invite code</b> (valid 24 hours):\n\n" +
                    $"Tell your friend to open this bot in Telegram and send:\n" +
                    $"<code>/join {code}</code>",
                    parseMode: "HTML");
            }
        }
        else if (text == "/users")
        {
            if (!session.IsOwner)
            {
                await session.Telegram.SendText("🔒 Only the bot owner can manage users.");
            }
            else
            {
                await SendUsersMenu();
            }
        }
        else if (text == "/remove")
        {
            await session.Telegram.SendText(
                "Type the search after /remove (like <code>/remove rtx 3070</code>), or use /list to delete with buttons.",
                parseMode: "HTML");
        }
        else if (text.StartsWith("/remove "))
        {
            string target = text.Substring(8).Trim();
            SearchRule? snapshot = session.SearchList.Rules.GetValueOrDefault(target);
            string reply = session.SearchList.Remove(target, out string removedKey);
            if (removedKey != "")
            {
                lastDeletedRule = snapshot;
                session.NewestIdPerSearch.Remove(removedKey);
                session.Scheduler.Remove(removedKey);
                session.Stats.Remove(removedKey);
                await session.Telegram.SendText(reply, UndoMarkup("undo:search", "list:0", L("📋 Searches", "📋 Zoekopdrachten")));
            }
            else
            {
                await session.Telegram.SendText(reply);
            }
        }
        else if (text == "/pause")
        {
            await SendPauseMenu();
        }
        else if (text == "/snooze" || text.StartsWith("/snooze "))
        {
            await HandleSnoozeCommand(text);
        }
        else if (text == "/resume")
        {
            session.Paused = false;
            session.PausedUntil = null;
            await session.Telegram.SendText(L("▶️ Searching again.", "▶️ Zoeken hervat."));
        }
        else if (text == "/status")
        {
            await session.Telegram.SendText(buildStatusText(session), HomeButtonMarkup());
        }
        else if (text == "/help")
        {
            await session.Telegram.SendText(BuildHelpText(), HomeButtonMarkup(), parseMode: "HTML");
        }
    }

    private async Task HandleOnboardingInput(string text)
    {
        if (step == "onboard_km")
        {
            string clean = text.ToLowerInvariant().Replace("km", "").Trim();
            if (int.TryParse(clean, out int km) && km >= 0)
            {
                session.PickupKm = km;
                session.OnboardingComplete = true;
                session.SaveProfile();
                step = "";
                pendingOnboardingTour = true;
                await session.Telegram.SendText(
                    $"✅ All set! (Postcode {session.Postcode}, pickup {session.PickupKm} km)");
                await SendStarterPacksMenu();
            }
            else
            {
                await session.Telegram.SendText("❌ Type a whole number in km (0 = shipping only), like 10.");
            }

            return;
        }

        string cleanPostcode = text.Trim().ToUpperInvariant().Replace(" ", "");
        if (cleanPostcode.Length == 6 && int.TryParse(cleanPostcode.Substring(0, 4), out _) &&
            char.IsLetter(cleanPostcode[4]) && char.IsLetter(cleanPostcode[5]))
        {
            session.Postcode = cleanPostcode;
            session.SaveProfile();
            step = "onboard_km";
            await session.Telegram.SendText("🚗 How many km will you travel to pick something up? (0 = shipping only)");
        }
        else
        {
            await session.Telegram.SendText("❌ That's not a Dutch postcode. Use 4 numbers and 2 letters, like 1234AB.");
        }
    }

    private async Task<bool> TryHandleReplyShortcut(string text, SentListingRef listing)
    {
        string lower = text.ToLowerInvariant().Trim();

        if (lower == "watch" || lower.StartsWith("watch "))
        {
            double targetPrice = 0;
            if (lower.Length > 5)
            {
                string priceArg = lower.Substring(5).Replace("€", "").Trim();
                if (!double.TryParse(priceArg, NumberStyles.Float, CultureInfo.InvariantCulture, out targetPrice) || targetPrice < 0)
                {
                    await session.Telegram.SendText("❌ Use <code>watch</code> or <code>watch 80</code>.", parseMode: "HTML");
                    return true;
                }
            }

            session.Watchlist.AddOrUpdate(new WatchItem
            {
                Id = listing.Id,
                Title = listing.Title,
                Link = listing.Link,
                LastPrice = listing.Price,
                HighestBid = listing.HighestBid,
                BidCount = listing.BidCount,
                TargetPrice = targetPrice,
                AddedAt = DateTime.Now,
                LastCheckedAt = DateTime.Now
            });

            string targetNote = targetPrice > 0 ? $" (target ≤ {Listing.FormatEuro(targetPrice)})" : "";
            await session.Telegram.SendText($"👀 Watching <b>{Encode(listing.Title)}</b>{targetNote}.", parseMode: "HTML");
            return true;
        }

        if (lower.StartsWith("bought "))
        {
            string priceArg = lower.Substring(7).Replace("€", "").Trim().Replace(",", ".");
            if (!double.TryParse(priceArg, NumberStyles.Float, CultureInfo.InvariantCulture, out double buyPrice) || buyPrice < 0)
            {
                await session.Telegram.SendText("❌ Reply with <code>bought 50</code> to log the purchase price.", parseMode: "HTML");
                return true;
            }

            FlipItem flip = session.FlipTracker.AddPurchase(listing.Title, buyPrice);
            await session.Telegram.SendText(
                $"🛒 Logged purchase #{flip.Id}: <b>{Encode(flip.Name)}</b> for {Listing.FormatEuro(flip.BuyPrice)}.",
                Telegram.InlineKeyboard(new[] { new[] { Telegram.CallbackButton("💰 Open Flips", "flip:home") } }),
                parseMode: "HTML");
            return true;
        }

        if (lower == "block")
        {
            if (string.IsNullOrWhiteSpace(listing.SellerName))
            {
                await session.Telegram.SendText("❌ Couldn't find the seller name on that listing.");
                return true;
            }

            bool added = session.Blocklist.AddSeller(listing.SellerName);
            await session.Telegram.SendText(added
                ? $"🚫 Blocked seller <b>{Encode(listing.SellerName)}</b>."
                : $"ℹ️ Seller <b>{Encode(listing.SellerName)}</b> is already blocked.",
                parseMode: "HTML");
            return true;
        }

        return false;
    }

    private async Task HandleSharedListing(string listingUrl, string itemId)
    {
        Listing? fetched = await fetchSingleListing(listingUrl, itemId);
        string title = fetched != null && fetched.Title != "" ? fetched.Title : $"Listing {itemId}";
        double price = fetched?.Price ?? 0;
        double highestBid = fetched?.HighestBid ?? 0;
        int bidCount = fetched?.BidCount ?? 0;
        string link = fetched != null && fetched.Link != "" ? fetched.Link : listingUrl;

        WatchItem item = session.Watchlist.AddOrUpdate(new WatchItem
        {
            Id = itemId,
            Title = title,
            Link = link,
            LastPrice = price,
            HighestBid = highestBid,
            BidCount = bidCount,
            AddedAt = DateTime.Now,
            LastCheckedAt = DateTime.Now
        });

        string priceStr = price > 0 ? Listing.FormatEuro(price) : (highestBid > 0 ? $"highest bid {Listing.FormatEuro(highestBid)}" : "Bieden");
        string markup = Telegram.InlineKeyboard(new[]
        {
            new[]
            {
                Telegram.CallbackButton("🎯 Set target price", $"wtgt:{item.Id}"),
                Telegram.CallbackButton("❌ Unwatch", $"wdel:{item.Id}")
            }
        });

        await session.Telegram.SendText(
            $"👀 <b>Now watching:</b>\n{Encode(item.Title)}\n💶 Current: {priceStr}\n\nI'll ping you on price drops, new higher bids, or if it gets reserved/removed.",
            markup,
            parseMode: "HTML");
    }

    private async Task HandleBlockCommand(string text)
    {
        string rest = text.Length > 6 ? text.Substring(6).Trim() : "";
        if (rest == "")
        {
            await SendBlocklistMenu();
            return;
        }

        if (rest.StartsWith("word ", StringComparison.OrdinalIgnoreCase))
        {
            string[] words = ParseSkipWords(rest.Substring(5));
            int added = session.Blocklist.AddGlobalSkipWords(words);
            await session.Telegram.SendText(added > 0
                ? $"🚫 Added {added} global skip word(s)."
                : "ℹ️ Those word(s) were already in your global skip list.");
            return;
        }

        string seller = rest.StartsWith("seller ", StringComparison.OrdinalIgnoreCase)
            ? rest.Substring(7).Trim()
            : rest;

        bool addedSeller = session.Blocklist.AddSeller(seller);
        await session.Telegram.SendText(addedSeller
            ? $"🚫 Blocked seller \"{seller}\"."
            : $"ℹ️ \"{seller}\" is already blocked.");
    }

    public async Task HandleCallback(string callbackId, long messageId, string data)
    {
        string[] parts = data.Split(':');
        string action = parts.Length > 0 ? parts[0] : "";

        // Main Home Menu navigation
        if (action == "menu" && parts.Length >= 2)
        {
            step = "";
            await HandleMenuCallback(callbackId, messageId, parts[1]);
            return;
        }

        // 1-tap Undo
        if (action == "undo" && parts.Length >= 2)
        {
            await HandleUndoCallback(callbackId, messageId, parts[1]);
            return;
        }

        // Starter packs
        if (action == "pack" && parts.Length >= 2)
        {
            await HandleStarterPackCallback(callbackId, messageId, parts[1]);
            return;
        }

        // Pause / Snooze buttons
        if (action == "snz" && parts.Length >= 2)
        {
            await HandleSnoozeCallback(callbackId, messageId, parts[1]);
            return;
        }

        // Button-driven Flips UI
        if (action == "flip" && parts.Length >= 2)
        {
            await HandleFlipCallback(callbackId, messageId, parts);
            return;
        }

        // Live "Check now" button inside a search detail card
        if (action == "chk" && parts.Length >= 3 &&
            int.TryParse(parts[1], out int chkId) && int.TryParse(parts[2], out int chkPage))
        {
            SearchRule? rule = session.SearchList.FindById(chkId);
            if (rule == null)
            {
                await session.Telegram.AnswerCallbackQuery(callbackId, "Search not found");
                return;
            }

            await session.Telegram.AnswerCallbackQuery(callbackId, "⚡ Checking Marktplaats...");
            string report = await checkSearchNow(session, rule);
            string backMarkup = Telegram.InlineKeyboard(new[]
            {
                new[] { Telegram.CallbackButton("⬅️ Back to search", $"item:{rule.Id}:{chkPage}") }
            });
            await session.Telegram.SendText(report, backMarkup, parseMode: "HTML");
            return;
        }

        // Inline "👀 Watch" / "✅ Watching" toggle right under a listing card
        if (action == "w" && parts.Length >= 2)
        {
            await HandleInlineWatchToggle(callbackId, messageId, parts[1]);
            return;
        }

        if (action == "wtgt" && parts.Length >= 2)
        {
            string itemId = parts[1];
            WatchItem? item = session.Watchlist.Get(itemId);
            if (item == null)
            {
                await session.Telegram.AnswerCallbackQuery(callbackId, "Not in watchlist");
                return;
            }

            targetWatchId = itemId;
            step = "watch_target";
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await session.Telegram.SendText(
                $"🎯 Send your target price for <b>{Encode(item.Title)}</b> (e.g. 80, or 0 for any drop). (or /cancel)",
                parseMode: "HTML");
            return;
        }

        if (action == "wdel" && parts.Length >= 2)
        {
            string itemId = parts[1];
            lastDeletedWatch = session.Watchlist.Get(itemId);
            session.Watchlist.Remove(itemId);
            await session.Telegram.AnswerCallbackQuery(callbackId, "Stopped watching");
            string name = lastDeletedWatch?.Title ?? itemId;
            await session.Telegram.EditMessage(
                messageId,
                $"❌ Stopped watching <b>{Encode(name)}</b>.",
                UndoMarkup("undo:watch", "menu:watch", L("👀 Watchlist", "👀 Volglijst")),
                parseMode: "HTML");
            return;
        }

        if (action == "ws" && parts.Length >= 2)
        {
            string itemId = parts[1];
            HistoryEntry? hist = session.History.GetById(itemId);
            if (hist == null)
            {
                await session.Telegram.AnswerCallbackQuery(callbackId, "Listing not found");
                return;
            }

            session.Watchlist.AddOrUpdate(new WatchItem
            {
                Id = hist.Id,
                Title = hist.Title,
                Link = $"https://link.marktplaats.nl/{hist.Id}",
                LastPrice = hist.Price,
                AddedAt = DateTime.Now,
                LastCheckedAt = DateTime.MinValue
            });
            await session.Telegram.AnswerCallbackQuery(callbackId, $"👀 Watching {hist.Title}");
            return;
        }

        if (action == "list" && parts.Length >= 2)
        {
            if (parts[1] == "find")
            {
                step = "list_find";
                await session.Telegram.AnswerCallbackQuery(callbackId);
                await session.Telegram.SendText(L(
                    "🔍 Type part of a search name to find it (e.g. <code>rtx</code> or <code>monitor</code>). (or /cancel)",
                    "🔍 Typ een deel van een zoekopdracht (bijv. <code>rtx</code> of <code>monitor</code>). (of /cancel)"),
                    parseMode: "HTML");
                return;
            }

            if (int.TryParse(parts[1], out int page))
            {
                step = "";
                await session.Telegram.AnswerCallbackQuery(callbackId);
                await SendListPage(page, messageId);
                return;
            }
        }
        else if (action == "item" && parts.Length >= 3 &&
                 int.TryParse(parts[1], out int itemId) && int.TryParse(parts[2], out int itemPage))
        {
            step = "";
            SearchRule? rule = session.SearchList.FindById(itemId);
            if (rule == null)
            {
                await session.Telegram.AnswerCallbackQuery(callbackId, "Search not found");
                await SendListPage(itemPage, messageId);
                return;
            }

            await session.Telegram.AnswerCallbackQuery(callbackId);
            await SendSearchDetail(rule, itemPage, messageId);
        }
        else if (action == "turg" && parts.Length >= 3 &&
                 int.TryParse(parts[1], out int turgId) && int.TryParse(parts[2], out int turgPage))
        {
            SearchRule? rule = session.SearchList.FindById(turgId);
            if (rule == null)
            {
                await session.Telegram.AnswerCallbackQuery(callbackId, "Search not found");
                return;
            }

            rule.Urgent = !rule.Urgent;
            session.SearchList.UpdateRule(rule);
            await session.Telegram.AnswerCallbackQuery(callbackId, rule.Urgent ? "Marked urgent 🚨" : "Set to normal");
            await SendSearchDetail(rule, turgPage, messageId);
        }
        else if (action == "del" && parts.Length >= 3 &&
                 int.TryParse(parts[1], out int delId) && int.TryParse(parts[2], out int delPage))
        {
            step = "";
            SearchRule? rule = session.SearchList.FindById(delId);
            if (rule == null)
            {
                await session.Telegram.AnswerCallbackQuery(callbackId, "Already removed");
                await SendListPage(delPage, messageId);
                return;
            }

            lastDeletedRule = rule;
            session.SearchList.Remove(rule.Query, out string removedKey);
            if (removedKey != "")
            {
                session.NewestIdPerSearch.Remove(removedKey);
                session.Scheduler.Remove(removedKey);
                session.Stats.Remove(removedKey);
            }

            await session.Telegram.AnswerCallbackQuery(callbackId, $"Removed {rule.Query}");
            await session.Telegram.EditMessage(
                messageId,
                $"🗑️ Removed <b>{Encode(rule.Query)}</b> (€{rule.MaxPrice}).",
                UndoMarkup("undo:search", $"list:{delPage}", L("📋 Back to list", "📋 Terug naar lijst")),
                parseMode: "HTML");
        }
        else if (action == "editp" && parts.Length >= 3 &&
                 int.TryParse(parts[1], out int editPriceId) && int.TryParse(parts[2], out int editPricePage))
        {
            SearchRule? rule = session.SearchList.FindById(editPriceId);
            if (rule == null)
            {
                await session.Telegram.AnswerCallbackQuery(callbackId, "Search not found");
                return;
            }

            editRuleId = rule.Id;
            editReturnPage = editPricePage;
            step = "edit_price";
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await session.Telegram.SendText(
                $"💶 Send the new max price for <b>{Encode(rule.Query)}</b> (currently €{rule.MaxPrice}). (or /cancel)",
                parseMode: "HTML");
        }
        else if (action == "edits" && parts.Length >= 3 &&
                 int.TryParse(parts[1], out int editSkipId) && int.TryParse(parts[2], out int editSkipPage))
        {
            SearchRule? rule = session.SearchList.FindById(editSkipId);
            if (rule == null)
            {
                await session.Telegram.AnswerCallbackQuery(callbackId, "Search not found");
                return;
            }

            editRuleId = rule.Id;
            editReturnPage = editSkipPage;
            step = "edit_skip";
            string current = rule.SkipWords.Length > 0 ? string.Join(", ", rule.SkipWords) : "none";
            string markup = Telegram.InlineKeyboard(new[]
            {
                new[] { Telegram.CallbackButton("🖥️ Monitor faults preset", $"eskp:{rule.Id}:monitor:{editSkipPage}") },
                new[] { Telegram.CallbackButton("💧 Waterschade only", $"eskp:{rule.Id}:water:{editSkipPage}") },
                new[] { Telegram.CallbackButton("🧹 Clear skip words", $"eskp:{rule.Id}:none:{editSkipPage}") },
                new[] { Telegram.CallbackButton("⬅️ Back", $"item:{rule.Id}:{editSkipPage}") }
            });
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await session.Telegram.EditMessage(
                messageId,
                $"🚫 Edit skip words for <b>{Encode(rule.Query)}</b>\nCurrent: <code>{Encode(current)}</code>\n\nPick a preset or type new comma-separated words:",
                markup,
                parseMode: "HTML");
        }
        else if (action == "eskp" && parts.Length >= 4 &&
                 int.TryParse(parts[1], out int eskpId) && int.TryParse(parts[3], out int eskpPage))
        {
            SearchRule? rule = session.SearchList.FindById(eskpId);
            step = "";
            if (rule == null)
            {
                await session.Telegram.AnswerCallbackQuery(callbackId, "Search not found");
                return;
            }

            rule.SkipWords = parts[2] switch
            {
                "monitor" => MonitorSkipPreset.ToArray(),
                "water" => WaterSkipPreset.ToArray(),
                _ => Array.Empty<string>()
            };
            session.SearchList.UpdateRule(rule);
            await session.Telegram.AnswerCallbackQuery(callbackId, "Skip words updated");
            await SendSearchDetail(rule, eskpPage, messageId);
        }
        else if (action == "nat" && parts.Length >= 2 && parts[1] == "cat")
        {
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await AskCategoryForAdd(messageId);
        }
        else if (action == "url" && parts.Length >= 2)
        {
            await session.Telegram.AnswerCallbackQuery(callbackId);
            if (parts[1] == "save")
            {
                step = "";
                await session.Telegram.EditMessage(messageId, session.SearchList.AddRule(pendingRule), HomeButtonMarkup());
            }
            else if (parts[1] == "skip")
            {
                await AskSkipWordsForAdd(messageId);
            }
            else if (parts[1] == "price")
            {
                step = "add_price";
                await session.Telegram.EditMessage(
                    messageId,
                    $"💶 What's the max price for <b>{Encode(pendingRule.Query)}</b>? (or /cancel)",
                    parseMode: "HTML");
            }
            else
            {
                step = "";
                await session.Telegram.EditMessage(messageId, L("👍 Cancelled.", "👍 Geannuleerd."));
            }
        }
        else if (action == "addcat" && parts.Length >= 2)
        {
            pendingRule.Category = parts[1];
            pendingRule.SubCategory = 0;
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await AskSkipWordsForAdd(messageId);
        }
        else if (action == "addskip" && parts.Length >= 2)
        {
            pendingRule.SkipWords = parts[1] switch
            {
                "monitor" => MonitorSkipPreset.ToArray(),
                "water" => WaterSkipPreset.ToArray(),
                _ => Array.Empty<string>()
            };
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await AskUrgentForAdd(messageId);
        }
        else if (action == "addurg" && parts.Length >= 2)
        {
            pendingRule.Urgent = parts[1] == "yes";
            step = "";
            string result = session.SearchList.AddRule(pendingRule);
            await session.Telegram.AnswerCallbackQuery(callbackId, "Saved!");
            await session.Telegram.EditMessage(messageId, result, HomeButtonMarkup());
        }
        else if (action == "set" && parts.Length >= 2)
        {
            await HandleSettingsCallback(callbackId, messageId, parts[1]);
        }
        else if (action == "blk" && parts.Length >= 2)
        {
            await HandleBlockCallback(callbackId, messageId, parts);
        }
        else if (action == "ai" && parts.Length >= 2)
        {
            await HandleAiCallback(callbackId, messageId, parts);
        }
        else if (action == "usr" && parts.Length >= 3 && parts[1] == "del" && session.IsOwner)
        {
            string targetId = parts[2];
            bool removed = userManager.RemoveUser(targetId);
            await session.Telegram.AnswerCallbackQuery(callbackId, removed ? "User removed" : "Couldn't remove");
            await SendUsersMenu(messageId);
        }
        else
        {
            await session.Telegram.AnswerCallbackQuery(callbackId);
        }
    }

    private async Task HandleMenuCallback(string callbackId, long messageId, string target)
    {
        await session.Telegram.AnswerCallbackQuery(callbackId);
        switch (target)
        {
            case "home":
                await SendHomeMenu(messageId);
                break;
            case "add":
                await StartInteractiveAdd();
                break;
            case "watch":
                await SendWatchlistMenu(messageId);
                break;
            case "stale":
                await SendStaleListings(7);
                break;
            case "today":
                await session.Telegram.EditMessage(messageId, session.Digest.BuildTodaySummaryHtml(), HomeButtonMarkup(), parseMode: "HTML");
                break;
            case "stats":
                await session.Telegram.EditMessage(messageId, session.Stats.BuildReport(session.SearchList.Rules.Keys), HomeButtonMarkup());
                break;
            case "packs":
                await SendStarterPacksMenu(messageId);
                break;
            case "settings":
                await SendSettingsMenu(messageId);
                break;
            case "ai":
                await SendAiMenu(messageId);
                break;
            case "block":
                await SendBlocklistMenu(messageId);
                break;
            case "pause":
                await SendPauseMenu(messageId);
                break;
            case "backup":
                await SendBackupZip();
                break;
            case "test":
                await SendTestListing();
                break;
            case "users" when session.IsOwner:
                await SendUsersMenu(messageId);
                break;
            case "logs" when session.IsOwner:
                await session.Telegram.EditMessage(messageId, BotLog.GetRecent(25), HomeButtonMarkup());
                break;
        }
    }

    private async Task HandleUndoCallback(string callbackId, long messageId, string kind)
    {
        if (kind == "search" && lastDeletedRule != null)
        {
            SearchRule restore = lastDeletedRule;
            lastDeletedRule = null;
            string res = session.SearchList.AddRule(restore);
            await session.Telegram.AnswerCallbackQuery(callbackId, "Restored!");
            await session.Telegram.EditMessage(messageId, $"↩️ {res}", HomeButtonMarkup());
        }
        else if (kind == "flip" && lastDeletedFlip != null)
        {
            FlipItem restore = lastDeletedFlip;
            lastDeletedFlip = null;
            session.FlipTracker.Restore(restore);
            await session.Telegram.AnswerCallbackQuery(callbackId, "Restored!");
            await SendFlipsMenu(messageId);
        }
        else if (kind == "watch" && lastDeletedWatch != null)
        {
            WatchItem restore = lastDeletedWatch;
            lastDeletedWatch = null;
            session.Watchlist.AddOrUpdate(restore);
            await session.Telegram.AnswerCallbackQuery(callbackId, "Restored!");
            await SendWatchlistMenu(messageId);
        }
        else
        {
            await session.Telegram.AnswerCallbackQuery(callbackId, "Nothing to undo");
        }
    }

    private async Task HandleStarterPackCallback(string callbackId, long messageId, string packId)
    {
        if (packId == "empty")
        {
            await session.Telegram.AnswerCallbackQuery(callbackId, "Starting empty");
            await session.Telegram.EditMessage(
                messageId,
                L("✅ Ready! Send /add, paste a Marktplaats URL, or open /menu anytime.",
                  "✅ Klaar! Stuur /add, plak een Marktplaats link, of open /menu."),
                HomeButtonMarkup());
        }
        else if (StarterPacks.TryGetValue(packId, out var pack))
        {
            int added = 0;
            foreach (SearchRule template in pack.Rules)
            {
                if (!session.SearchList.Rules.ContainsKey(template.Query))
                {
                    session.SearchList.AddRule(new SearchRule
                    {
                        Query = template.Query,
                        MaxPrice = template.MaxPrice,
                        Category = template.Category,
                        SubCategory = template.SubCategory,
                        SkipWords = template.SkipWords.ToArray(),
                        Urgent = template.Urgent
                    });
                    added++;
                }
            }

            await session.Telegram.AnswerCallbackQuery(callbackId, $"Added {added} searches!");
            await session.Telegram.EditMessage(
                messageId,
                $"🎁 Added <b>{added}</b> searches from <b>{Encode(pack.Title)}</b>! ({session.SearchList.Rules.Count} total)\nYou can tweak any price in /list.",
                Telegram.InlineKeyboard(new[]
                {
                    new[]
                    {
                        Telegram.CallbackButton("🎁 Add another pack", "menu:packs"),
                        Telegram.CallbackButton("📋 My Searches", "list:0")
                    },
                    new[] { Telegram.CallbackButton("🏠 Menu", "menu:home") }
                }),
                parseMode: "HTML");
        }
        else
        {
            await session.Telegram.AnswerCallbackQuery(callbackId);
            return;
        }

        if (pendingOnboardingTour)
        {
            pendingOnboardingTour = false;
            await session.Telegram.SendText(
                "Here's what a listing looks like:\n" +
                "• <b>Great deal</b> = well under the typical price\n" +
                "• <b>Watch</b> = tap to track price drops and bids\n" +
                "• <b>/menu</b> = buttons for everything else",
                parseMode: "HTML");
            await SendTestListing();
        }
    }

    private async Task HandleInlineWatchToggle(string callbackId, long messageId, string itemId)
    {
        SentListingRef? recent = session.RecentById.GetValueOrDefault(itemId);
        HistoryEntry? hist = session.History.GetById(itemId);
        string fallbackUrl = recent?.Link ?? $"https://link.marktplaats.nl/{itemId}";

        if (session.Watchlist.Contains(itemId))
        {
            WatchItem? existing = session.Watchlist.Get(itemId);
            string url = existing != null && existing.Link != "" ? existing.Link : fallbackUrl;
            session.Watchlist.Remove(itemId);
            await session.Telegram.EditReplyMarkup(messageId, Telegram.ListingButtons(url, itemId, watching: false, session.Settings.IsDutch));
            await session.Telegram.AnswerCallbackQuery(callbackId, L("Stopped watching", "Gestopt met volgen"));
            return;
        }

        string title = recent?.Title ?? hist?.Title ?? $"Listing {itemId}";
        double price = recent?.Price ?? hist?.Price ?? 0;

        session.Watchlist.AddOrUpdate(new WatchItem
        {
            Id = itemId,
            Title = title,
            Link = fallbackUrl,
            LastPrice = price,
            HighestBid = recent?.HighestBid ?? 0,
            BidCount = recent?.BidCount ?? 0,
            AddedAt = DateTime.Now,
            LastCheckedAt = DateTime.Now
        });

        await session.Telegram.EditReplyMarkup(messageId, Telegram.ListingButtons(fallbackUrl, itemId, watching: true, session.Settings.IsDutch));
        await session.Telegram.AnswerCallbackQuery(callbackId, L("👀 Watching! Use /watchlist to manage.", "👀 Gevolgd! Beheer via /watchlist."));
    }

    private async Task HandleBlockCallback(string callbackId, long messageId, string[] parts)
    {
        string sub = parts[1];
        if (sub == "addseller")
        {
            step = "block_seller";
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await session.Telegram.SendText("🚫 Send the exact seller name to block. (or /cancel)");
        }
        else if (sub == "addword")
        {
            step = "block_word";
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await session.Telegram.SendText("🚫 Send global skip word(s), comma-separated (e.g. <code>waterschade,alleen doos</code>). (or /cancel)",
                parseMode: "HTML");
        }
        else if (sub == "preset")
        {
            int added = session.Blocklist.AddGlobalSkipWords(JunkGlobalPreset);
            await session.Telegram.AnswerCallbackQuery(callbackId, added > 0 ? $"Added {added} words" : "Already added");
            await SendBlocklistMenu(messageId);
        }
        else if (sub == "rmseller" && parts.Length >= 3 && int.TryParse(parts[2], out int sIdx) &&
                 sIdx >= 0 && sIdx < session.Blocklist.BlockedSellers.Count)
        {
            string name = session.Blocklist.BlockedSellers[sIdx];
            session.Blocklist.RemoveSeller(name);
            await session.Telegram.AnswerCallbackQuery(callbackId, $"Unblocked {name}");
            await SendBlocklistMenu(messageId);
        }
        else if (sub == "rmword" && parts.Length >= 3 && int.TryParse(parts[2], out int wIdx) &&
                 wIdx >= 0 && wIdx < session.Blocklist.GlobalSkipWords.Count)
        {
            string word = session.Blocklist.GlobalSkipWords[wIdx];
            session.Blocklist.RemoveGlobalSkipWord(word);
            await session.Telegram.AnswerCallbackQuery(callbackId, $"Removed '{word}'");
            await SendBlocklistMenu(messageId);
        }
        else
        {
            await session.Telegram.AnswerCallbackQuery(callbackId);
        }
    }

    private async Task HandleAiCallback(string callbackId, long messageId, string[] parts)
    {
        string sub = parts[1];
        if (sub == "toggle")
        {
            if (!session.Settings.AiEnabled &&
                !session.Settings.AiProvider.Equals("ollama", StringComparison.OrdinalIgnoreCase) &&
                session.AiGrader.GetApiKey(session.Settings.AiProvider) == "")
            {
                step = "ai_key";
                await session.Telegram.AnswerCallbackQuery(callbackId, "Add an API key first");
                await PromptAiApiKey();
                return;
            }

            session.Settings.AiEnabled = !session.Settings.AiEnabled;
            session.Settings.Save();
            await session.Telegram.AnswerCallbackQuery(callbackId, session.Settings.AiEnabled ? "AI ON 🤖" : "AI OFF");
            await SendAiMenu(messageId);
        }
        else if (sub == "prov" && parts.Length >= 3)
        {
            string provider = parts[2].ToLowerInvariant();
            session.Settings.AiProvider = provider;
            session.Settings.AiModel = AiGrader.DefaultModelFor(provider);
            session.Settings.Save();
            await session.Telegram.AnswerCallbackQuery(callbackId, $"Switched to {provider}");
            await SendAiMenu(messageId);
        }
        else if (sub == "key")
        {
            step = "ai_key";
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await PromptAiApiKey();
        }
        else if (sub == "model")
        {
            step = "ai_model";
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await session.Telegram.SendText(
                $"🎛️ Send the model name for <b>{Encode(session.Settings.AiProvider)}</b> (currently <code>{Encode(session.Settings.AiModel)}</code>).\n" +
                "Examples: <code>gemini-2.5-flash</code>, <code>gemini-2.5-flash-lite</code>, <code>claude-haiku-4-5</code>, <code>gpt-4o-mini</code>, <code>llama3.2-vision</code>. (or /cancel)",
                parseMode: "HTML");
        }
        else if (sub == "limit")
        {
            step = "ai_limit";
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await session.Telegram.SendText($"📊 Send max AI calls per day (currently {session.Settings.AiDailyLimit}, range 1–500). (or /cancel)");
        }
        else if (sub == "url")
        {
            step = "ai_url";
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await session.Telegram.SendText($"🏠 Send your Ollama URL (currently <code>{Encode(session.Settings.AiBaseUrl)}</code>). (or /cancel)", parseMode: "HTML");
        }
        else if (sub == "autoskip")
        {
            session.Settings.AiAutoSkip = !session.Settings.AiAutoSkip;
            session.Settings.Save();
            await session.Telegram.AnswerCallbackQuery(callbackId, session.Settings.AiAutoSkip ? "Auto-skipping AI SKIP" : "Showing AI SKIP with tag");
            await SendAiMenu(messageId);
        }
        else
        {
            await session.Telegram.AnswerCallbackQuery(callbackId);
        }
    }

    private async Task PromptAiApiKey()
    {
        string providerNote = "";
        foreach (var p in AiGrader.Providers)
        {
            if (p.Id.Equals(session.Settings.AiProvider, StringComparison.OrdinalIgnoreCase))
            {
                providerNote = p.Note;
                break;
            }
        }

        await session.Telegram.SendText(
            $"🔑 Paste your API key for <b>{Encode(session.Settings.AiProvider)}</b>.\n" +
            $"<i>{Encode(providerNote)}</i>\n\n" +
            "Your key is saved locally in <code>ai_key.txt</code> and never included in /backup. (or /cancel)",
            parseMode: "HTML");
    }

    private async Task HandleSettingsCallback(string callbackId, long messageId, string settingAction)
    {
        if (settingAction == "lang")
        {
            session.Settings.Language = session.Settings.IsDutch ? "en" : "nl";
            session.Settings.Save();
            await session.Telegram.AnswerCallbackQuery(callbackId, session.Settings.IsDutch ? "Taal: Nederlands 🇳🇱" : "Language: English 🇬🇧");
            await SendSettingsMenu(messageId);
        }
        else if (settingAction == "postcode")
        {
            step = "set_postcode";
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await session.Telegram.SendText($"📍 Send your new Dutch postcode (currently {session.Postcode}), like 1234AB. (or /cancel)");
        }
        else if (settingAction == "km")
        {
            step = "set_km";
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await session.Telegram.SendText($"🚗 Send your max pickup distance in km (currently {session.PickupKm} km, 0 = shipping only). (or /cancel)");
        }
        else if (settingAction == "night_toggle")
        {
            session.Settings.QuietNightsEnabled = !session.Settings.QuietNightsEnabled;
            session.Settings.Save();
            await session.Telegram.AnswerCallbackQuery(callbackId, session.Settings.QuietNightsEnabled ? "Quiet nights ON" : "Quiet nights OFF");
            await SendSettingsMenu(messageId);
        }
        else if (settingAction == "night_hours")
        {
            step = "set_night_hours";
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await session.Telegram.SendText("🕐 Send quiet night hours as <code>start-end</code>, e.g. <code>23-7</code> or <code>22-8</code>. (or /cancel)", parseMode: "HTML");
        }
        else if (settingAction == "biz")
        {
            session.Settings.SkipLikelyBusinesses = !session.Settings.SkipLikelyBusinesses;
            session.Settings.Save();
            await session.Telegram.AnswerCallbackQuery(callbackId, session.Settings.SkipLikelyBusinesses ? "Skipping likely businesses" : "Showing likely businesses");
            await SendSettingsMenu(messageId);
        }
        else if (settingAction == "descprice")
        {
            session.Settings.SkipHighDescriptionPrice = !session.Settings.SkipHighDescriptionPrice;
            session.Settings.Save();
            await session.Telegram.AnswerCallbackQuery(callbackId, session.Settings.SkipHighDescriptionPrice ? "Skipping high desc. prices" : "Showing all desc. prices");
            await SendSettingsMenu(messageId);
        }
        else if (settingAction == "digest")
        {
            session.Settings.MorningDigestEnabled = !session.Settings.MorningDigestEnabled;
            session.Settings.Save();
            await session.Telegram.AnswerCallbackQuery(callbackId, session.Settings.MorningDigestEnabled ? "Morning digest ON" : "Morning digest OFF");
            await SendSettingsMenu(messageId);
        }
        else if (settingAction == "fastcat")
        {
            session.Settings.FastCategoryMode = !session.Settings.FastCategoryMode;
            session.Settings.Save();
            await session.Telegram.AnswerCallbackQuery(callbackId, session.Settings.FastCategoryMode ? "Fast 35s category mode ON" : "Fast category mode OFF");
            await SendSettingsMenu(messageId);
        }
        else if (settingAction == "tweakers")
        {
            session.Settings.EnableTweakers = !session.Settings.EnableTweakers;
            session.Settings.Save();
            await session.Telegram.AnswerCallbackQuery(callbackId, session.Settings.EnableTweakers ? "Tweakers V&A ON" : "Tweakers V&A OFF");
            await SendSettingsMenu(messageId);
        }
        else if (settingAction == "2dehands")
        {
            session.Settings.EnableTweedehands = !session.Settings.EnableTweedehands;
            session.Settings.Save();
            await session.Telegram.AnswerCallbackQuery(callbackId, session.Settings.EnableTweedehands ? "2dehands.be ON" : "2dehands.be OFF");
            await SendSettingsMenu(messageId);
        }
        else if (settingAction == "coolblue")
        {
            session.Settings.EnableCoolblue = !session.Settings.EnableCoolblue;
            session.Settings.Save();
            await session.Telegram.AnswerCallbackQuery(callbackId, session.Settings.EnableCoolblue ? "Coolblue Tweedekans ON" : "Coolblue Tweedekans OFF");
            await SendSettingsMenu(messageId);
        }
        else if (settingAction == "vinted")
        {
            session.Settings.EnableVinted = !session.Settings.EnableVinted;
            session.Settings.Save();
            await session.Telegram.AnswerCallbackQuery(callbackId, session.Settings.EnableVinted ? "Vinted ON" : "Vinted OFF");
            await SendSettingsMenu(messageId);
        }
        else
        {
            await session.Telegram.AnswerCallbackQuery(callbackId);
        }
    }

    private async Task SendHomeMenu(long? editMessageId = null)
    {
        string statusLine;
        if (session.Paused && session.PausedUntil.HasValue)
        {
            statusLine = L($"⏰ Snoozed until {session.PausedUntil.Value:HH:mm}", $"⏰ Gesnoozed tot {session.PausedUntil.Value:HH:mm}");
        }
        else if (session.Paused)
        {
            statusLine = L("⏸️ Paused", "⏸️ Gepauzeerd");
        }
        else
        {
            statusLine = L("▶️ Running", "▶️ Actief");
        }

        string text =
            $"🏠 <b>Marktplaats Notifier</b> ({statusLine})\n" +
            $"🔎 {session.SearchList.Rules.Count} {L("searches", "zoekopdrachten")} · 👀 {session.Watchlist.Count} {L("watched", "gevolgd")} · 📨 {session.ListingsSent} {L("sent", "verstuurd")}\n\n" +
            L("Pick an option, paste a Marktplaats link, or type e.g. <code>rtx 3070 under 140</code>.",
              "Kies een optie, plak een Marktplaats link, of typ bijv. <code>rtx 3070 onder 140</code>.");

        List<object[]> rows = new List<object[]>
        {
            new[]
            {
                Telegram.CallbackButton(L("➕ Add Search", "➕ Zoekopdracht"), "menu:add"),
                Telegram.CallbackButton(L($"📋 Searches ({session.SearchList.Rules.Count})", $"📋 Lijst ({session.SearchList.Rules.Count})"), "list:0")
            },
            new[]
            {
                Telegram.CallbackButton(L($"👀 Watchlist ({session.Watchlist.Count})", $"👀 Volglijst ({session.Watchlist.Count})"), "menu:watch"),
                Telegram.CallbackButton(L("⏳ Stale Deals", "⏳ Lang te koop"), "menu:stale")
            },
            new[]
            {
                Telegram.CallbackButton(L("📊 Today", "📊 Vandaag"), "menu:today"),
                Telegram.CallbackButton(L("📈 Stats", "📈 Statistieken"), "menu:stats")
            },
            new[]
            {
                Telegram.CallbackButton(L("💰 Flips & Profit", "💰 Flips & Winst"), "flip:home"),
                Telegram.CallbackButton(L("🎁 Starter Packs", "🎁 Startpakketten"), "menu:packs")
            },
            new[]
            {
                Telegram.CallbackButton(L("⚙️ Settings", "⚙️ Instellingen"), "menu:settings"),
                Telegram.CallbackButton("🤖 AI Grader", "menu:ai")
            },
            new[]
            {
                Telegram.CallbackButton(L("🚫 Blocklist", "🚫 Blokkades"), "menu:block"),
                Telegram.CallbackButton(session.Paused ? L("▶️ Resume / Snooze", "▶️ Hervat / Snooze") : L("⏸️ Pause / Snooze", "⏸️ Pauze / Snooze"), "menu:pause")
            },
            new[]
            {
                Telegram.CallbackButton("📦 Backup", "menu:backup"),
                Telegram.CallbackButton(L("🧪 Test Alert", "🧪 Testmelding"), "menu:test")
            }
        };

        if (session.IsOwner)
        {
            rows.Add(new[]
            {
                Telegram.CallbackButton($"👥 Users ({userManager.AllUsers.Count})", "menu:users"),
                Telegram.CallbackButton("📜 Logs", "menu:logs")
            });
        }

        string markup = Telegram.InlineKeyboard(rows);
        if (editMessageId.HasValue)
        {
            await session.Telegram.EditMessage(editMessageId.Value, text, markup, parseMode: "HTML");
        }
        else
        {
            await session.Telegram.SendText(text, markup, parseMode: "HTML");
        }
    }

    private async Task SendStarterPacksMenu(long? editMessageId = null)
    {
        string text = L(
            "🎁 <b>Starter Packs</b>\nPick a pack to get started (you can change prices later in /list):",
            "🎁 <b>Startpakketten</b>\nKies een pakket om te starten (prijzen kun je later aanpassen in /list):");

        string markup = Telegram.InlineKeyboard(new[]
        {
            new[]
            {
                Telegram.CallbackButton("🖥️ Broken Monitors (5)", "pack:monitors"),
                Telegram.CallbackButton("💻 PC & GPU Parts (5)", "pack:pc")
            },
            new[]
            {
                Telegram.CallbackButton("🎮 Consoles (4)", "pack:consoles"),
                Telegram.CallbackButton("📱 Phones (3)", "pack:phones")
            },
            new[]
            {
                Telegram.CallbackButton(L("⏭️ Start empty", "⏭️ Leeg beginnen"), "pack:empty"),
                Telegram.CallbackButton("🏠 Menu", "menu:home")
            }
        });

        if (editMessageId.HasValue)
        {
            await session.Telegram.EditMessage(editMessageId.Value, text, markup, parseMode: "HTML");
        }
        else
        {
            await session.Telegram.SendText(text, markup, parseMode: "HTML");
        }
    }

    private async Task SendBackupZip()
    {
        byte[] zipBytes = session.CreateBackupZip();
        string fileName = $"marktplaats-backup-{DateTime.Now:yyyy-MM-dd}.zip";
        await session.Telegram.SendDocument(
            zipBytes,
            fileName,
            L("📦 Backup (no secrets). Send this .zip back here anytime to restore.",
              "📦 Back-up (zonder sleutels). Stuur dit .zip bestand terug om te herstellen."));
    }

    private async Task SendWatchlistMenu(long? editMessageId = null)
    {
        List<WatchItem> items = session.Watchlist.Items.OrderByDescending(i => i.AddedAt).ToList();
        if (items.Count == 0)
        {
            string empty =
                "👀 Your watchlist is empty.\n\n" +
                "Tap <b>👀 Watch</b> under any listing, swipe-reply <code>watch 80</code>, or Share a listing straight from the Marktplaats app!";
            if (editMessageId.HasValue)
            {
                await session.Telegram.EditMessage(editMessageId.Value, empty, HomeButtonMarkup(), parseMode: "HTML");
            }
            else
            {
                await session.Telegram.SendText(empty, HomeButtonMarkup(), parseMode: "HTML");
            }

            return;
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"👀 <b>Watched Listings ({items.Count})</b>\n");
        List<object[]> rows = new List<object[]>();

        foreach (WatchItem item in items.Take(12))
        {
            string priceStr = item.LastPrice > 0 ? Listing.FormatEuro(item.LastPrice) : "Bieden";
            string bidStr = item.HighestBid > 0 ? $" · bid {Listing.FormatEuro(item.HighestBid)}" : "";
            string targetStr = item.TargetPrice > 0 ? $" · 🎯 ≤{Listing.FormatEuro(item.TargetPrice)}" : "";
            string statusStr = item.Status == "RESERVED" ? " 🔒" : "";
            sb.AppendLine($"• <a href=\"{Encode(item.Link)}\">{Encode(item.Title)}</a> — {priceStr}{bidStr}{targetStr}{statusStr}");

            string shortTitle = item.Title.Length > 22 ? item.Title.Substring(0, 19) + "..." : item.Title;
            rows.Add(new[]
            {
                Telegram.CallbackButton($"🎯 {shortTitle}", $"wtgt:{item.Id}"),
                Telegram.CallbackButton("❌ Stop", $"wdel:{item.Id}")
            });
        }

        rows.Add(new[] { Telegram.CallbackButton("🏠 Menu", "menu:home") });
        string markup = Telegram.InlineKeyboard(rows);
        if (editMessageId.HasValue)
        {
            await session.Telegram.EditMessage(editMessageId.Value, sb.ToString().TrimEnd(), markup, parseMode: "HTML");
        }
        else
        {
            await session.Telegram.SendText(sb.ToString().TrimEnd(), markup, parseMode: "HTML");
        }
    }

    private async Task SendStaleListings(int minDays)
    {
        var stale = session.History.FindStaleListings(session.SearchList, session.Blocklist, minDays);
        if (stale.Count == 0)
        {
            await session.Telegram.SendText(
                $"⏳ No stale listings found sitting unsold for {minDays}+ days just above your max prices yet.\n" +
                "(This fills up as the bot tracks listings over 7+ days in history.txt.)",
                HomeButtonMarkup());
            return;
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"⏳ <b>Unsold for {minDays}+ days</b> (above your max, good for a lower offer)\n");
        List<object[]> rows = new List<object[]>();

        foreach (var (entry, maxPrice, daysOld) in stale)
        {
            string url = $"https://link.marktplaats.nl/{entry.Id}";
            sb.AppendLine(
                $"• <a href=\"{Encode(url)}\">{Encode(entry.Title)}</a>\n" +
                $"   💶 {Listing.FormatEuro(entry.Price)} (your max €{maxPrice}) · 🕒 {daysOld}d unsold · {Encode(entry.Search)}");

            string shortTitle = entry.Title.Length > 28 ? entry.Title.Substring(0, 25) + "..." : entry.Title;
            rows.Add(new[] { Telegram.CallbackButton($"👀 Watch: {shortTitle}", $"ws:{entry.Id}") });
        }

        rows.Add(new[] { Telegram.CallbackButton("🏠 Menu", "menu:home") });
        await session.Telegram.SendText(sb.ToString().TrimEnd(), Telegram.InlineKeyboard(rows), parseMode: "HTML");
    }

    private async Task SendBlocklistMenu(long? editMessageId = null)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("🚫 <b>Blocklist & Global Skip Words</b>\n");
        sb.AppendLine(session.Blocklist.GlobalSkipWords.Count > 0
            ? $"Global skip words: <code>{Encode(string.Join(", ", session.Blocklist.GlobalSkipWords))}</code>"
            : "Global skip words: <i>none</i>");
        sb.AppendLine(session.Blocklist.BlockedSellers.Count > 0
            ? $"Blocked sellers: <code>{Encode(string.Join(", ", session.Blocklist.BlockedSellers))}</code>"
            : "Blocked sellers: <i>none</i>");
        sb.Append("\n💡 You can also swipe-reply <code>block</code> on any listing to block its seller.");

        List<object[]> rows = new List<object[]>
        {
            new[]
            {
                Telegram.CallbackButton("➕ Block seller", "blk:addseller"),
                Telegram.CallbackButton("➕ Global skip word", "blk:addword")
            },
            new[] { Telegram.CallbackButton("🧹 Add junk preset (waterschade, lege doos...)", "blk:preset") }
        };

        for (int i = 0; i < Math.Min(session.Blocklist.BlockedSellers.Count, 5); i++)
        {
            rows.Add(new[] { Telegram.CallbackButton($"❌ Unblock seller: {session.Blocklist.BlockedSellers[i]}", $"blk:rmseller:{i}") });
        }

        for (int i = 0; i < Math.Min(session.Blocklist.GlobalSkipWords.Count, 5); i++)
        {
            rows.Add(new[] { Telegram.CallbackButton($"❌ Remove word: {session.Blocklist.GlobalSkipWords[i]}", $"blk:rmword:{i}") });
        }

        rows.Add(new[] { Telegram.CallbackButton("🏠 Menu", "menu:home") });
        string markup = Telegram.InlineKeyboard(rows);
        if (editMessageId.HasValue)
        {
            await session.Telegram.EditMessage(editMessageId.Value, sb.ToString(), markup, parseMode: "HTML");
        }
        else
        {
            await session.Telegram.SendText(sb.ToString(), markup, parseMode: "HTML");
        }
    }

    private async Task SendAiMenu(long? editMessageId = null)
    {
        BotSettings s = session.Settings;
        bool isOllama = s.AiProvider.Equals("ollama", StringComparison.OrdinalIgnoreCase);
        bool hasKey = isOllama || session.AiGrader.GetApiKey(s.AiProvider) != "";
        string keyStatus = isOllama ? "Not needed (local)" : (hasKey ? "✅ Configured" : "❌ Not set");

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("🤖 <b>AI Photo & Deal Grader</b>\n");
        sb.AppendLine($"Status: <b>{(s.AiEnabled ? "🟢 ON" : "⚪ OFF")}</b>");
        sb.AppendLine($"Provider: <b>{Encode(s.AiProvider)}</b>");
        sb.AppendLine($"Model: <code>{Encode(s.AiModel)}</code>");
        sb.AppendLine($"API Key: <b>{keyStatus}</b>");
        sb.AppendLine($"Used today: <b>{session.AiGrader.CallsToday} / {s.AiDailyLimit}</b>");
        sb.AppendLine($"Auto-skip AI SKIP: <b>{(s.AiAutoSkip ? "ON" : "OFF (tag only)")}</b>");
        if (isOllama)
        {
            sb.AppendLine($"Ollama URL: <code>{Encode(s.AiBaseUrl)}</code>");
        }
        sb.Append("\nTip: <b>Gemini</b> has a free tier with decent limits (aistudio.google.com/apikey). OpenRouter works with most other models too.");

        List<object[]> rows = new List<object[]>
        {
            new[]
            {
                Telegram.CallbackButton(s.AiEnabled ? "🟢 AI Grader: ON" : "⚪ AI Grader: OFF", "ai:toggle"),
                Telegram.CallbackButton(s.AiAutoSkip ? "🚫 Auto-skip: ON" : "🏷️ Auto-skip: OFF", "ai:autoskip")
            },
            new[]
            {
                Telegram.CallbackButton("✨ Gemini (Rec.)", "ai:prov:gemini"),
                Telegram.CallbackButton("⚡ Groq", "ai:prov:groq")
            },
            new[]
            {
                Telegram.CallbackButton("🌐 OpenRouter", "ai:prov:openrouter"),
                Telegram.CallbackButton("🧠 Claude", "ai:prov:claude")
            },
            new[]
            {
                Telegram.CallbackButton("🤖 OpenAI", "ai:prov:openai"),
                Telegram.CallbackButton("🏠 Ollama", "ai:prov:ollama")
            },
            new[]
            {
                Telegram.CallbackButton(isOllama ? "🏠 Ollama URL" : "🔑 Set API key", isOllama ? "ai:url" : "ai:key"),
                Telegram.CallbackButton("🎛️ Change model", "ai:model"),
                Telegram.CallbackButton("📊 Daily limit", "ai:limit")
            },
            new[] { Telegram.CallbackButton("🏠 Menu", "menu:home") }
        };

        string markup = Telegram.InlineKeyboard(rows);
        if (editMessageId.HasValue)
        {
            await session.Telegram.EditMessage(editMessageId.Value, sb.ToString(), markup, parseMode: "HTML");
        }
        else
        {
            await session.Telegram.SendText(sb.ToString(), markup, parseMode: "HTML");
        }
    }

    private async Task SendSettingsMenu(long? editMessageId = null)
    {
        BotSettings s = session.Settings;
        string langLabel = s.IsDutch ? "🇳🇱 Nederlands" : "🇬🇧 English";
        string nightText = s.QuietNightsEnabled ? $"{s.NightStartHour:00}:00–{s.NightEndHour:00}:00" : "Off";
        string bizText = s.SkipLikelyBusinesses ? "ON (auto-skip)" : "OFF (show warning)";
        string descPriceText = s.SkipHighDescriptionPrice ? "ON (skip high € in text)" : "OFF (show note only)";
        string digestText = s.MorningDigestEnabled ? $"ON ({s.MorningDigestHour:00}:00)" : "OFF";

        string text =
            $"⚙️ <b>{L("Settings", "Instellingen")}</b>\n\n" +
            $"🌐 {L("Language", "Taal")}: <b>{langLabel}</b>\n" +
            $"📍 Postcode: <b>{Encode(session.Postcode)}</b>\n" +
            $"🚗 {L("Pickup distance", "Ophaalafstand")}: <b>{session.PickupKm} km</b>\n" +
            $"🌙 {L("Quiet nights", "Stille nachten")}: <b>{nightText}</b>\n" +
            $"🏢 {L("Skip likely businesses", "Bedrijven overslaan")}: <b>{bizText}</b>\n" +
            $"💬 {L("Skip high price in description", "Hoge prijs in tekst overslaan")}: <b>{descPriceText}</b>\n" +
            $"⚡ {L("Fast 35s category feed", "Snelle 35s categorie-check")}: <b>{(s.FastCategoryMode ? "ON" : "OFF")}</b>\n" +
            $"🔧 Tweakers V&amp;A: <b>{(s.EnableTweakers ? "ON" : "OFF")}</b> · 🇧🇪 2dehands: <b>{(s.EnableTweedehands ? "ON" : "OFF")}</b>\n" +
            $"🔵 Coolblue: <b>{(s.EnableCoolblue ? "ON" : "OFF")}</b> · 🛍️ Vinted: <b>{(s.EnableVinted ? "ON" : "OFF")}</b>\n" +
            $"☀️ {L("Morning digest", "Ochtendoverzicht")}: <b>{digestText}</b>";

        string markup = Telegram.InlineKeyboard(new[]
        {
            new[] { Telegram.CallbackButton($"🌐 {langLabel}", "set:lang") },
            new[]
            {
                Telegram.CallbackButton("📍 Postcode", "set:postcode"),
                Telegram.CallbackButton(L("🚗 Pickup km", "🚗 Afstand km"), "set:km")
            },
            new[]
            {
                Telegram.CallbackButton(s.QuietNightsEnabled ? "🌙 Quiet nights: ON" : "🌙 Quiet nights: OFF", "set:night_toggle"),
                Telegram.CallbackButton("🕐 Night hours", "set:night_hours")
            },
            new[] { Telegram.CallbackButton(s.SkipLikelyBusinesses ? "🏢 Skip businesses: ON" : "🏢 Skip businesses: OFF", "set:biz") },
            new[] { Telegram.CallbackButton(s.SkipHighDescriptionPrice ? "💬 Skip high desc €: ON" : "💬 Skip high desc €: OFF", "set:descprice") },
            new[]
            {
                Telegram.CallbackButton(s.FastCategoryMode ? "⚡ Fast 35s: ON" : "⚡ Fast 35s: OFF", "set:fastcat"),
                Telegram.CallbackButton(s.EnableTweakers ? "🔧 Tweakers: ON" : "🔧 Tweakers: OFF", "set:tweakers")
            },
            new[]
            {
                Telegram.CallbackButton(s.EnableTweedehands ? "🇧🇪 2dehands: ON" : "🇧🇪 2dehands: OFF", "set:2dehands"),
                Telegram.CallbackButton(s.EnableCoolblue ? "🔵 Coolblue: ON" : "🔵 Coolblue: OFF", "set:coolblue")
            },
            new[]
            {
                Telegram.CallbackButton(s.EnableVinted ? "🛍️ Vinted: ON" : "🛍️ Vinted: OFF", "set:vinted"),
                Telegram.CallbackButton(s.MorningDigestEnabled ? "☀️ Digest: ON" : "☀️ Digest: OFF", "set:digest")
            },
            new[] { Telegram.CallbackButton("🏠 Menu", "menu:home") }
        });

        if (editMessageId.HasValue)
        {
            await session.Telegram.EditMessage(editMessageId.Value, text, markup, parseMode: "HTML");
        }
        else
        {
            await session.Telegram.SendText(text, markup, parseMode: "HTML");
        }
    }

    private async Task SendUsersMenu(long? editMessageId = null)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"👥 <b>Connected Users ({userManager.AllUsers.Count})</b>\n");
        List<object[]> rows = new List<object[]>();

        foreach (UserSession u in userManager.AllUsers)
        {
            string role = u.IsOwner ? "👑 Owner" : "👤 Member";
            sb.AppendLine($"• {role}: <b>{Encode(u.Name)}</b> — {u.SearchList.Rules.Count} searches, {Encode(u.Postcode)} ({u.PickupKm} km)");
            if (!u.IsOwner)
            {
                rows.Add(new[] { Telegram.CallbackButton($"🗑️ Remove {u.Name}", $"usr:del:{u.ChatId}") });
            }
        }

        sb.Append("\nUse /invite to generate a one-time invite code for a friend.");
        rows.Add(new[] { Telegram.CallbackButton("🏠 Menu", "menu:home") });
        string markup = Telegram.InlineKeyboard(rows);

        if (editMessageId.HasValue)
        {
            await session.Telegram.EditMessage(editMessageId.Value, sb.ToString(), markup, parseMode: "HTML");
        }
        else
        {
            await session.Telegram.SendText(sb.ToString(), markup, parseMode: "HTML");
        }
    }

    private async Task SendTestListing()
    {
        Listing sample = new Listing
        {
            Id = "m9999999999",
            Title = "Samsung Odyssey G5 27 inch monitor — geen beeld wel geluid",
            Price = 55,
            OldPrice = 110,
            PriceText = "€55",
            PriceType = "FIXED",
            Condition = "Gebruikt",
            City = "Utrecht",
            DistanceKm = 4,
            Delivery = "Ophalen of Verzenden",
            CanShip = true,
            PostedAt = DateTime.Now.AddMinutes(-3),
            Views = 12,
            Saves = 2,
            SellerName = "Jan",
            SellerVerified = true,
            SellerRating = 4.9,
            SellerReviewCount = 34,
            SellerYears = 8,
            SellerType = "CONSUMER",
            PhotoCount = 4,
            Description = "Monitor gaat aan en lampje brandt, geen barst in het scherm maar blijft zwart.",
            Hint = "💡 Sound but no picture: usually the backlight or the power board",
            HintQuestion = "Als je met een zaklamp op het scherm schijnt, zie je dan iets?",
            MatchedSearch = "defect monitor",
            CategoryId = 322,
            CategoryName = "Monitoren",
            TypicalPrice = 105,
            TypicalCount = 19,
            AiVerdict = "BUY",
            AiReason = "No cracks visible; likely backlight or power board",
            AiResale = "€130-160",
            AiMaxBid = "€60",
            Link = "https://www.marktplaats.nl"
        };

        long msgId = await session.Telegram.SendListing(sample, night: false, alert: "TEST ALERT", isWatched: false, dutch: session.Settings.IsDutch);
        session.RememberSentMessage(sample, msgId);
    }

    private string BuildHelpText()
    {
        string ownerExtra = session.IsOwner
            ? "\n<b>Admin</b>\n/invite · /users · /logs\n"
            : "";

        return
            "🤖 <b>Commands</b>\n" +
            "Send <b>/menu</b> for buttons so you don't have to type commands.\n\n" +
            "<b>Shortcuts</b>\n" +
            "• Type <code>rtx 3070 under 140</code> to add a search\n" +
            "• Paste a Marktplaats search URL to add it\n" +
            "• Share a Marktplaats listing from the app to watch it\n" +
            "• Reply <code>watch 80</code>, <code>bought 50</code>, or <code>block</code> on any listing\n" +
            "• Send a <code>.zip</code> backup or <code>searches.txt</code> to restore\n\n" +
            "<b>All commands</b>\n" +
            "/menu · /add · /list · /find · /price · /watchlist · /stale · /stats\n" +
            "/settings · /ai · /block · /profit · /today · /backup\n" +
            "/pause · /snooze 2h · /resume · /status · /test\n" +
            ownerExtra;
    }

    private static string HomeButtonMarkup()
    {
        return Telegram.InlineKeyboard(new[]
        {
            new[] { Telegram.CallbackButton("🏠 Menu", "menu:home") }
        });
    }

    private static string UndoMarkup(string undoCallback, string backCallback, string backLabel)
    {
        return Telegram.InlineKeyboard(new[]
        {
            new[]
            {
                Telegram.CallbackButton("↩️ Undo", undoCallback),
                Telegram.CallbackButton(backLabel, backCallback)
            }
        });
    }

    private static bool TryParseEuro(string raw, out double value)
    {
        string clean = raw.Replace("€", "").Trim().Replace(",", ".");
        return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string NormalizeCommand(string text)
    {
        if (!text.StartsWith('/'))
        {
            return text;
        }

        int spaceIndex = text.IndexOf(' ');
        string commandPart = spaceIndex >= 0 ? text.Substring(0, spaceIndex) : text;
        int atIndex = commandPart.IndexOf('@');
        if (atIndex >= 0)
        {
            commandPart = commandPart.Substring(0, atIndex);
            return spaceIndex >= 0 ? commandPart + text.Substring(spaceIndex) : commandPart;
        }

        return text;
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

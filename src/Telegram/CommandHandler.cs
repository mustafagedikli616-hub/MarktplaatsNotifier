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

    public async Task StartOnboarding()
    {
        step = "onboard_postcode";
        await session.Telegram.SendText(
            "👋 Welcome to Marktplaats Notifier!\n\nLet's set up two quick things so pickup distances work.\n📍 What's your Dutch postcode? (like 1234AB)");
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

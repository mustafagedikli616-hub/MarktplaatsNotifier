using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MarktplaatsNotifier;

public partial class CommandHandler
{
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
}

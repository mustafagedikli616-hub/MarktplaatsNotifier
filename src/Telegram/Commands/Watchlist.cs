using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MarktplaatsNotifier;

public partial class CommandHandler
{
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
}

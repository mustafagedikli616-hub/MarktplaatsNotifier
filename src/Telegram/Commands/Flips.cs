using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MarktplaatsNotifier;

public partial class CommandHandler
{
    private async Task HandleBoughtCommand(string text)
    {
        string rest = text.Length > 7 ? text.Substring(7).Trim() : "";
        if (rest == "")
        {
            step = "flip_name";
            await session.Telegram.SendText(L("🛒 What item did you buy? (or /cancel)", "🛒 Welk item heb je gekocht? (of /cancel)"));
            return;
        }

        string[] parts = rest.Split(';', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts[0] == "" || !TryParseEuro(parts[1], out double price) || price < 0)
        {
            await session.Telegram.SendText(
                "Use <code>/bought item name;price</code>, or tap <b>➕ Log purchase</b> in /profit!",
                parseMode: "HTML");
            return;
        }

        FlipItem item = session.FlipTracker.AddPurchase(parts[0], price);
        await session.Telegram.SendText(
            $"🛒 Logged #{item.Id}: <b>{Encode(item.Name)}</b> bought for {Listing.FormatEuro(item.BuyPrice)}.",
            Telegram.InlineKeyboard(new[] { new[] { Telegram.CallbackButton("💰 Open Flips", "flip:home") } }),
            parseMode: "HTML");
    }
    private async Task HandlePartsCommand(string text)
    {
        string rest = text.Length > 6 ? text.Substring(6).Trim() : "";
        string[] parts = rest.Split(';', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts[0] == "" || !TryParseEuro(parts[1], out double cost) || cost < 0)
        {
            await session.Telegram.SendText("Use <code>/parts #1;12.50</code>, or tap the item inside /profit.", parseMode: "HTML");
            return;
        }

        FlipItem? item = session.FlipTracker.AddParts(parts[0], cost);
        if (item == null)
        {
            await session.Telegram.SendText($"❌ Couldn't find flip \"{parts[0]}\". Send /profit to see your open items.");
            return;
        }

        await session.Telegram.SendText(
            $"🔧 Added {Listing.FormatEuro(cost)} parts to #{item.Id} <b>{Encode(item.Name)}</b> (total cost: {Listing.FormatEuro(item.TotalCost)}).",
            parseMode: "HTML");
    }
    private async Task HandleSoldCommand(string text)
    {
        string rest = text.Length > 5 ? text.Substring(5).Trim() : "";
        string[] parts = rest.Split(';', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts[0] == "" || !TryParseEuro(parts[1], out double sellPrice) || sellPrice < 0)
        {
            await session.Telegram.SendText("Use <code>/sold #1;135</code>, or tap the item inside /profit.", parseMode: "HTML");
            return;
        }

        FlipItem? item = session.FlipTracker.MarkSold(parts[0], sellPrice);
        if (item == null)
        {
            await session.Telegram.SendText($"❌ Couldn't find flip \"{parts[0]}\". Send /profit to see your open items.");
            return;
        }

        await SendSaleCelebration(item);
    }
    private async Task SendSaleCelebration(FlipItem item)
    {
        double profit = item.Profit ?? 0;
        string roi = item.TotalCost > 0 ? $" ({Math.Round(profit / item.TotalCost * 100)}% ROI)" : "";
        await session.Telegram.SendText(
            $"🎉 Sold #{item.Id} <b>{Encode(item.Name)}</b>!\n" +
            $"Cost {Listing.FormatEuro(item.TotalCost)} → Sold {Listing.FormatEuro(item.SellPrice ?? 0)} = <b>{FlipTracker.FormatSignedEuro(profit)}{roi}</b>",
            Telegram.InlineKeyboard(new[] { new[] { Telegram.CallbackButton("💰 Flips & Profit", "flip:home") } }),
            parseMode: "HTML");
    }
    private async Task HandleFlipCallback(string callbackId, long messageId, string[] parts)
    {
        string sub = parts[1];
        if (sub == "home")
        {
            step = "";
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await SendFlipsMenu(messageId);
        }
        else if (sub == "new")
        {
            step = "flip_name";
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await session.Telegram.SendText(L(
                "🛒 What item did you buy? (e.g. Samsung Odyssey G5) (or /cancel)",
                "🛒 Welk item heb je gekocht? (bijv. Samsung Odyssey G5) (of /cancel)"));
        }
        else if (sub == "item" && parts.Length >= 3 && int.TryParse(parts[2], out int flipId))
        {
            FlipItem? item = session.FlipTracker.GetById(flipId);
            if (item == null)
            {
                await session.Telegram.AnswerCallbackQuery(callbackId, "Flip not found");
                await SendFlipsMenu(messageId);
                return;
            }

            await session.Telegram.AnswerCallbackQuery(callbackId);
            await SendFlipDetail(item, messageId);
        }
        else if (sub == "parts" && parts.Length >= 3 && int.TryParse(parts[2], out int partsId))
        {
            editFlipId = partsId;
            step = "flip_parts";
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await session.Telegram.SendText(L(
                "🔧 How much did the repair parts cost? (e.g. 12.50) (or /cancel)",
                "🔧 Hoeveel kostten de onderdelen? (bijv. 12.50) (of /cancel)"));
        }
        else if (sub == "sold" && parts.Length >= 3 && int.TryParse(parts[2], out int soldId))
        {
            editFlipId = soldId;
            step = "flip_sold";
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await session.Telegram.SendText(L(
                "💵 How much did you sell it for? (e.g. 135) (or /cancel)",
                "💵 Voor hoeveel heb je het verkocht? (bijv. 135) (of /cancel)"));
        }
        else if (sub == "del" && parts.Length >= 3 && int.TryParse(parts[2], out int delId))
        {
            FlipItem? deleted = session.FlipTracker.Delete(delId);
            if (deleted == null)
            {
                await session.Telegram.AnswerCallbackQuery(callbackId, "Already deleted");
                await SendFlipsMenu(messageId);
                return;
            }

            lastDeletedFlip = deleted;
            await session.Telegram.AnswerCallbackQuery(callbackId, "Deleted");
            await session.Telegram.EditMessage(
                messageId,
                $"🗑️ Deleted flip #{deleted.Id} <b>{Encode(deleted.Name)}</b>.",
                UndoMarkup("undo:flip", "flip:home", "💰 Flips & Profit"),
                parseMode: "HTML");
        }
        else
        {
            await session.Telegram.AnswerCallbackQuery(callbackId);
        }
    }
    private async Task SendFlipsMenu(long? editMessageId = null)
    {
        string text = session.FlipTracker.BuildProfitSummary(session.Settings.IsDutch);
        List<object[]> rows = new List<object[]>
        {
            new[] { Telegram.CallbackButton(L("➕ Log Purchase", "➕ Aankoop toevoegen"), "flip:new") }
        };

        foreach (FlipItem item in session.FlipTracker.OpenItems.TakeLast(8))
        {
            string shortName = item.Name.Length > 28 ? item.Name.Substring(0, 25) + "..." : item.Name;
            rows.Add(new[] { Telegram.CallbackButton($"🔧 #{item.Id} {shortName} ({Listing.FormatEuro(item.TotalCost)})", $"flip:item:{item.Id}") });
        }

        rows.Add(new[] { Telegram.CallbackButton("🏠 Menu", "menu:home") });
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
    private async Task SendFlipDetail(FlipItem item, long? editMessageId = null)
    {
        string text =
            $"🔧 <b>#{item.Id} {Encode(item.Name)}</b>\n\n" +
            $"🛒 {L("Bought", "Gekocht")}: <b>{Listing.FormatEuro(item.BuyPrice)}</b> ({item.BoughtAt:d MMM})\n" +
            $"🔩 {L("Parts", "Onderdelen")}: <b>{Listing.FormatEuro(item.PartsCost)}</b>\n" +
            $"💶 {L("Total cost", "Totale kosten")}: <b>{Listing.FormatEuro(item.TotalCost)}</b>";

        string markup = Telegram.InlineKeyboard(new[]
        {
            new[]
            {
                Telegram.CallbackButton(L("🔧 Add parts", "🔧 Onderdelen +"), $"flip:parts:{item.Id}"),
                Telegram.CallbackButton(L("💵 Mark sold", "💵 Verkocht"), $"flip:sold:{item.Id}")
            },
            new[]
            {
                Telegram.CallbackButton(L("🗑️ Delete", "🗑️ Verwijder"), $"flip:del:{item.Id}"),
                Telegram.CallbackButton(L("⬅️ Back", "⬅️ Terug"), "flip:home")
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
}

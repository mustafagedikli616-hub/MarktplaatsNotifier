using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MarktplaatsNotifier;

public partial class CommandHandler
{
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
}

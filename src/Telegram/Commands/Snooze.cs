using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MarktplaatsNotifier;

public partial class CommandHandler
{
    private async Task HandleSnoozeCommand(string text)
    {
        string arg = text.Length > 7 ? text.Substring(7).Trim() : "";
        if (arg == "")
        {
            await SendPauseMenu();
            return;
        }

        if (arg.Equals("tomorrow", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("morgen", StringComparison.OrdinalIgnoreCase))
        {
            DateTime tomorrow8 = DateTime.Today.AddDays(1).AddHours(8);
            ApplySnoozeUntil(tomorrow8);
            await session.Telegram.SendText(L(
                $"🌅 Snoozed until tomorrow {tomorrow8:HH:mm}.",
                $"🌅 Gesnoozed tot morgen {tomorrow8:HH:mm}."));
            return;
        }

        Match m = SnoozeDurationPattern.Match(arg);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out int amount) || amount <= 0)
        {
            await session.Telegram.SendText(L(
                "❌ Use e.g. <code>/snooze 30m</code>, <code>/snooze 2h</code>, or <code>/snooze tomorrow</code>.",
                "❌ Gebruik bijv. <code>/snooze 30m</code>, <code>/snooze 2h</code>, of <code>/snooze morgen</code>."),
                parseMode: "HTML");
            return;
        }

        string unit = m.Groups[2].Value.ToLowerInvariant();
        TimeSpan duration = unit switch
        {
            "m" or "min" or "mins" => TimeSpan.FromMinutes(Math.Min(amount, 1440)),
            "d" or "day" or "days" => TimeSpan.FromDays(Math.Min(amount, 7)),
            _ => TimeSpan.FromHours(Math.Min(amount, 72))
        };

        DateTime until = DateTime.Now + duration;
        ApplySnoozeUntil(until);
        await session.Telegram.SendText(L(
            $"⏰ Snoozed until <b>{until:HH:mm}</b> ({until:d MMM}). I'll resume automatically!",
            $"⏰ Gesnoozed tot <b>{until:HH:mm}</b> ({until:d MMM}). Ik ga daarna vanzelf weer verder!"),
            parseMode: "HTML");
    }
    private void ApplySnoozeUntil(DateTime until)
    {
        session.Paused = true;
        session.PausedUntil = until;
    }
    private async Task HandleSnoozeCallback(string callbackId, long messageId, string mode)
    {
        if (mode == "1h")
        {
            ApplySnoozeUntil(DateTime.Now.AddHours(1));
            await session.Telegram.AnswerCallbackQuery(callbackId, "Snoozed 1 hour");
        }
        else if (mode == "3h")
        {
            ApplySnoozeUntil(DateTime.Now.AddHours(3));
            await session.Telegram.AnswerCallbackQuery(callbackId, "Snoozed 3 hours");
        }
        else if (mode == "tomorrow")
        {
            ApplySnoozeUntil(DateTime.Today.AddDays(1).AddHours(8));
            await session.Telegram.AnswerCallbackQuery(callbackId, "Snoozed until 08:00");
        }
        else if (mode == "forever")
        {
            session.Paused = true;
            session.PausedUntil = null;
            await session.Telegram.AnswerCallbackQuery(callbackId, "Paused");
        }
        else if (mode == "resume")
        {
            session.Paused = false;
            session.PausedUntil = null;
            await session.Telegram.AnswerCallbackQuery(callbackId, "Resumed ▶️");
        }

        await SendPauseMenu(messageId);
    }
    private async Task SendPauseMenu(long? editMessageId = null)
    {
        string state = session.Paused
            ? (session.PausedUntil.HasValue
                ? L($"⏰ Snoozed until <b>{session.PausedUntil.Value:HH:mm}</b> ({session.PausedUntil.Value:d MMM})",
                    $"⏰ Gesnoozed tot <b>{session.PausedUntil.Value:HH:mm}</b> ({session.PausedUntil.Value:d MMM})")
                : L("⏸️ Paused indefinitely", "⏸️ Gepauzeerd tot /resume"))
            : L("▶️ Currently running", "▶️ Momenteel actief");

        string text = $"⏸️ <b>{L("Pause & Snooze", "Pauze & Snooze")}</b>\n{state}";
        string markup = Telegram.InlineKeyboard(new[]
        {
            new[]
            {
                Telegram.CallbackButton(L("⏰ Snooze 1h", "⏰ Snooze 1 uur"), "snz:1h"),
                Telegram.CallbackButton(L("⏰ Snooze 3h", "⏰ Snooze 3 uur"), "snz:3h")
            },
            new[]
            {
                Telegram.CallbackButton(L("🌅 Until tomorrow 08:00", "🌅 Tot morgen 08:00"), "snz:tomorrow")
            },
            new[]
            {
                Telegram.CallbackButton(L("⏸️ Pause until /resume", "⏸️ Pauzeer"), "snz:forever"),
                Telegram.CallbackButton(L("▶️ Resume now", "▶️ Hervat nu"), "snz:resume")
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
}

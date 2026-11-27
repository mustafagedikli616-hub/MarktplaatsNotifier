using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MarktplaatsNotifier;

public partial class CommandHandler
{
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
}

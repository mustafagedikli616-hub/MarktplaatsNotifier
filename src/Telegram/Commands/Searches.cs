using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MarktplaatsNotifier;

public partial class CommandHandler
{
    private async Task StartInteractiveAdd()
    {
        step = "add_search";
        pendingRule = new SearchRule();
        string markup = Telegram.InlineKeyboard(new[]
        {
            new[]
            {
                Telegram.CallbackButton(L("🎁 Starter Packs", "🎁 Startpakketten"), "menu:packs"),
                Telegram.CallbackButton(L("🏠 Menu", "🏠 Menu"), "menu:home")
            }
        });
        await session.Telegram.SendText(
            L("🔎 What do you want to search for?\nType words (e.g. <code>rtx 3070 under 140</code>), paste a Marktplaats URL, or tap Starter Packs below. (or /cancel)",
              "🔎 Waar wil je naar zoeken?\nTyp zoekwoorden (bijv. <code>rtx 3070 onder 140</code>), plak een Marktplaats link, of kies een Startpakket. (of /cancel)"),
            markup,
            parseMode: "HTML");
    }
    private static bool TryParseNaturalSearch(string text, out string query, out int maxPrice)
    {
        query = "";
        maxPrice = 0;

        if (text.Contains(';') || text.StartsWith('/') || text.Length > 80)
        {
            return false;
        }

        Match m1 = NaturalSearchPattern1.Match(text);
        if (m1.Success && int.TryParse(m1.Groups[2].Value, out int p1))
        {
            query = m1.Groups[1].Value.Trim();
            maxPrice = p1;
            return query.Length >= 2;
        }

        Match m2 = NaturalSearchPattern2.Match(text);
        if (m2.Success)
        {
            string numStr = m2.Groups[2].Success ? m2.Groups[2].Value : m2.Groups[3].Value;
            if (int.TryParse(numStr, out int p2))
            {
                query = m2.Groups[1].Value.Trim();
                maxPrice = p2;
                return query.Length >= 2;
            }
        }

        return false;
    }
    private async Task OfferNaturalSearchAdd(string query, int maxPrice)
    {
        if (session.SearchList.Rules.ContainsKey(query))
        {
            await session.Telegram.SendText($"⚠️ \"{query}\" is already in your list.");
            return;
        }

        pendingRule = new SearchRule { Query = query, MaxPrice = maxPrice };
        step = "";

        string buttons = Telegram.InlineKeyboard(new[]
        {
            new[]
            {
                Telegram.CallbackButton(L("✅ Save now", "✅ Direct opslaan"), "url:save"),
                Telegram.CallbackButton(L("📂 Pick category", "📂 Kies categorie"), "nat:cat")
            },
            new[]
            {
                Telegram.CallbackButton(L("🚫 Skip words", "🚫 Skipwoorden"), "url:skip"),
                Telegram.CallbackButton(L("❌ Cancel", "❌ Annuleer"), "url:cancel")
            }
        });

        await session.Telegram.SendText(
            $"✨ <b>{L("Add this search?", "Deze zoekopdracht toevoegen?")}</b>\n" +
            $"• {L("Search", "Zoekterm")}: <b>{Encode(query)}</b>\n" +
            $"• {L("Max price", "Max prijs")}: <b>€{maxPrice}</b>\n" +
            $"• {L("Category", "Categorie")}: <b>All of Marktplaats</b>",
            buttons,
            parseMode: "HTML");
    }
    private async Task StartAddFromText(string searchText)
    {
        if (searchText.Contains(';'))
        {
            step = "";
            await session.Telegram.SendText("❌ Search text can't contain ';'. Try again with /add.");
            return;
        }

        if (session.SearchList.Rules.ContainsKey(searchText))
        {
            step = "";
            await session.Telegram.SendText($"⚠️ \"{searchText}\" is already in your list.");
            return;
        }

        pendingRule = new SearchRule { Query = searchText };
        step = "add_price";
        await session.Telegram.SendText($"💶 What's the max price for \"{searchText}\"?\nJust the number, like 140. (or /cancel)");
    }
    private async Task StartAddFromUrl(string url)
    {
        if (!MarktplaatsUrlParser.TryParse(url, out ParsedMarktplaatsUrl? parsed, out string error))
        {
            step = "";
            await session.Telegram.SendText("❌ " + error);
            return;
        }

        if (session.SearchList.Rules.ContainsKey(parsed!.Query))
        {
            step = "";
            await session.Telegram.SendText($"⚠️ \"{parsed.Query}\" is already in your list.");
            return;
        }

        pendingRule = new SearchRule
        {
            Query = parsed.Query,
            MaxPrice = parsed.MaxPrice ?? 0,
            Category = parsed.Category,
            SubCategory = parsed.SubCategory
        };

        string catLabel = FormatCategory(pendingRule.Category, pendingRule.SubCategory);

        if (parsed.MaxPrice.HasValue)
        {
            step = "";
            string summary =
                $"🔗 <b>Found from Marktplaats link:</b>\n" +
                $"• Search: <b>{Encode(pendingRule.Query)}</b>\n" +
                $"• Max price: <b>€{pendingRule.MaxPrice}</b>\n" +
                $"• Category: <b>{Encode(catLabel)}</b>";

            string buttons = Telegram.InlineKeyboard(new[]
            {
                new[]
                {
                    Telegram.CallbackButton("✅ Save now", "url:save"),
                    Telegram.CallbackButton("🚫 Add skip words", "url:skip")
                },
                new[]
                {
                    Telegram.CallbackButton("💶 Change price", "url:price"),
                    Telegram.CallbackButton("❌ Cancel", "url:cancel")
                }
            });

            await session.Telegram.SendText(summary, buttons, parseMode: "HTML");
            return;
        }

        step = "add_price";
        await session.Telegram.SendText(
            $"🔗 Parsed from link:\n• Search: <b>{Encode(pendingRule.Query)}</b>\n• Category: <b>{Encode(catLabel)}</b>\n\n💶 What's your max price in whole euros? (or /cancel)",
            parseMode: "HTML");
    }
    private async Task HandleStepInput(string text)
    {
        if (step == "add_search")
        {
            if (MarktplaatsUrlParser.IsMarktplaatsSearchUrl(text))
            {
                await StartAddFromUrl(text);
            }
            else if (TryParseNaturalSearch(text, out string q, out int p))
            {
                await OfferNaturalSearchAdd(q, p);
            }
            else
            {
                await StartAddFromText(text);
            }
        }
        else if (step == "add_price")
        {
            string priceInput = text.Replace("€", "").Trim();
            if (!int.TryParse(priceInput, out int price) || price < 0)
            {
                await session.Telegram.SendText("❌ Send just a whole number (0 or higher), like 140. (or /cancel)");
                return;
            }

            pendingRule.MaxPrice = price;
            if (pendingRule.Category != "")
            {
                await AskSkipWordsForAdd();
            }
            else
            {
                await AskCategoryForAdd();
            }
        }
        else if (step == "add_category")
        {
            if (!SearchList.TryParseCategory(text, out string cat, out int sub, out string catError))
            {
                await session.Telegram.SendText("❌ " + catError + " (or tap a button above, or /cancel)");
                return;
            }

            pendingRule.Category = cat;
            pendingRule.SubCategory = sub;
            await AskSkipWordsForAdd();
        }
        else if (step == "add_skip")
        {
            pendingRule.SkipWords = ParseSkipWords(text);
            await AskUrgentForAdd();
        }
        else if (step == "list_find")
        {
            step = "";
            await SendFilteredList(text);
        }
        else if (step == "price_lookup")
        {
            step = "";
            PriceCheckResult result = await lookupMarketPrice(session, text.Trim());
            await session.Telegram.SendText(PriceLookup.FormatHtml(result), HomeButtonMarkup(), parseMode: "HTML");
        }
        else if (step == "edit_price")
        {
            string priceInput = text.Replace("€", "").Trim();
            if (!int.TryParse(priceInput, out int price) || price < 0)
            {
                await session.Telegram.SendText("❌ Send a whole number (0 or higher), like 120. (or /cancel)");
                return;
            }

            SearchRule? rule = session.SearchList.FindById(editRuleId);
            step = "";
            if (rule == null)
            {
                await session.Telegram.SendText("⚠️ That search no longer exists.");
                return;
            }

            rule.MaxPrice = price;
            session.SearchList.UpdateRule(rule);
            await SendSearchDetail(rule, editReturnPage);
        }
        else if (step == "edit_skip")
        {
            SearchRule? rule = session.SearchList.FindById(editRuleId);
            step = "";
            if (rule == null)
            {
                await session.Telegram.SendText("⚠️ That search no longer exists.");
                return;
            }

            rule.SkipWords = ParseSkipWords(text);
            session.SearchList.UpdateRule(rule);
            await SendSearchDetail(rule, editReturnPage);
        }
        else if (step == "flip_name")
        {
            pendingFlipName = text.Trim();
            step = "flip_price";
            await session.Telegram.SendText(
                $"💶 {L("How much did you pay for", "Hoeveel heb je betaald voor")} <b>{Encode(pendingFlipName)}</b>? (e.g. 50)",
                parseMode: "HTML");
        }
        else if (step == "flip_price")
        {
            if (!TryParseEuro(text, out double buyPrice) || buyPrice < 0)
            {
                await session.Telegram.SendText("❌ Send a price like 50 or 45.50. (or /cancel)");
                return;
            }

            step = "";
            FlipItem item = session.FlipTracker.AddPurchase(pendingFlipName, buyPrice);
            await session.Telegram.SendText(
                $"🛒 Logged #{item.Id}: <b>{Encode(item.Name)}</b> ({Listing.FormatEuro(item.BuyPrice)}).",
                parseMode: "HTML");
            await SendFlipsMenu();
        }
        else if (step == "flip_parts")
        {
            if (!TryParseEuro(text, out double cost) || cost < 0)
            {
                await session.Telegram.SendText("❌ Send a cost like 12.50. (or /cancel)");
                return;
            }

            step = "";
            FlipItem? item = session.FlipTracker.AddParts($"#{editFlipId}", cost);
            if (item == null)
            {
                await session.Telegram.SendText("⚠️ That flip no longer exists.");
                return;
            }

            await SendFlipDetail(item);
        }
        else if (step == "flip_sold")
        {
            if (!TryParseEuro(text, out double sellPrice) || sellPrice < 0)
            {
                await session.Telegram.SendText("❌ Send the sale price like 135. (or /cancel)");
                return;
            }

            step = "";
            FlipItem? item = session.FlipTracker.MarkSold($"#{editFlipId}", sellPrice);
            if (item == null)
            {
                await session.Telegram.SendText("⚠️ That flip no longer exists.");
                return;
            }

            await SendSaleCelebration(item);
        }
        else if (step == "watch_target")
        {
            WatchItem? item = session.Watchlist.Get(targetWatchId);
            step = "";
            if (item == null)
            {
                await session.Telegram.SendText("⚠️ That listing is no longer in your watchlist.");
                return;
            }

            if (!TryParseEuro(text, out double target) || target < 0)
            {
                await session.Telegram.SendText("❌ Send a number like 80 (or 0 to alert on any drop).");
                return;
            }

            item.TargetPrice = target;
            session.Watchlist.Save();
            string note = target > 0 ? $"≤ {Listing.FormatEuro(target)}" : "any price drop";
            await session.Telegram.SendText($"🎯 Target price for <b>{Encode(item.Title)}</b> set to {note}.", parseMode: "HTML");
        }
        else if (step == "block_seller")
        {
            step = "";
            bool added = session.Blocklist.AddSeller(text);
            await session.Telegram.SendText(added ? $"🚫 Blocked seller \"{text}\"." : $"ℹ️ \"{text}\" is already blocked.");
            await SendBlocklistMenu();
        }
        else if (step == "block_word")
        {
            step = "";
            int added = session.Blocklist.AddGlobalSkipWords(ParseSkipWords(text));
            await session.Telegram.SendText(added > 0 ? $"🚫 Added {added} global skip word(s)." : "ℹ️ Already in your global skip list.");
            await SendBlocklistMenu();
        }
        else if (step == "ai_key")
        {
            step = "";
            session.AiGrader.SaveApiKey(text);
            session.Settings.AiEnabled = true;
            session.Settings.Save();
            await session.Telegram.SendText("✅ API key saved and AI grader turned ON!");
            await SendAiMenu();
        }
        else if (step == "ai_model")
        {
            step = "";
            session.Settings.AiModel = text.Trim();
            session.Settings.Save();
            await session.Telegram.SendText($"✅ AI model set to <code>{Encode(session.Settings.AiModel)}</code>.", parseMode: "HTML");
            await SendAiMenu();
        }
        else if (step == "ai_limit")
        {
            if (!int.TryParse(text.Trim(), out int limit) || limit < 1 || limit > 500)
            {
                await session.Telegram.SendText("❌ Send a whole number between 1 and 500. (or /cancel)");
                return;
            }

            step = "";
            session.Settings.AiDailyLimit = limit;
            session.Settings.Save();
            await session.Telegram.SendText($"✅ AI daily limit set to {limit} calls/day.");
            await SendAiMenu();
        }
        else if (step == "ai_url")
        {
            step = "";
            session.Settings.AiBaseUrl = text.Trim();
            session.Settings.Save();
            await session.Telegram.SendText($"✅ Ollama URL set to <code>{Encode(session.Settings.AiBaseUrl)}</code>.", parseMode: "HTML");
            await SendAiMenu();
        }
        else if (step == "set_postcode")
        {
            string clean = text.Trim().ToUpperInvariant().Replace(" ", "");
            if (clean.Length == 6 && int.TryParse(clean.Substring(0, 4), out _) &&
                char.IsLetter(clean[4]) && char.IsLetter(clean[5]))
            {
                session.Postcode = clean;
                session.SaveProfile();
                step = "";
                await session.Telegram.SendText($"✅ Postcode updated to {clean}.");
                await SendSettingsMenu();
            }
            else
            {
                await session.Telegram.SendText("❌ Use a Dutch postcode like 1234AB. (or /cancel)");
            }
        }
        else if (step == "set_km")
        {
            string clean = text.ToLowerInvariant().Replace("km", "").Trim();
            if (int.TryParse(clean, out int km) && km >= 0)
            {
                session.PickupKm = km;
                session.SaveProfile();
                step = "";
                await session.Telegram.SendText($"✅ Pickup distance updated to {km} km.");
                await SendSettingsMenu();
            }
            else
            {
                await session.Telegram.SendText("❌ Send a whole number (0 = shipping only), like 10. (or /cancel)");
            }
        }
        else if (step == "set_night_hours")
        {
            string[] parts = text.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int startHour) && startHour >= 0 && startHour <= 23 &&
                int.TryParse(parts[1], out int endHour) && endHour >= 0 && endHour <= 23 &&
                startHour != endHour)
            {
                session.Settings.QuietNightsEnabled = true;
                session.Settings.NightStartHour = startHour;
                session.Settings.NightEndHour = endHour;
                session.Settings.MorningDigestHour = endHour;
                session.Settings.Save();
                step = "";
                await session.Telegram.SendText($"✅ Quiet nights set to {startHour:00}:00–{endHour:00}:00.");
                await SendSettingsMenu();
            }
            else
            {
                await session.Telegram.SendText("❌ Send two hours separated by a dash, like 23-7 or 22-8. (or /cancel)");
            }
        }
    }
    private async Task AskCategoryForAdd(long? editMessageId = null)
    {
        step = "add_category";
        List<object[]> rows = new List<object[]>();
        for (int i = 0; i < Categories.Length; i += 2)
        {
            if (i + 1 < Categories.Length)
            {
                rows.Add(new[]
                {
                    Telegram.CallbackButton(Categories[i].Label, $"addcat:{Categories[i].Id}"),
                    Telegram.CallbackButton(Categories[i + 1].Label, $"addcat:{Categories[i + 1].Id}")
                });
            }
            else
            {
                rows.Add(new[] { Telegram.CallbackButton(Categories[i].Label, $"addcat:{Categories[i].Id}") });
            }
        }

        string text = "📂 Which category? Tap a button, or type a category number like 322 or 356/2954.";
        string markup = Telegram.InlineKeyboard(rows);
        if (editMessageId.HasValue)
        {
            await session.Telegram.EditMessage(editMessageId.Value, text, markup);
        }
        else
        {
            await session.Telegram.SendText(text, markup);
        }
    }
    private async Task AskSkipWordsForAdd(long? editMessageId = null)
    {
        step = "add_skip";
        string text =
            $"🚫 Any skip words for <b>{Encode(pendingRule.Query)}</b>?\n" +
            "Pick a preset below, or type comma-separated words (e.g. <code>barst,strepen</code>):";
        string markup = Telegram.InlineKeyboard(new[]
        {
            new[] { Telegram.CallbackButton("🖥️ Monitor faults (barst, strepen, waterschade)", "addskip:monitor") },
            new[] { Telegram.CallbackButton("💧 Waterschade only", "addskip:water") },
            new[] { Telegram.CallbackButton("⏭️ None", "addskip:none") }
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
    private async Task AskUrgentForAdd(long? editMessageId = null)
    {
        step = "add_urgent";
        string text =
            $"🚨 Make <b>{Encode(pendingRule.Query)}</b> urgent?\n" +
            "Urgent searches check faster and always notify with sound (even at night).";
        string markup = Telegram.InlineKeyboard(new[]
        {
            new[]
            {
                Telegram.CallbackButton("🚨 Yes, urgent", "addurg:yes"),
                Telegram.CallbackButton("🔔 No, normal", "addurg:no")
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
    private async Task SendFilteredList(string keyword)
    {
        List<SearchRule> matches = session.SearchList.Rules.Values
            .Where(r => r.Query.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Take(15)
            .ToList();

        if (matches.Count == 0)
        {
            await session.Telegram.SendText(
                $"🔍 No searches matching \"{keyword}\".",
                Telegram.InlineKeyboard(new[]
                {
                    new[]
                    {
                        Telegram.CallbackButton("🔍 Search again", "list:find"),
                        Telegram.CallbackButton("📋 All searches", "list:0")
                    }
                }));
            return;
        }

        List<object[]> rows = new List<object[]>();
        foreach (SearchRule rule in matches)
        {
            string prefix = rule.Urgent ? "🚨 " : "";
            rows.Add(new[] { Telegram.CallbackButton($"{prefix}{rule.Query} (€{rule.MaxPrice})", $"item:{rule.Id}:0") });
        }

        rows.Add(new[]
        {
            Telegram.CallbackButton("🔍 Find another", "list:find"),
            Telegram.CallbackButton("📋 All searches", "list:0"),
            Telegram.CallbackButton("🏠 Menu", "menu:home")
        });

        await session.Telegram.SendText(
            $"🔍 <b>Matches for \"{Encode(keyword)}\" ({matches.Count}):</b>",
            Telegram.InlineKeyboard(rows),
            parseMode: "HTML");
    }
    private async Task SendListPage(int page, long? editMessageId = null)
    {
        List<SearchRule> all = session.SearchList.Rules.Values.ToList();
        if (all.Count == 0)
        {
            string emptyText = "📋 You have no searches yet. Send /add, pick a Starter Pack, or paste a Marktplaats URL!";
            string emptyMarkup = Telegram.InlineKeyboard(new[]
            {
                new[]
                {
                    Telegram.CallbackButton("➕ Add Search", "menu:add"),
                    Telegram.CallbackButton("🎁 Starter Packs", "menu:packs")
                },
                new[] { Telegram.CallbackButton("🏠 Menu", "menu:home") }
            });
            if (editMessageId.HasValue)
            {
                await session.Telegram.EditMessage(editMessageId.Value, emptyText, emptyMarkup);
            }
            else
            {
                await session.Telegram.SendText(emptyText, emptyMarkup);
            }

            return;
        }

        int totalPages = (int)Math.Ceiling(all.Count / (double)PageSize);
        page = Math.Clamp(page, 0, totalPages - 1);

        int start = page * PageSize;
        int end = Math.Min(start + PageSize, all.Count);

        List<object[]> rows = new List<object[]>
        {
            new[]
            {
                Telegram.CallbackButton(L("🔍 Find", "🔍 Zoek"), "list:find"),
                Telegram.CallbackButton(L("➕ Add", "➕ Nieuw"), "menu:add"),
                Telegram.CallbackButton("🏠 Menu", "menu:home")
            }
        };

        for (int i = start; i < end; i++)
        {
            SearchRule rule = all[i];
            string prefix = rule.Urgent ? "🚨 " : "";
            string label = $"{prefix}{rule.Query} (€{rule.MaxPrice})";
            if (label.Length > 48)
            {
                label = label.Substring(0, 45) + "...";
            }

            rows.Add(new[] { Telegram.CallbackButton(label, $"item:{rule.Id}:{page}") });
        }

        if (totalPages > 1)
        {
            List<object> nav = new List<object>();
            if (page > 0)
            {
                nav.Add(Telegram.CallbackButton("⬅️ Prev", $"list:{page - 1}"));
            }

            nav.Add(Telegram.CallbackButton($"Page {page + 1}/{totalPages}", "noop"));

            if (page + 1 < totalPages)
            {
                nav.Add(Telegram.CallbackButton("Next ➡️", $"list:{page + 1}"));
            }

            rows.Add(nav.ToArray());
        }

        string text = $"📋 <b>{L("Your searches", "Jouw zoekopdrachten")} ({all.Count})</b> — {L("Page", "Pagina")} {page + 1}/{totalPages}\n{L("Tap a search to edit, test, or remove it:", "Tik op een zoekopdracht om te bewerken of testen:")}";
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
    private async Task SendSearchDetail(SearchRule rule, int returnPage, long? editMessageId = null)
    {
        string skipText = rule.SkipWords.Length > 0 ? string.Join(", ", rule.SkipWords) : "none";
        string urgentText = rule.Urgent ? "🚨 ON (fast + loud at night)" : "OFF";
        string categoryText = FormatCategory(rule.Category, rule.SubCategory);

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"🔎 <b>{Encode(rule.Query)}</b>\n");
        sb.AppendLine($"💶 Max price: <b>€{rule.MaxPrice}</b>");
        sb.AppendLine($"📂 Category: <b>{Encode(categoryText)}</b>");
        sb.AppendLine($"🚫 Skip words: <code>{Encode(skipText)}</code>");
        sb.Append($"🚨 Urgent: <b>{urgentText}</b>");

        string markup = Telegram.InlineKeyboard(new[]
        {
            new[]
            {
                Telegram.CallbackButton(L("⚡ Check now", "⚡ Check nu"), $"chk:{rule.Id}:{returnPage}"),
                Telegram.CallbackButton(L("💶 Edit price", "💶 Prijs"), $"editp:{rule.Id}:{returnPage}")
            },
            new[]
            {
                Telegram.CallbackButton(rule.Urgent ? "🚨 Urgent: ON" : "🔔 Urgent: OFF", $"turg:{rule.Id}:{returnPage}"),
                Telegram.CallbackButton(L("🚫 Skip words", "🚫 Skipwoorden"), $"edits:{rule.Id}:{returnPage}")
            },
            new[]
            {
                Telegram.CallbackButton(L("🗑️ Delete", "🗑️ Verwijder"), $"del:{rule.Id}:{returnPage}"),
                Telegram.CallbackButton(L("⬅️ Back to list", "⬅️ Terug"), $"list:{returnPage}")
            }
        });

        if (editMessageId.HasValue)
        {
            await session.Telegram.EditMessage(editMessageId.Value, sb.ToString(), markup, parseMode: "HTML");
        }
        else
        {
            await session.Telegram.SendText(sb.ToString(), markup, parseMode: "HTML");
        }
    }
    private string FormatCategory(string category, int subCategory)
    {
        if (category == "")
        {
            return "All of Marktplaats";
        }

        string? knownName = null;
        foreach ((string label, string id) in Categories)
        {
            if (id == category)
            {
                knownName = label;
                break;
            }
        }

        string baseText = knownName != null ? $"{knownName} ({category})" : category;
        if (subCategory != 0)
        {
            string subName = categoryNames.GetValueOrDefault(subCategory, "");
            baseText += subName != "" ? $" / {subName} ({subCategory})" : $" / {subCategory}";
        }

        return baseText;
    }
    private static string[] ParseSkipWords(string text)
    {
        return text.ToLowerInvariant()
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
}

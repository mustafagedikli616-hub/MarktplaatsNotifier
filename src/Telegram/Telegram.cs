using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MarktplaatsNotifier;

public class Telegram
{
    private const int MaxMessageLength = 4000;
    private const int MaxCaptionLength = 1024;
    private static readonly Regex HtmlTags = new Regex("<[^>]+>", RegexOptions.Compiled);

    private readonly HttpClient client;
    private readonly string token;
    private readonly string chatId;

    public Telegram(HttpClient client, string token, string chatId)
    {
        this.client = client;
        this.token = token;
        this.chatId = chatId;
    }

    public static string InlineKeyboard(IEnumerable<IEnumerable<object>> rows)
    {
        return JsonSerializer.Serialize(new { inline_keyboard = rows });
    }

    public static object CallbackButton(string text, string callbackData)
    {
        return new { text, callback_data = callbackData };
    }

    public static object UrlButton(string text, string url)
    {
        return new { text, url };
    }

    public static string ListingButtons(string fullUrl, string itemId, bool watching, bool dutch = false, string source = "Marktplaats")
    {
        string openLabel = source.ToLowerInvariant() switch
        {
            "tweakers" => "🔗 Bekijk op Tweakers",
            "2dehands.be" => "🔗 Bekijk op 2dehands.be",
            "coolblue tweedekans" => "🔗 Bekijk op Coolblue",
            "vinted" => "🔗 Bekijk op Vinted",
            _ => "🔗 Bekijk op Marktplaats"
        };

        bool isMarktplaats = source.Equals("Marktplaats", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(itemId) || !isMarktplaats)
        {
            return InlineKeyboard(new[]
            {
                new[] { UrlButton(openLabel, fullUrl) }
            });
        }

        string watchLabel = dutch
            ? (watching ? "✅ Gevolgd" : "👀 Volgen")
            : (watching ? "✅ Watching" : "👀 Watch");
        return InlineKeyboard(new[]
        {
            new[]
            {
                UrlButton(openLabel, fullUrl),
                CallbackButton(watchLabel, $"w:{itemId}")
            }
        });
    }

    public async Task SetCommands(object commands)
    {
        string commandsJson = JsonSerializer.Serialize(commands);
        await PostAsync("setMyCommands", new Dictionary<string, string>
        {
            ["commands"] = commandsJson
        });
    }

    public async Task<string> GetUpdatesJson(long offset)
    {
        return await PostAsync("getUpdates", new Dictionary<string, string>
        {
            ["offset"] = offset.ToString()
        });
    }

    public async Task SendText(string text, string replyMarkup = "", string parseMode = "", bool silent = false)
    {
        List<string> chunks = SplitIntoChunks(text, MaxMessageLength);
        for (int i = 0; i < chunks.Count; i++)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>
            {
                ["chat_id"] = chatId,
                ["text"] = chunks[i]
            };

            if (parseMode != "")
            {
                fields["parse_mode"] = parseMode;
            }

            if (silent)
            {
                fields["disable_notification"] = "true";
            }

            if (replyMarkup != "" && i == chunks.Count - 1)
            {
                fields["reply_markup"] = replyMarkup;
            }

            await PostAsync("sendMessage", fields);
        }
    }

    public async Task SendDocument(byte[] fileBytes, string fileName, string caption = "")
    {
        using MultipartFormDataContent form = new MultipartFormDataContent();
        form.Add(new StringContent(chatId), "chat_id");
        if (caption != "")
        {
            form.Add(new StringContent(caption), "caption");
        }

        ByteArrayContent fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(fileContent, "document", fileName);

        using HttpResponseMessage response =
            await client.PostAsync($"https://api.telegram.org/bot{token}/sendDocument", form);
        response.EnsureSuccessStatusCode();
    }

    public async Task<byte[]?> DownloadFileAsync(string fileId)
    {
        string json = await PostAsync("getFile", new Dictionary<string, string>
        {
            ["file_id"] = fileId
        });
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out JsonElement result) ||
            !result.TryGetProperty("file_path", out JsonElement pathEl))
        {
            return null;
        }

        string filePath = pathEl.GetString() ?? "";
        if (filePath == "")
        {
            return null;
        }

        return await client.GetByteArrayAsync($"https://api.telegram.org/file/bot{token}/{filePath}");
    }

    public async Task EditMessage(long messageId, string text, string replyMarkup = "", string parseMode = "")
    {
        Dictionary<string, string> fields = new Dictionary<string, string>
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId.ToString(),
            ["text"] = text
        };

        if (parseMode != "")
        {
            fields["parse_mode"] = parseMode;
        }

        if (replyMarkup != "")
        {
            fields["reply_markup"] = replyMarkup;
        }

        try
        {
            await PostAsync("editMessageText", fields);
        }
        catch (HttpRequestException)
        {
        }
    }

    public async Task EditReplyMarkup(long messageId, string replyMarkup)
    {
        Dictionary<string, string> fields = new Dictionary<string, string>
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId.ToString(),
            ["reply_markup"] = replyMarkup
        };

        try
        {
            await PostAsync("editMessageReplyMarkup", fields);
        }
        catch (HttpRequestException)
        {
        }
    }

    public async Task AnswerCallbackQuery(string callbackQueryId, string text = "")
    {
        Dictionary<string, string> fields = new Dictionary<string, string>
        {
            ["callback_query_id"] = callbackQueryId
        };
        if (text != "")
        {
            fields["text"] = text;
        }

        try
        {
            await PostAsync("answerCallbackQuery", fields);
        }
        catch
        {
        }
    }

    public async Task<long> SendListing(Listing ad, bool night, string alert, bool isWatched = false, bool dutch = false)
    {
        string place = ad.City;
        if (ad.DistanceKm >= 0)
        {
            place += $" ({ad.DistanceKm} km)";
        }

        StringBuilder sb = new StringBuilder();
        if (alert != "")
        {
            sb.Append($"🚨 <b>{Encode(alert)}</b> 🚨\n\n");
        }

        string sourceHeader = ad.Source.ToLowerInvariant() switch
        {
            "tweakers" => "🔧 <b>Tweakers V&amp;A</b>\n",
            "2dehands.be" => "🇧🇪 <b>2dehands.be</b>\n",
            "coolblue tweedekans" => "🔵 <b>Coolblue Tweedekans</b>\n",
            "vinted" => "🛍️ <b>Vinted</b>\n",
            _ => ""
        };
        if (sourceHeader != "")
        {
            sb.Append(sourceHeader);
        }

        sb.Append($"<b>{Encode(ad.Title)}</b>\n");

        if (ad.Price > 0 && ad.TypicalPrice > 0 && !ad.LikelyBusiness && ad.SellerType != "TRADER")
        {
            int pctUnder = (int)Math.Round((ad.TypicalPrice - ad.Price) / ad.TypicalPrice * 100);
            if (pctUnder >= 30)
            {
                string privateTag = ad.SellerType == "CONSUMER" ? " · private seller" : "";
                sb.Append($"⭐ <b>Great deal</b> · {pctUnder}% under typical{privateTag}\n");
            }
            else if (pctUnder >= 20)
            {
                sb.Append($"👍 <b>Good price</b> · {pctUnder}% under typical\n");
            }
        }

        sb.Append($"💶 {Encode(ad.PriceText)}");
        if (ad.Condition != "")
        {
            sb.Append($" · 🏷️ {Encode(ad.Condition)}");
        }
        sb.Append('\n');

        if (ad.Price == 0 && ad.MentionedPrice > 0)
        {
            sb.Append($"💬 Mentions {Listing.FormatEuro(ad.MentionedPrice)} in description\n");
        }

        if (ad.OldPrice > 0)
        {
            sb.Append($"📉 Price drop: {Listing.FormatEuro(ad.OldPrice)} → {Listing.FormatEuro(ad.Price)}\n");
        }

        if (ad.TypicalPrice > 0)
        {
            int difference = (int)Math.Round((ad.Price - ad.TypicalPrice) / ad.TypicalPrice * 100);
            string compared = difference <= 0 ? $"{-difference}% under" : $"{difference}% over";
            sb.Append($"📊 Typical {Listing.FormatEuro(ad.TypicalPrice)} ({ad.TypicalCount} similar listings) · {compared}\n");
        }

        if (ad.RepostInfo != "")
        {
            sb.Append($"🔁 {Encode(ad.RepostInfo)}\n");
        }
        if (ad.FaultReason != "")
        {
            sb.Append($"{Encode(ad.FaultReason)}\n");
        }

        sb.Append($"📍 {Encode(place)}");
        if (ad.Delivery != "")
        {
            sb.Append($" · 📦 {Encode(ad.Delivery)}");
        }
        sb.Append('\n');

        if (ad.PostedAt != DateTime.MinValue)
        {
            int minutes = Math.Max(0, (int)(DateTime.Now - ad.PostedAt).TotalMinutes);
            if (minutes < 60)
            {
                sb.Append($"🕒 online {minutes} min");
            }
            else
            {
                sb.Append($"🕒 online {minutes / 60} h");
            }
        }
        else if (ad.Date != "")
        {
            sb.Append($"📅 {Encode(ad.Date)}");
        }

        if (ad.Views >= 0)
        {
            sb.Append($" · 👀 {ad.Views}");
            if (ad.Saves >= 0)
            {
                sb.Append($" · ❤️ {ad.Saves}");
            }
        }
        sb.Append('\n');

        if (ad.BidCount > 0)
        {
            sb.Append($"💰 {ad.BidCount} bids, highest {Listing.FormatEuro(ad.HighestBid)}\n");
        }

        sb.Append($"👤 {Encode(ad.SellerName)}");
        if (ad.SellerVerified)
        {
            sb.Append(" ✓");
        }
        if (ad.SellerRating >= 0)
        {
            string rev = ad.SellerReviewCount >= 0 ? $" ({ad.SellerReviewCount})" : "";
            sb.Append($" · ⭐ {ad.SellerRating.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}{rev}");
        }
        if (ad.SellerYears >= 0)
        {
            sb.Append($" · {ad.SellerYears} yr on Marktplaats");
        }
        if (ad.SellerType == "CONSUMER")
        {
            sb.Append(" · private");
        }
        else if (ad.SellerType != "")
        {
            sb.Append(" · business");
        }
        sb.Append($" · 📸 {ad.PhotoCount}\n");

        if (ad.ScamWarning != "")
        {
            sb.Append($"⚠️ <b>Scam check:</b> {Encode(ad.ScamWarning)}\n");
        }
        else
        {
            double effectivePrice = ad.Price > 0 ? ad.Price : ad.HighestBid;
            if (ad.SellerYears == 0 && ad.SellerReviewCount <= 0 && !ad.SellerVerified && ad.CanShip && effectivePrice >= 50)
            {
                sb.Append(dutch
                    ? "⚠️ Nieuw ongeverifieerd account (0 jr, 0 reviews) - pas op bij verzenden\n"
                    : "⚠️ New unverified account (0 yr, 0 reviews) - watch out with shipping\n");
            }
        }

        if (ad.Description != "")
        {
            string snippet = ad.Description;
            if (snippet.Length > 150)
            {
                snippet = snippet.Substring(0, 150) + "...";
            }
            sb.Append($"📝 <i>{Encode(snippet)}</i>\n");
        }

        if (ad.AiVerdict != "")
        {
            string icon = ad.AiVerdict switch
            {
                "BUY" => "🟢 BUY",
                "MAYBE" => "🟡 MAYBE",
                "SKIP" => "🔴 SKIP",
                _ => Encode(ad.AiVerdict)
            };
            sb.Append($"🤖 <b>AI: {icon}</b>");
            if (ad.AiReason != "")
            {
                sb.Append($" · {Encode(ad.AiReason)}");
            }
            if (ad.AiResale != "")
            {
                sb.Append($" · Resale {Encode(ad.AiResale)}");
            }
            if (ad.AiMaxBid != "")
            {
                sb.Append($" (max {Encode(ad.AiMaxBid)})");
            }
            sb.Append('\n');
        }

        if (ad.Hint != "")
        {
            sb.Append($"{Encode(ad.Hint)}\n");
            if (ad.HintQuestion != "")
            {
                sb.Append($"💬 Ask: <code>{Encode(ad.HintQuestion)}</code>\n");
            }
        }

        sb.Append($"🔍 via: {Encode(ad.MatchedSearch)}");
        if (ad.CategoryName != "")
        {
            sb.Append($" · 📂 {Encode(ad.CategoryName)} ({ad.CategoryId})");
        }
        sb.Append('\n');

        if (ad.IsBumped)
        {
            sb.Append("🔁 Dagtopper (paid to the top, may be an old listing)\n");
        }
        if (ad.LikelyBusiness)
        {
            sb.Append("🏢 Probably a business\n");
        }

        string fullUrl = ad.Link.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? ad.Link
            : "https://www.marktplaats.nl" + ad.Link;

        string replyMarkup = ListingButtons(fullUrl, ad.Id, isWatched, dutch, ad.Source);

        string message = sb.ToString().TrimEnd();
        bool silent = night && alert == "";
        int plainLength = WebUtility.HtmlDecode(HtmlTags.Replace(message, "")).Length;

        if (ad.Photo != "" && plainLength <= MaxCaptionLength)
        {
            try
            {
                Dictionary<string, string> photoFields = new Dictionary<string, string>
                {
                    ["chat_id"] = chatId,
                    ["photo"] = ad.Photo,
                    ["caption"] = message,
                    ["parse_mode"] = "HTML",
                    ["reply_markup"] = replyMarkup
                };
                if (silent)
                {
                    photoFields["disable_notification"] = "true";
                }

                string photoJson = await PostAsync("sendPhoto", photoFields);
                return ExtractMessageId(photoJson);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                BotLog.Write($"Photo message failed ({ex.Message}), sending as text instead.");
            }
        }

        Dictionary<string, string> textFields = new Dictionary<string, string>
        {
            ["chat_id"] = chatId,
            ["text"] = message,
            ["parse_mode"] = "HTML",
            ["reply_markup"] = replyMarkup
        };
        if (silent)
        {
            textFields["disable_notification"] = "true";
        }

        string textJson = await PostAsync("sendMessage", textFields);
        return ExtractMessageId(textJson);
    }

    private static long ExtractMessageId(string responseJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("result", out JsonElement result) &&
                result.TryGetProperty("message_id", out JsonElement idEl) &&
                idEl.ValueKind == JsonValueKind.Number &&
                idEl.TryGetInt64(out long msgId))
            {
                return msgId;
            }
        }
        catch
        {
        }

        return 0;
    }

    private async Task<string> PostAsync(string method, Dictionary<string, string> fields)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using FormUrlEncodedContent content = new FormUrlEncodedContent(fields);
            using HttpResponseMessage response =
                await client.PostAsync($"https://api.telegram.org/bot{token}/{method}", content);

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2)
            {
                string body = await response.Content.ReadAsStringAsync();
                int retryAfter = ExtractRetryAfterSeconds(body);
                BotLog.Write($"Telegram 429 on {method}: waiting {retryAfter}s before retrying...");
                await Task.Delay(TimeSpan.FromSeconds(retryAfter));
                continue;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        throw new HttpRequestException($"Telegram {method} failed after retries.");
    }

    private static int ExtractRetryAfterSeconds(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("parameters", out JsonElement p) &&
                p.TryGetProperty("retry_after", out JsonElement ra) &&
                ra.ValueKind == JsonValueKind.Number &&
                ra.TryGetInt32(out int secs) && secs > 0)
            {
                return Math.Clamp(secs, 1, 120);
            }
        }
        catch
        {
        }

        return 5;
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static List<string> SplitIntoChunks(string text, int maxLength)
    {
        List<string> chunks = new List<string>();
        if (text.Length <= maxLength)
        {
            chunks.Add(text);
            return chunks;
        }

        int start = 0;
        while (start < text.Length)
        {
            int remaining = text.Length - start;
            if (remaining <= maxLength)
            {
                chunks.Add(text.Substring(start));
                break;
            }

            int cut = text.LastIndexOf('\n', start + maxLength, maxLength);
            if (cut <= start)
            {
                cut = start + maxLength;
            }

            chunks.Add(text.Substring(start, cut - start));
            start = cut;
            if (start < text.Length && text[start] == '\n')
            {
                start++;
            }
        }

        return chunks;
    }
}

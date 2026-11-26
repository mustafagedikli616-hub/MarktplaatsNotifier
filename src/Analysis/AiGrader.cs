using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MarktplaatsNotifier;

public class AiGradeResult
{
    public string Verdict { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Resale { get; set; } = "";
    public string MaxBid { get; set; } = "";
}

public class AiGrader
{
    public static readonly (string Id, string Name, string DefaultModel, string Note)[] Providers =
    {
        ("gemini", "✨ Gemini (Recommended)", "gemini-2.5-flash", "Free tier + generous limits · aistudio.google.com/apikey"),
        ("groq", "⚡ Groq", "meta-llama/llama-4-scout-17b-16e-instruct", "Fast free tier · console.groq.com"),
        ("openrouter", "🌐 OpenRouter (100+ models)", "google/gemini-2.5-flash", "Gemini, Claude, GPT, Llama, Qwen · openrouter.ai/keys"),
        ("claude", "🧠 Claude", "claude-haiku-4-5", "Anthropic API · console.anthropic.com"),
        ("openai", "🤖 OpenAI", "gpt-4o-mini", "GPT-4o / GPT-4.1 · platform.openai.com"),
        ("ollama", "🏠 Ollama (Local / Mini PC)", "llama3.2-vision", "100% free & offline · no API key needed")
    };

    private const int MaxPhotosToSend = 2;
    private const int MaxImageBytes = 2 * 1024 * 1024;

    private readonly HttpClient aiClient;
    private readonly HttpClient imageClient;
    private readonly string keyFilePath;

    private DateOnly usageDate = DateOnly.FromDateTime(DateTime.Now);
    private int callsToday = 0;

    public int CallsToday
    {
        get
        {
            RolloverDay();
            return callsToday;
        }
    }

    public AiGrader(HttpClient imageClient, string? dataDir = null)
    {
        this.imageClient = imageClient;
        aiClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        string dir = dataDir ?? AppFiles.DataDirectory;
        keyFilePath = Path.Combine(dir, "ai_key.txt");
    }

    public static string DefaultModelFor(string provider)
    {
        foreach (var p in Providers)
        {
            if (p.Id.Equals(provider, StringComparison.OrdinalIgnoreCase))
            {
                return p.DefaultModel;
            }
        }

        return "gemini-2.5-flash";
    }

    public string GetApiKey(string provider)
    {
        if (File.Exists(keyFilePath))
        {
            string saved = File.ReadAllText(keyFilePath).Trim();
            if (saved != "")
            {
                return saved;
            }
        }

        string? generic = Environment.GetEnvironmentVariable("AI_API_KEY");
        if (!string.IsNullOrWhiteSpace(generic))
        {
            return generic.Trim();
        }

        string envVar = provider.ToLowerInvariant() switch
        {
            "gemini" => "GEMINI_API_KEY",
            "claude" => "ANTHROPIC_API_KEY",
            "openai" => "OPENAI_API_KEY",
            "openrouter" => "OPENROUTER_API_KEY",
            "groq" => "GROQ_API_KEY",
            _ => ""
        };

        return envVar != "" ? (Environment.GetEnvironmentVariable(envVar) ?? "").Trim() : "";
    }

    public void SaveApiKey(string key)
    {
        File.WriteAllText(keyFilePath, key.Trim());
    }

    public bool IsReady(BotSettings settings)
    {
        if (!settings.AiEnabled)
        {
            return false;
        }

        if (settings.AiProvider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return GetApiKey(settings.AiProvider) != "";
    }

    public async Task<AiGradeResult?> GradeAsync(Listing ad, SearchRule rule, BotSettings settings)
    {
        RolloverDay();
        if (!IsReady(settings))
        {
            return null;
        }

        if (callsToday >= settings.AiDailyLimit)
        {
            return null;
        }

        string provider = settings.AiProvider.ToLowerInvariant();
        string model = string.IsNullOrWhiteSpace(settings.AiModel)
            ? DefaultModelFor(provider)
            : settings.AiModel.Trim();
        string apiKey = GetApiKey(provider);

        try
        {
            List<(string MimeType, string Base64)> images = await DownloadPhotosAsync(ad);
            string prompt = BuildPrompt(ad, rule);

            string rawResponse = provider switch
            {
                "gemini" => await CallGeminiAsync(apiKey, model, prompt, images),
                "claude" => await CallClaudeAsync(apiKey, model, prompt, images),
                "ollama" => await CallOllamaAsync(settings.AiBaseUrl, model, prompt, images),
                "openai" => await CallOpenAiCompatibleAsync(
                    "https://api.openai.com/v1/chat/completions", apiKey, model, prompt, images),
                "openrouter" => await CallOpenAiCompatibleAsync(
                    "https://openrouter.ai/api/v1/chat/completions", apiKey, model, prompt, images),
                "groq" => await CallOpenAiCompatibleAsync(
                    "https://api.groq.com/openai/v1/chat/completions", apiKey, model, prompt, images),
                _ => ""
            };

            callsToday++;
            return ParseGradeJson(rawResponse);
        }
        catch (Exception ex)
        {
            BotLog.Write($"AI grader ({provider}/{model}) failed for '{ad.Title}': {ex.Message}");
            return null;
        }
    }

    private async Task<List<(string MimeType, string Base64)>> DownloadPhotosAsync(Listing ad)
    {
        List<(string MimeType, string Base64)> results = new List<(string, string)>();
        List<string> urls = ad.PhotoUrls.Count > 0
            ? ad.PhotoUrls
            : (ad.Photo != "" ? new List<string> { ad.Photo } : new List<string>());

        foreach (string url in urls.Take(MaxPhotosToSend))
        {
            try
            {
                using HttpResponseMessage resp = await imageClient.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    continue;
                }

                byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
                {
                    continue;
                }

                string mime = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    mime = "image/jpeg";
                }

                results.Add((mime, Convert.ToBase64String(bytes)));
            }
            catch
            {
            }
        }

        return results;
    }

    private static string BuildPrompt(Listing ad, SearchRule rule)
    {
        string typical = ad.TypicalPrice > 0
            ? $"{Listing.FormatEuro(ad.TypicalPrice)} ({ad.TypicalCount} similar listings)"
            : "unknown";
        string desc = ad.Description.Length > 1200 ? ad.Description.Substring(0, 1200) : ad.Description;

        return
            "You are grading a Dutch Marktplaats.nl electronics listing for a repair-and-resell buyer.\n" +
            "Inspect the text AND the attached photos carefully (especially for cracked LCD panels, vertical lines, ink bleed, water damage, bent pins, or missing parts).\n" +
            $"Search: {rule.Query} (buyer max budget: €{rule.MaxPrice})\n" +
            $"Title: {ad.Title}\n" +
            $"Price: {ad.PriceText} (Highest bid: {(ad.HighestBid > 0 ? Listing.FormatEuro(ad.HighestBid) : "none")}, Typical: {typical})\n" +
            $"Condition: {ad.Condition}\n" +
            $"Description:\n{desc}\n\n" +
            "Respond with ONLY valid JSON (no markdown fences) with these exact keys:\n" +
            "{\"verdict\":\"BUY|MAYBE|SKIP\",\"reason\":\"max 18 words in English explaining why and mentioning any damage seen in photos\",\"resale\":\"estimated Dutch working resale like €120-150\",\"max_bid\":\"suggested max buy price like €55\"}";
    }

    private async Task<string> CallGeminiAsync(
        string apiKey, string model, string prompt, List<(string MimeType, string Base64)> images)
    {
        List<object> parts = new List<object> { new { text = prompt } };
        foreach (var img in images)
        {
            parts.Add(new
            {
                inline_data = new { mime_type = img.MimeType, data = img.Base64 }
            });
        }

        object payload = new
        {
            contents = new[] { new { parts } },
            generationConfig = new { temperature = 0.2, responseMimeType = "application/json" }
        };

        string url =
            $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(apiKey)}";

        using StringContent body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await aiClient.PostAsync(url, body);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? "";
    }

    private async Task<string> CallClaudeAsync(
        string apiKey, string model, string prompt, List<(string MimeType, string Base64)> images)
    {
        List<object> content = new List<object>();
        foreach (var img in images)
        {
            content.Add(new
            {
                type = "image",
                source = new { type = "base64", media_type = img.MimeType, data = img.Base64 }
            });
        }
        content.Add(new { type = "text", text = prompt });

        object payload = new
        {
            model,
            max_tokens = 250,
            temperature = 0.2,
            messages = new[] { new { role = "user", content } }
        };

        using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await aiClient.SendAsync(req);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }

    private async Task<string> CallOpenAiCompatibleAsync(
        string endpoint, string apiKey, string model, string prompt, List<(string MimeType, string Base64)> images)
    {
        List<object> content = new List<object> { new { type = "text", text = prompt } };
        foreach (var img in images)
        {
            content.Add(new
            {
                type = "image_url",
                image_url = new { url = $"data:{img.MimeType};base64,{img.Base64}" }
            });
        }

        object payload = new
        {
            model,
            max_tokens = 250,
            temperature = 0.2,
            messages = new[] { new { role = "user", content } }
        };

        using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await aiClient.SendAsync(req);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    private async Task<string> CallOllamaAsync(
        string baseUrl, string model, string prompt, List<(string MimeType, string Base64)> images)
    {
        string endpoint = (string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:11434" : baseUrl.TrimEnd('/')) + "/api/chat";
        object message = images.Count > 0
            ? new { role = "user", content = prompt, images = images.Select(i => i.Base64).ToArray() }
            : (object)new { role = "user", content = prompt };

        object payload = new
        {
            model,
            stream = false,
            format = "json",
            messages = new[] { message }
        };

        using StringContent body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await aiClient.PostAsync(endpoint, body);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private static AiGradeResult? ParseGradeJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        int start = raw.IndexOf('{');
        int end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        string json = raw.Substring(start, end - start + 1);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        string verdict = root.TryGetProperty("verdict", out JsonElement v)
            ? (v.GetString() ?? "").Trim().ToUpperInvariant()
            : "";
        if (verdict != "BUY" && verdict != "MAYBE" && verdict != "SKIP")
        {
            return null;
        }

        return new AiGradeResult
        {
            Verdict = verdict,
            Reason = root.TryGetProperty("reason", out JsonElement r) ? (r.GetString() ?? "").Trim() : "",
            Resale = root.TryGetProperty("resale", out JsonElement rs) ? (rs.GetString() ?? "").Trim() : "",
            MaxBid = root.TryGetProperty("max_bid", out JsonElement mb) ? (mb.GetString() ?? "").Trim() : ""
        };
    }

    private void RolloverDay()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        if (today != usageDate)
        {
            usageDate = today;
            callsToday = 0;
        }
    }
}

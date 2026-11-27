using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MarktplaatsNotifier;

public partial class CommandHandler
{
    private async Task HandleAiCallback(string callbackId, long messageId, string[] parts)
    {
        string sub = parts[1];
        if (sub == "toggle")
        {
            if (!session.Settings.AiEnabled &&
                !session.Settings.AiProvider.Equals("ollama", StringComparison.OrdinalIgnoreCase) &&
                session.AiGrader.GetApiKey(session.Settings.AiProvider) == "")
            {
                step = "ai_key";
                await session.Telegram.AnswerCallbackQuery(callbackId, "Add an API key first");
                await PromptAiApiKey();
                return;
            }

            session.Settings.AiEnabled = !session.Settings.AiEnabled;
            session.Settings.Save();
            await session.Telegram.AnswerCallbackQuery(callbackId, session.Settings.AiEnabled ? "AI ON 🤖" : "AI OFF");
            await SendAiMenu(messageId);
        }
        else if (sub == "prov" && parts.Length >= 3)
        {
            string provider = parts[2].ToLowerInvariant();
            session.Settings.AiProvider = provider;
            session.Settings.AiModel = AiGrader.DefaultModelFor(provider);
            session.Settings.Save();
            await session.Telegram.AnswerCallbackQuery(callbackId, $"Switched to {provider}");
            await SendAiMenu(messageId);
        }
        else if (sub == "key")
        {
            step = "ai_key";
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await PromptAiApiKey();
        }
        else if (sub == "model")
        {
            step = "ai_model";
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await session.Telegram.SendText(
                $"🎛️ Send the model name for <b>{Encode(session.Settings.AiProvider)}</b> (currently <code>{Encode(session.Settings.AiModel)}</code>).\n" +
                "Examples: <code>gemini-2.5-flash</code>, <code>gemini-2.5-flash-lite</code>, <code>claude-haiku-4-5</code>, <code>gpt-4o-mini</code>, <code>llama3.2-vision</code>. (or /cancel)",
                parseMode: "HTML");
        }
        else if (sub == "limit")
        {
            step = "ai_limit";
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await session.Telegram.SendText($"📊 Send max AI calls per day (currently {session.Settings.AiDailyLimit}, range 1–500). (or /cancel)");
        }
        else if (sub == "url")
        {
            step = "ai_url";
            await session.Telegram.AnswerCallbackQuery(callbackId);
            await session.Telegram.SendText($"🏠 Send your Ollama URL (currently <code>{Encode(session.Settings.AiBaseUrl)}</code>). (or /cancel)", parseMode: "HTML");
        }
        else if (sub == "autoskip")
        {
            session.Settings.AiAutoSkip = !session.Settings.AiAutoSkip;
            session.Settings.Save();
            await session.Telegram.AnswerCallbackQuery(callbackId, session.Settings.AiAutoSkip ? "Auto-skipping AI SKIP" : "Showing AI SKIP with tag");
            await SendAiMenu(messageId);
        }
        else
        {
            await session.Telegram.AnswerCallbackQuery(callbackId);
        }
    }
    private async Task PromptAiApiKey()
    {
        string providerNote = "";
        foreach (var p in AiGrader.Providers)
        {
            if (p.Id.Equals(session.Settings.AiProvider, StringComparison.OrdinalIgnoreCase))
            {
                providerNote = p.Note;
                break;
            }
        }

        await session.Telegram.SendText(
            $"🔑 Paste your API key for <b>{Encode(session.Settings.AiProvider)}</b>.\n" +
            $"<i>{Encode(providerNote)}</i>\n\n" +
            "Your key is saved locally in <code>ai_key.txt</code> and never included in /backup. (or /cancel)",
            parseMode: "HTML");
    }
    private async Task SendAiMenu(long? editMessageId = null)
    {
        BotSettings s = session.Settings;
        bool isOllama = s.AiProvider.Equals("ollama", StringComparison.OrdinalIgnoreCase);
        bool hasKey = isOllama || session.AiGrader.GetApiKey(s.AiProvider) != "";
        string keyStatus = isOllama ? "Not needed (local)" : (hasKey ? "✅ Configured" : "❌ Not set");

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("🤖 <b>AI Photo & Deal Grader</b>\n");
        sb.AppendLine($"Status: <b>{(s.AiEnabled ? "🟢 ON" : "⚪ OFF")}</b>");
        sb.AppendLine($"Provider: <b>{Encode(s.AiProvider)}</b>");
        sb.AppendLine($"Model: <code>{Encode(s.AiModel)}</code>");
        sb.AppendLine($"API Key: <b>{keyStatus}</b>");
        sb.AppendLine($"Used today: <b>{session.AiGrader.CallsToday} / {s.AiDailyLimit}</b>");
        sb.AppendLine($"Auto-skip AI SKIP: <b>{(s.AiAutoSkip ? "ON" : "OFF (tag only)")}</b>");
        if (isOllama)
        {
            sb.AppendLine($"Ollama URL: <code>{Encode(s.AiBaseUrl)}</code>");
        }
        sb.Append("\nTip: <b>Gemini</b> has a free tier with decent limits (aistudio.google.com/apikey). OpenRouter works with most other models too.");

        List<object[]> rows = new List<object[]>
        {
            new[]
            {
                Telegram.CallbackButton(s.AiEnabled ? "🟢 AI Grader: ON" : "⚪ AI Grader: OFF", "ai:toggle"),
                Telegram.CallbackButton(s.AiAutoSkip ? "🚫 Auto-skip: ON" : "🏷️ Auto-skip: OFF", "ai:autoskip")
            },
            new[]
            {
                Telegram.CallbackButton("✨ Gemini (Rec.)", "ai:prov:gemini"),
                Telegram.CallbackButton("⚡ Groq", "ai:prov:groq")
            },
            new[]
            {
                Telegram.CallbackButton("🌐 OpenRouter", "ai:prov:openrouter"),
                Telegram.CallbackButton("🧠 Claude", "ai:prov:claude")
            },
            new[]
            {
                Telegram.CallbackButton("🤖 OpenAI", "ai:prov:openai"),
                Telegram.CallbackButton("🏠 Ollama", "ai:prov:ollama")
            },
            new[]
            {
                Telegram.CallbackButton(isOllama ? "🏠 Ollama URL" : "🔑 Set API key", isOllama ? "ai:url" : "ai:key"),
                Telegram.CallbackButton("🎛️ Change model", "ai:model"),
                Telegram.CallbackButton("📊 Daily limit", "ai:limit")
            },
            new[] { Telegram.CallbackButton("🏠 Menu", "menu:home") }
        };

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

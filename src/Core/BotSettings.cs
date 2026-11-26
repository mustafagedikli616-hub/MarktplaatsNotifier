using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarktplaatsNotifier;

public class BotSettings
{
    public string Language { get; set; } = "en";
    public bool QuietNightsEnabled { get; set; } = true;
    public int NightStartHour { get; set; } = 23;
    public int NightEndHour { get; set; } = 7;
    public bool SkipLikelyBusinesses { get; set; } = false;
    public bool SkipHighDescriptionPrice { get; set; } = true;
    public bool FastCategoryMode { get; set; } = true;
    public bool EnableTweakers { get; set; } = true;
    public bool EnableTweedehands { get; set; } = true;
    public bool EnableKleinanzeigen { get; set; } = true;
    public bool EnableCoolblue { get; set; } = true;
    public bool EnableVinted { get; set; } = true;
    public bool MorningDigestEnabled { get; set; } = true;
    public int MorningDigestHour { get; set; } = 7;

    // AI photo + description grader settings (Gemini recommended: free tier + high limits)
    public bool AiEnabled { get; set; } = false;
    public string AiProvider { get; set; } = "gemini";
    public string AiModel { get; set; } = "gemini-2.5-flash";
    public string AiBaseUrl { get; set; } = "http://localhost:11434";
    public int AiDailyLimit { get; set; } = 30;
    public bool AiAutoSkip { get; set; } = false;

    [JsonIgnore]
    public string FilePath { get; set; } = AppFiles.PathFor("settings.json");

    [JsonIgnore]
    public bool IsDutch => Language.Equals("nl", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true
    };

    public static BotSettings Load(string? dataDir = null)
    {
        string path = Path.Combine(dataDir ?? AppFiles.DataDirectory, "settings.json");
        if (!File.Exists(path))
        {
            BotSettings defaults = new BotSettings { FilePath = path };
            string? envProvider = Environment.GetEnvironmentVariable("AI_PROVIDER");
            if (!string.IsNullOrWhiteSpace(envProvider))
            {
                defaults.AiProvider = envProvider.Trim().ToLowerInvariant();
                defaults.AiModel = AiGrader.DefaultModelFor(defaults.AiProvider);
                defaults.AiEnabled = true;
            }

            string? envModel = Environment.GetEnvironmentVariable("AI_MODEL");
            if (!string.IsNullOrWhiteSpace(envModel))
            {
                defaults.AiModel = envModel.Trim();
            }

            defaults.Save();
            return defaults;
        }

        try
        {
            string json = File.ReadAllText(path);
            BotSettings? loaded = JsonSerializer.Deserialize<BotSettings>(json);
            if (loaded != null)
            {
                loaded.FilePath = path;
                loaded.Clamp();
                return loaded;
            }
        }
        catch (Exception ex)
        {
            BotLog.Write($"Couldn't read settings.json ({ex.Message}), using defaults.");
        }

        return new BotSettings { FilePath = path };
    }

    public void ReloadFromDisk()
    {
        if (!File.Exists(FilePath))
        {
            return;
        }

        try
        {
            BotSettings? loaded = JsonSerializer.Deserialize<BotSettings>(File.ReadAllText(FilePath));
            if (loaded == null)
            {
                return;
            }

            Language = loaded.Language;
            QuietNightsEnabled = loaded.QuietNightsEnabled;
            NightStartHour = loaded.NightStartHour;
            NightEndHour = loaded.NightEndHour;
            SkipLikelyBusinesses = loaded.SkipLikelyBusinesses;
            SkipHighDescriptionPrice = loaded.SkipHighDescriptionPrice;
            FastCategoryMode = loaded.FastCategoryMode;
            EnableTweakers = loaded.EnableTweakers;
            EnableTweedehands = loaded.EnableTweedehands;
            EnableKleinanzeigen = loaded.EnableKleinanzeigen;
            EnableCoolblue = loaded.EnableCoolblue;
            EnableVinted = loaded.EnableVinted;
            MorningDigestEnabled = loaded.MorningDigestEnabled;
            MorningDigestHour = loaded.MorningDigestHour;
            AiEnabled = loaded.AiEnabled;
            AiProvider = loaded.AiProvider;
            AiModel = loaded.AiModel;
            AiBaseUrl = loaded.AiBaseUrl;
            AiDailyLimit = loaded.AiDailyLimit;
            AiAutoSkip = loaded.AiAutoSkip;
            Clamp();
        }
        catch
        {
        }
    }

    public void Save()
    {
        Clamp();
        string tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tempPath, FilePath, overwrite: true);
    }

    private void Clamp()
    {
        Language = Language.Equals("nl", StringComparison.OrdinalIgnoreCase) ? "nl" : "en";
        NightStartHour = Math.Clamp(NightStartHour, 0, 23);
        NightEndHour = Math.Clamp(NightEndHour, 0, 23);
        MorningDigestHour = Math.Clamp(MorningDigestHour, 0, 23);
        AiDailyLimit = Math.Clamp(AiDailyLimit, 1, 500);
        if (string.IsNullOrWhiteSpace(AiProvider))
        {
            AiProvider = "gemini";
        }
        if (string.IsNullOrWhiteSpace(AiModel))
        {
            AiModel = AiGrader.DefaultModelFor(AiProvider);
        }
    }
}

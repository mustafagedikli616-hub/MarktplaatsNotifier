using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace MarktplaatsNotifier;

public class UserProfileData
{
    public string ChatId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Postcode { get; set; } = "";
    public int PickupKm { get; set; } = 0;
    public bool OnboardingComplete { get; set; } = false;
    public DateTime JoinedAt { get; set; } = DateTime.Now;
}

public class SentListingRef
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Link { get; set; } = "";
    public double Price { get; set; }
    public double HighestBid { get; set; }
    public int BidCount { get; set; }
    public string SellerName { get; set; } = "";
}

public class UserSession
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

    private static readonly string[] BackupFileNames =
    {
        "searches.txt", "hints.txt", "settings.json", "blocklist.json",
        "watchlist.json", "flips.json", "stats.json", "history.txt", "profile.json"
    };

    private readonly string token;
    private readonly string seenPath;
    private readonly string profilePath;
    private readonly Queue<string> recentIdOrder = new Queue<string>();
    private readonly Queue<long> recentMessageOrder = new Queue<long>();

    public string ChatId { get; }
    public bool IsOwner { get; }
    public string Name { get; set; }
    public string DataDir { get; }
    public string Postcode { get; set; }
    public int PickupKm { get; set; }
    public bool OnboardingComplete { get; set; }
    public bool Paused { get; set; }
    public DateTime? PausedUntil { get; set; }

    public Telegram Telegram { get; }
    public SearchList SearchList { get; }
    public SearchScheduler Scheduler { get; } = new SearchScheduler();
    public BotSettings Settings { get; }
    public ListingHistory History { get; }
    public Blocklist Blocklist { get; }
    public Watchlist Watchlist { get; }
    public FlipTracker FlipTracker { get; }
    public SearchStats Stats { get; }
    public DailyDigest Digest { get; } = new DailyDigest();
    public ActivityFeed Feed { get; } = new ActivityFeed();
    public AiGrader AiGrader { get; }

    public HashSet<string> SeenIds { get; } = new HashSet<string>();
    public Dictionary<string, long> NewestIdPerSearch { get; } =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SentListingRef> RecentById { get; } =
        new Dictionary<string, SentListingRef>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<long, SentListingRef> RecentByMessageId { get; } = new Dictionary<long, SentListingRef>();

    public int ChecksDone { get; set; }
    public int ListingsSent { get; set; }
    public DateTime LastHeartbeat { get; set; } = DateTime.Now;
    public int ChecksAtLastHeartbeat { get; set; }

    public CommandHandler? Commands { get; set; }

    public UserSession(
        string chatId,
        bool isOwner,
        string name,
        string dataDir,
        string token,
        string postcode,
        int pickupKm,
        bool onboardingComplete,
        HttpClient telegramClient,
        HttpClient marktplaatsClient)
    {
        Directory.CreateDirectory(dataDir);

        ChatId = chatId;
        IsOwner = isOwner;
        Name = name;
        DataDir = dataDir;
        this.token = token;
        Postcode = postcode;
        PickupKm = pickupKm;
        OnboardingComplete = onboardingComplete;

        seenPath = Path.Combine(dataDir, "seen.txt");
        profilePath = Path.Combine(dataDir, "profile.json");

        Telegram = new Telegram(telegramClient, token, chatId);
        SearchList = new SearchList(dataDir);
        Settings = BotSettings.Load(dataDir);
        History = new ListingHistory(dataDir);
        Blocklist = new Blocklist(dataDir);
        Watchlist = new Watchlist(dataDir);
        FlipTracker = new FlipTracker(dataDir);
        Stats = new SearchStats(dataDir);
        AiGrader = new AiGrader(marktplaatsClient, dataDir);
    }

    public string LoadData()
    {
        string loadError = SearchList.Load();
        if (loadError != "")
        {
            return loadError;
        }

        History.Load();
        Blocklist.Load();
        Watchlist.Load();
        FlipTracker.Load();
        Stats.Load();

        if (File.Exists(seenPath))
        {
            string[] savedIds = File.ReadAllLines(seenPath);
            if (savedIds.Length > 50000)
            {
                savedIds = savedIds[^50000..];
                File.WriteAllLines(seenPath, savedIds);
            }

            foreach (string id in savedIds)
            {
                if (id != "")
                {
                    SeenIds.Add(id);
                }
            }
        }

        return "";
    }

    public void SaveProfile()
    {
        if (IsOwner)
        {
            string secretsPath = Path.Combine(DataDir, "secrets.txt");
            File.WriteAllLines(secretsPath, new[] { token, ChatId, Postcode, PickupKm.ToString(CultureInfo.InvariantCulture) });
            return;
        }

        UserProfileData profile = new UserProfileData
        {
            ChatId = ChatId,
            Name = Name,
            Postcode = Postcode,
            PickupKm = PickupKm,
            OnboardingComplete = OnboardingComplete
        };
        string tempPath = profilePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(profile, JsonOptions));
        File.Move(tempPath, profilePath, overwrite: true);
    }

    public bool MarkSeen(string itemId)
    {
        if (SeenIds.Contains(itemId))
        {
            return false;
        }

        SeenIds.Add(itemId);
        File.AppendAllText(seenPath, itemId + "\n");
        return true;
    }

    public void RememberSentMessage(Listing ad, long messageId)
    {
        if (ad.Id == "")
        {
            return;
        }

        SentListingRef info = new SentListingRef
        {
            Id = ad.Id,
            Title = ad.Title,
            Link = ad.Link.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? ad.Link
                : "https://www.marktplaats.nl" + ad.Link,
            Price = ad.Price,
            HighestBid = ad.HighestBid,
            BidCount = ad.BidCount,
            SellerName = ad.SellerName
        };

        RecentById[ad.Id] = info;
        recentIdOrder.Enqueue(ad.Id);
        while (recentIdOrder.Count > 500)
        {
            string oldest = recentIdOrder.Dequeue();
            RecentById.Remove(oldest);
        }

        if (messageId > 0)
        {
            RecentByMessageId[messageId] = info;
            recentMessageOrder.Enqueue(messageId);
            while (recentMessageOrder.Count > 500)
            {
                long oldestMsg = recentMessageOrder.Dequeue();
                RecentByMessageId.Remove(oldestMsg);
            }
        }
    }

    public byte[] CreateBackupZip()
    {
        History.Save();
        Stats.Save();

        using MemoryStream ms = new MemoryStream();
        using (ZipArchive zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string fileName in BackupFileNames)
            {
                string fullPath = Path.Combine(DataDir, fileName);
                if (!File.Exists(fullPath))
                {
                    continue;
                }

                ZipArchiveEntry entry = zip.CreateEntry(fileName, CompressionLevel.Optimal);
                using Stream entryStream = entry.Open();
                using FileStream fs = File.OpenRead(fullPath);
                fs.CopyTo(entryStream);
            }
        }

        return ms.ToArray();
    }

    public bool RestoreFromBackupZip(byte[] zipBytes, out List<string> restoredFiles, out string error)
    {
        restoredFiles = new List<string>();
        error = "";

        HashSet<string> allowed = new HashSet<string>(BackupFileNames, StringComparer.OrdinalIgnoreCase);
        allowed.Remove("profile.json");

        try
        {
            using MemoryStream ms = new MemoryStream(zipBytes);
            using ZipArchive zip = new ZipArchive(ms, ZipArchiveMode.Read);

            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string fileName = Path.GetFileName(entry.FullName);
                if (fileName == "" || !allowed.Contains(fileName))
                {
                    continue;
                }

                // Never allow secrets or arbitrary paths
                if (fileName.Equals("secrets.txt", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("ai_key.txt", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string targetPath = Path.Combine(DataDir, fileName);
                string tempPath = targetPath + ".restore.tmp";
                using (Stream src = entry.Open())
                using (FileStream dst = File.Create(tempPath))
                {
                    src.CopyTo(dst);
                }

                File.Move(tempPath, targetPath, overwrite: true);
                restoredFiles.Add(fileName);
            }

            if (restoredFiles.Count == 0)
            {
                error = "No recognized Marktplaats Notifier files were found inside that .zip.";
                return false;
            }

            string searchErr = SearchList.Load();
            if (searchErr != "")
            {
                error = "Restored files, but searches.txt had an error: " + searchErr;
                return false;
            }

            Settings.ReloadFromDisk();
            Blocklist.Load();
            Watchlist.Load();
            FlipTracker.Load();
            Stats.Load();
            History.Load();
            return true;
        }
        catch (Exception ex)
        {
            error = $"Couldn't read that .zip file: {ex.Message}";
            return false;
        }
    }

    public void SaveAllState()
    {
        try
        {
            History.Save();
            Stats.Save();
        }
        catch
        {
        }
    }
}

using System.Security.Cryptography;
using System.Text.Json;

namespace MarktplaatsNotifier;

public class UserManager
{
    private readonly string usersRootDir;
    private readonly string token;
    private readonly HttpClient telegramClient;
    private readonly HttpClient marktplaatsClient;
    private readonly Action<UserSession, UserManager> wireSession;

    private readonly Dictionary<string, UserSession> sessions =
        new Dictionary<string, UserSession>(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> inviteCodes =
        new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

    public UserSession Owner { get; }
    public IReadOnlyCollection<UserSession> AllUsers => sessions.Values;

    public UserManager(
        UserSession owner,
        string token,
        HttpClient telegramClient,
        HttpClient marktplaatsClient,
        Action<UserSession, UserManager> wireSession)
    {
        Owner = owner;
        this.token = token;
        this.telegramClient = telegramClient;
        this.marktplaatsClient = marktplaatsClient;
        this.wireSession = wireSession;
        usersRootDir = Path.Combine(AppFiles.DataDirectory, "users");

        sessions[owner.ChatId] = owner;
        wireSession(owner, this);
    }

    public void LoadInvitedUsers()
    {
        if (!Directory.Exists(usersRootDir))
        {
            return;
        }

        foreach (string userDir in Directory.GetDirectories(usersRootDir))
        {
            string profilePath = Path.Combine(userDir, "profile.json");
            if (!File.Exists(profilePath))
            {
                continue;
            }

            try
            {
                UserProfileData? profile = JsonSerializer.Deserialize<UserProfileData>(File.ReadAllText(profilePath));
                if (profile == null || string.IsNullOrWhiteSpace(profile.ChatId) || sessions.ContainsKey(profile.ChatId))
                {
                    continue;
                }

                UserSession session = new UserSession(
                    profile.ChatId,
                    isOwner: false,
                    name: profile.Name == "" ? profile.ChatId : profile.Name,
                    dataDir: userDir,
                    token: token,
                    postcode: profile.Postcode,
                    pickupKm: profile.PickupKm,
                    onboardingComplete: profile.OnboardingComplete,
                    telegramClient: telegramClient,
                    marktplaatsClient: marktplaatsClient);

                string err = session.LoadData();
                if (err != "")
                {
                    BotLog.Write($"Couldn't load user {profile.ChatId}: {err}");
                    continue;
                }

                sessions[session.ChatId] = session;
                wireSession(session, this);
                BotLog.Write($"Loaded user '{session.Name}' ({session.SearchList.Rules.Count} searches).");
            }
            catch (Exception ex)
            {
                BotLog.Write($"Error loading user in {userDir}: {ex.Message}");
            }
        }
    }

    public UserSession? GetByChatId(string chatId) => sessions.GetValueOrDefault(chatId);

    public string CreateInviteCode()
    {
        PruneExpiredInvites();
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        char[] buf = new char[6];
        for (int i = 0; i < buf.Length; i++)
        {
            buf[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        }

        string code = new string(buf);
        inviteCodes[code] = DateTime.Now.AddHours(24);
        return code;
    }

    public bool TryRedeemInvite(string code, string chatId, string displayName, out UserSession? created, out string message)
    {
        created = null;
        PruneExpiredInvites();

        if (sessions.ContainsKey(chatId))
        {
            message = "✅ You're already connected to this bot! Send /help to see commands.";
            return false;
        }

        string cleanCode = code.Trim().ToUpperInvariant();
        if (!inviteCodes.Remove(cleanCode))
        {
            message = "❌ Invalid or expired invite code. Ask the bot owner for a fresh /invite code.";
            return false;
        }

        string userDir = Path.Combine(usersRootDir, chatId);
        Directory.CreateDirectory(userDir);

        UserSession session = new UserSession(
            chatId,
            isOwner: false,
            name: string.IsNullOrWhiteSpace(displayName) ? chatId : displayName.Trim(),
            dataDir: userDir,
            token: token,
            postcode: "",
            pickupKm: 0,
            onboardingComplete: false,
            telegramClient: telegramClient,
            marktplaatsClient: marktplaatsClient);

        session.LoadData();
        session.SaveProfile();
        sessions[chatId] = session;
        wireSession(session, this);

        created = session;
        message = "";
        BotLog.Write($"New user joined: {session.Name} ({chatId}).");
        return true;
    }

    public bool RemoveUser(string targetChatId)
    {
        if (targetChatId == Owner.ChatId || !sessions.TryGetValue(targetChatId, out UserSession? session))
        {
            return false;
        }

        session.SaveAllState();
        sessions.Remove(targetChatId);

        try
        {
            if (Directory.Exists(session.DataDir))
            {
                Directory.Delete(session.DataDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            BotLog.Write($"Removed user {targetChatId} in memory, but couldn't delete folder: {ex.Message}");
        }

        BotLog.Write($"Removed user: {session.Name} ({targetChatId}).");
        return true;
    }

    public void SaveAllUsers()
    {
        foreach (UserSession session in sessions.Values)
        {
            session.SaveAllState();
        }
    }

    private void PruneExpiredInvites()
    {
        DateTime now = DateTime.Now;
        List<string> expired = inviteCodes.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList();
        foreach (string code in expired)
        {
            inviteCodes.Remove(code);
        }
    }
}

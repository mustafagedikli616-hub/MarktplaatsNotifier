using System.Net;
using System.Text;

namespace MarktplaatsNotifier;

public class SentRecord
{
    public string Title { get; set; } = "";
    public string PriceText { get; set; } = "";
    public string Link { get; set; } = "";
    public string MatchedSearch { get; set; } = "";
    public string Alert { get; set; } = "";
    public bool WasSilent { get; set; }
    public DateTime SentAt { get; set; }
}

public class DailyDigest
{
    private readonly object syncLock = new object();
    private DateOnly currentDate = DateOnly.FromDateTime(DateTime.Now);
    private DateOnly lastDigestDate = DateOnly.MinValue;
    private readonly List<SentRecord> todaySent = new List<SentRecord>();
    private readonly List<SentRecord> overnightSilent = new List<SentRecord>();

    public int ChecksToday { get { lock (syncLock) { RolloverIfNewDayUnlocked(); return checksToday; } } }
    public int FilteredOutToday { get { lock (syncLock) { RolloverIfNewDayUnlocked(); return filteredOutToday; } } }
    public int SentTodayCount { get { lock (syncLock) { RolloverIfNewDayUnlocked(); return todaySent.Count; } } }

    private int checksToday;
    private int filteredOutToday;

    public void RecordCheck()
    {
        lock (syncLock) { RolloverIfNewDayUnlocked(); checksToday++; }
    }

    public void RecordFilteredOut()
    {
        lock (syncLock) { RolloverIfNewDayUnlocked(); filteredOutToday++; }
    }

    public void RecordSent(Listing ad, string alert, bool wasSilent)
    {
        lock (syncLock)
        {
            RolloverIfNewDayUnlocked();
            SentRecord record = new SentRecord
            {
                Title = ad.Title,
                PriceText = ad.PriceText,
                Link = ad.Link,
                MatchedSearch = ad.MatchedSearch,
                Alert = alert,
                WasSilent = wasSilent,
                SentAt = DateTime.Now
            };

            todaySent.Add(record);
            if (wasSilent)
            {
                overnightSilent.Add(record);
            }
        }
    }

    public string? TryBuildMorningDigestHtml(BotSettings settings)
    {
        if (!settings.MorningDigestEnabled)
        {
            return null;
        }

        lock (syncLock)
        {
            DateTime now = DateTime.Now;
            DateOnly today = DateOnly.FromDateTime(now);
            if (lastDigestDate == today || now.Hour < settings.MorningDigestHour)
            {
                return null;
            }

            lastDigestDate = today;
            if (overnightSilent.Count == 0)
            {
                return null;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"☀️ <b>Overnight listings</b> ({overnightSilent.Count}):\n");

            int shown = Math.Min(overnightSilent.Count, 20);
            for (int i = 0; i < shown; i++)
            {
                SentRecord item = overnightSilent[i];
                string url = BuildUrl(item.Link);
                sb.AppendLine(
                    $"• <a href=\"{Encode(url)}\">{Encode(item.Title)}</a> — {Encode(item.PriceText)} ({Encode(item.MatchedSearch)}, {item.SentAt:HH:mm})");
            }

            if (overnightSilent.Count > shown)
            {
                sb.AppendLine($"\n…and {overnightSilent.Count - shown} more.");
            }

            overnightSilent.Clear();
            return sb.ToString().TrimEnd();
        }
    }

    public string BuildTodaySummaryHtml()
    {
        lock (syncLock)
        {
            RolloverIfNewDayUnlocked();

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"📊 <b>Today</b> ({currentDate:d MMM})");
            sb.AppendLine($"🔎 {checksToday} checks · 📨 {todaySent.Count} sent · 🧹 {filteredOutToday} filtered out");

            if (todaySent.Count == 0)
            {
                sb.Append("\nNo listings sent yet today.");
                return sb.ToString();
            }

            sb.AppendLine("\n<b>Latest listings:</b>");
            int start = Math.Max(0, todaySent.Count - 15);
            for (int i = todaySent.Count - 1; i >= start; i--)
            {
                SentRecord item = todaySent[i];
                string badge = item.Alert != "" ? $"[{Encode(item.Alert)}] " : (item.WasSilent ? "🌙 " : "");
                string url = BuildUrl(item.Link);
                sb.AppendLine(
                    $"• {badge}<a href=\"{Encode(url)}\">{Encode(item.Title)}</a> — {Encode(item.PriceText)} ({item.SentAt:HH:mm})");
            }

            return sb.ToString().TrimEnd();
        }
    }

    private void RolloverIfNewDayUnlocked()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        if (today != currentDate)
        {
            currentDate = today;
            todaySent.Clear();
            checksToday = 0;
            filteredOutToday = 0;
        }
    }

    private static string BuildUrl(string link)
    {
        return link.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? link
            : "https://www.marktplaats.nl" + link;
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

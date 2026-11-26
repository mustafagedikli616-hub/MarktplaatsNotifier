using System.Globalization;
using System.Text.RegularExpressions;

namespace MarktplaatsNotifier;

public static class Filters
{
    private static readonly char[] ClauseSeparators = { '.', ',', '!', '?', '\n', '\r', ';', ':' };

    // Matches a whole-word negation right before a fault word, e.g.:
    // "geen barst", "zonder waterschade", "geen enkele strepen", "geen krassen of barsten".
    private static readonly Regex NegationBeforeWord = new Regex(
        @"\b(?:geen|zonder)\s+(?:(?:enkele|enige|zichtbare|grote|diepe)\s+|[\p{L}\d]+\s+of\s+)?$",
        RegexOptions.Compiled);

    private static readonly Regex[] AskingPricePatterns =
    {
        new Regex(@"\b(?:vraagprijs|richtprijs|minimaal|mag weg voor|vaste prijs)\s*:?\s*€?\s*(\d{2,5})(?:[.,](\d{2}))?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"€\s*(\d{2,5})(?:[.,](\d{2}))?\b", RegexOptions.Compiled),
        new Regex(@"\b(\d{2,5})(?:[.,](\d{2}))?\s*(?:euro|eur)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)
    };

    private static readonly string[] RetailPriceMarkers =
    {
        "nieuwprijs", "winkelprijs", "kost nieuw", "nieuw kost", "adviesprijs", "nieuwwaarde", "betaald"
    };

    public static string FindBadFault(string text, string[] skipWords)
    {
        if (skipWords.Length == 0 || string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        string[] clauses = text.ToLowerInvariant().Split(ClauseSeparators, StringSplitOptions.RemoveEmptyEntries);

        foreach (string clause in clauses)
        {
            foreach (string rawWord in skipWords)
            {
                string word = rawWord.Trim();
                if (word == "")
                {
                    continue;
                }

                int index = clause.IndexOf(word, StringComparison.Ordinal);
                while (index >= 0)
                {
                    string before = clause.Substring(0, index);
                    if (!NegationBeforeWord.IsMatch(before))
                    {
                        return word;
                    }

                    index = clause.IndexOf(word, index + 1, StringComparison.Ordinal);
                }
            }
        }

        return "";
    }

    // Detects an asking price mentioned inside a listing description (e.g. "Vraagprijs €220" or "180 euro"),
    // ignoring retail reference prices like "nieuwprijs €500".
    public static double ExtractMentionedPrice(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return 0;
        }

        string lower = description.ToLowerInvariant();
        foreach (Regex pattern in AskingPricePatterns)
        {
            foreach (Match match in pattern.Matches(lower))
            {
                int contextStart = Math.Max(0, match.Index - 28);
                string before = lower.Substring(contextStart, match.Index - contextStart);
                bool isRetailContext = false;
                foreach (string marker in RetailPriceMarkers)
                {
                    if (before.Contains(marker))
                    {
                        isRetailContext = true;
                        break;
                    }
                }

                if (isRetailContext)
                {
                    continue;
                }

                if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int euros))
                {
                    continue;
                }

                if (euros < 5 || euros > 50000)
                {
                    continue;
                }

                double amount = euros;
                if (match.Groups.Count > 2 && match.Groups[2].Success &&
                    int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cents))
                {
                    amount += cents / 100.0;
                }

                return amount;
            }
        }

        return 0;
    }

    public static bool IsNight(BotSettings settings)
    {
        if (!settings.QuietNightsEnabled || settings.NightStartHour == settings.NightEndHour)
        {
            return false;
        }

        int hour = DateTime.Now.Hour;
        if (settings.NightStartHour > settings.NightEndHour)
        {
            return hour >= settings.NightStartHour || hour < settings.NightEndHour;
        }

        return hour >= settings.NightStartHour && hour < settings.NightEndHour;
    }

    public static bool IsSuperDeal(Listing ad, int maxPrice)
    {
        if (ad.PriceType != "FIXED")
        {
            return false;
        }

        return ad.Price > 0 && ad.Price <= maxPrice / 2.0;
    }

    private static readonly char[] WordSeparators =
    {
        ' ', '.', ',', '!', '?', '\n', '\r', '(', ')', '/', '-', ':', ';', '"', '\'', '+', '&'
    };

    // Matches a search query locally against a listing (used for Category Firehose & Tweakers V&A).
    // Every search word must appear as the start of a word (so "ram" doesn't match "frame",
    // while "monitor" matches "monitoren"). Model numbers with digits also match squashed ("rtx3070" <-> "rtx 3070").
    public static bool MatchesSearch(string searchText, Listing ad)
    {
        string[] searchWords = searchText.ToLowerInvariant().Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (searchWords.Length == 0)
        {
            return false;
        }

        string[] textWords = (ad.Title + " " + ad.Description)
            .ToLowerInvariant()
            .Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        string squashedText = string.Concat(textWords);

        foreach (string searchWord in searchWords)
        {
            bool found = false;
            foreach (string textWord in textWords)
            {
                if (textWord.StartsWith(searchWord, StringComparison.Ordinal) ||
                    (textWord.Any(char.IsDigit) && textWord.Contains(searchWord, StringComparison.Ordinal)))
                {
                    found = true;
                    break;
                }
            }

            if (!found && searchWord.Any(char.IsDigit) && squashedText.Contains(searchWord, StringComparison.Ordinal))
            {
                found = true;
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsNotForSale(string title)
    {
        string lowerTitle = title.ToLowerInvariant();
        return lowerTitle.Contains("gezocht") ||
               lowerTitle.Contains("reparatie") ||
               lowerTitle.Contains("inkoop");
    }
}

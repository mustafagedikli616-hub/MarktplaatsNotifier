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

    private static readonly string[] FaultWords =
    {
        "defect", "defecte", "kapot", "kapotte", "is stuk", "gaat stuk", "werkt niet", "niet werkend",
        "doet het niet", "doet niks", "doet niets", "doet helemaal niks",
        "geen beeld", "geen stroom", "geen signaal", "geen geluid", "leest geen",
        "valt uit", "slaat af", "gaat niet aan", "gaat niet meer aan", "niet aangaat", "niet meer aangaat",
        "niet meer aan", "start niet", "laadt niet", "niet meer goed", "werkt niet goed", "werkt niet altijd",
        "werkt half", "half werkend", "beperkt werkend", "werkt soms", "hapert", "loopt vast", "blijft hangen",
        "vastloper", "herstart steeds", "herstart vanzelf", "blijft herstarten", "geen wifi", "geen bluetooth",
        "herrie", "lawaai", "oververhit", "oververhitting", "wordt heet", "wordt erg warm",
        "zwart scherm", "blauw scherm", "bluescreen", "knippert", "flikkert", "strepen", "piept", "bootloop",
        "dode pixel", "dode pixels", "pixelfout",
        "barst", "barstje", "barsten", "gebarsten", "gebroken", "kapotgevallen", "gevallen", "schermschade",
        "scherm beschadigd", "beschadigd scherm", "stickdrift", "stick drift", "drift", "vermoedelijk",
        "fan kapot", "ventilator kapot",
        "voor onderdelen", "als onderdelen", "voor parts", "for parts", "reparatie nodig", "te repareren", "klusser",
        "handige harry", "handig is", "knutselaar", "zoals hij is", "zoals deze is", "zoals het is", "as is",
        "zonder adapter", "zonder voet", "zonder voeding", "zonder ssd", "zonder harde schijf", "zonder oplader",
        "broken", "not working", "faulty"
    };

    private static readonly string[] NotWords = { "geen ", "zonder ", "niet ", "nooit ", "geen enkele ", "zonder enige " };

    public static string FindFaultWord(Listing ad)
    {
        if (ad.Condition.Equals("niet werkend", StringComparison.OrdinalIgnoreCase))
        {
            return "niet werkend";
        }

        string[] words = (ad.Title + " " + ad.Description).ToLowerInvariant()
            .Split(new char[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        string text = string.Join(" ", words);

        foreach (string faultWord in FaultWords)
        {
            if (HasWholeWord(text, faultWord))
            {
                return faultWord;
            }
        }

        return "";
    }

    private static bool HasWholeWord(string text, string word)
    {
        int index = text.IndexOf(word, StringComparison.Ordinal);
        while (index >= 0)
        {
            int end = index + word.Length;
            bool startOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            bool endOk = end == text.Length || !char.IsLetterOrDigit(text[end]);

            string before = text.Substring(0, index);
            bool negated = NegationBeforeWord.IsMatch(before);
            if (!negated)
            {
                foreach (string notWord in NotWords)
                {
                    if (before.EndsWith(notWord, StringComparison.Ordinal))
                    {
                        negated = true;
                        break;
                    }
                }
            }

            if (startOk && endOk && !negated)
            {
                return true;
            }

            index = text.IndexOf(word, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static readonly string[] ComputerSearchMarkers =
    {
        "pc", "laptop", "desktop", "thinkpad", "optiplex", "elitedesk", "prodesk", "tiny", "micro", "nuc", "macbook"
    };

    // Catches 1st-5th gen Intel Core (e.g. i5-4570, i7 3770, i5 750) plus ancient Intel/AMD CPUs
    private static readonly Regex OldIntelGenRegex = new Regex(
        @"\bi[357][\s\-]?(?:[2345]\d{3}[a-z]{0,2}|[56789]\d{2}[a-z]?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OldCpuNamedRegex = new Regex(
        @"\b(?:core\s*2\s*(?:duo|quad)|pentium(?:\s+(?:d|4|dual|g\d{3,4}))?|celeron|intel\s+atom|amd\s+fx[\s\-]?\d{4}|fx[\s\-]?[468]\d{3}|athlon\s*(?:ii|64|x[24])|phenom|sempron|a[468][\s\-]\d{4}|a10[\s\-]\d{4})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string FindOldCpu(Listing ad, string searchQuery)
    {
        string qLower = searchQuery.ToLowerInvariant();
        string combined = (ad.Title + " " + ad.Description).ToLowerInvariant();

        bool isComputerSearch = false;
        foreach (string marker in ComputerSearchMarkers)
        {
            if (HasWholeWord(qLower, marker))
            {
                isComputerSearch = true;
                break;
            }
        }

        if (!isComputerSearch)
        {
            return "";
        }

        Match genMatch = OldIntelGenRegex.Match(combined);
        if (genMatch.Success && !qLower.Contains(genMatch.Value, StringComparison.OrdinalIgnoreCase))
        {
            return genMatch.Value.Trim();
        }

        Match namedMatch = OldCpuNamedRegex.Match(combined);
        if (namedMatch.Success && !qLower.Contains(namedMatch.Value, StringComparison.OrdinalIgnoreCase))
        {
            return namedMatch.Value.Trim();
        }

        return "";
    }

    private static readonly string[] ScamPhrases =
    {
        "tikkie vooruit", "vooraf betalen", "overschrijven vooraf", "alleen overschrijven",
        "geen kopersbescherming", "buiten marktplaats", "stuur een whatsapp", "via whatsapp",
        "mail mij op", "western union", "giftcard", "cadeaukaart", "crypto"
    };

    public static string FindScamSigns(Listing ad)
    {
        if (ad.Source.Equals("Coolblue Tweedekans", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        List<string> signs = new List<string>();
        double effectivePrice = ad.Price > 0 ? ad.Price : ad.HighestBid;
        string lowerDesc = (ad.Title + " " + ad.Description).ToLowerInvariant();

        if (ad.SellerYears == 0 && ad.SellerReviewCount <= 0 && !ad.SellerVerified && effectivePrice >= 50)
        {
            signs.Add(ad.CanShip ? "0 yr / 0 reviews / unverified (shipping)" : "0 yr / 0 reviews / unverified");
        }

        foreach (string phrase in ScamPhrases)
        {
            if (lowerDesc.Contains(phrase, StringComparison.Ordinal))
            {
                signs.Add($"mentions \"{phrase}\"");
                break;
            }
        }

        if (ad.PhotoCount == 1 && effectivePrice >= 100 && ad.SellerYears == 0 && !ad.SellerVerified &&
            ad.TypicalPrice > 0 && effectivePrice <= ad.TypicalPrice * 0.40)
        {
            signs.Add("1 photo & suspiciously cheap");
        }

        return string.Join(" · ", signs);
    }
}

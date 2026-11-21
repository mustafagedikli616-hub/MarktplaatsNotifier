

public static class Filters
{
   
    
    public static string FindBadFault(string text,string[] skipWords)
    {
        string[] pieces = text.ToLower().Split('.', ',', '!', '?', '\n');

        foreach (string piece in pieces)
        {
            if (piece.Contains("geen") || piece.Contains("zonder"))
            {
                continue;
            }

            foreach (string word in skipWords)
            {
                if (piece.Contains(word))
                {
                    return word;
                }
            }
        }

        return "";
    }
    public static bool IsNight()
    {
        int hour = DateTime.Now.Hour;
        return hour >= 23 || hour < 7;
    }
    
    public static bool IsSuperDeal(Listing ad, int maxPrice)
    {
        if (ad.PriceType != "FIXED")
        {
            return false;
        }

        return ad.Price > 0 && ad.Price <= maxPrice / 2.0;
    }

    public static bool IsNotForSale(string title)
    {
        string lowerTitle = title.ToLower();
        if (lowerTitle.Contains("gezocht") || lowerTitle.Contains("reparatie") || lowerTitle.Contains("inkoop"))
        {
            return true;
        }

        return false;


    }
    private static char[] separators = { ' ', '.', ',', '!', '?', '\n', '\r', '(', ')', '/', '-', ':', ';', '"', '\'', '+', '&' };

    public static bool MatchesSearch(string searchText, Listing ad)
    {
        string[] textWords = (ad.Title + " " + ad.Description).ToLower().Split(separators, StringSplitOptions.RemoveEmptyEntries);
        string[] searchWords = searchText.ToLower().Split(separators, StringSplitOptions.RemoveEmptyEntries);
        string squashedText = string.Concat(textWords);

        foreach (string searchWord in searchWords)
        {
            bool found = false;
            foreach (string textWord in textWords)
            {
                if (textWord.StartsWith(searchWord) || (textWord.Any(char.IsDigit) && textWord.Contains(searchWord)))
                {
                    found = true;
                    break;
                }
            }

            if (!found && searchWord.Any(char.IsDigit) && squashedText.Contains(searchWord))
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
        private static string[] faultWords =
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

    private static string[] notWords = { "geen ", "zonder ", "niet ", "nooit ", "geen enkele ", "zonder enige " };

    
    public static string FindFaultWord(Listing ad)
    {
        if (ad.Condition.ToLower() == "niet werkend")
        {
            return "niet werkend";
        }

        string[] words = (ad.Title + " " + ad.Description).ToLower()
            .Split(new char[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        string text = string.Join(" ", words);

        foreach (string faultWord in faultWords)
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
        int index = text.IndexOf(word);
        while (index >= 0)
        {
            int end = index + word.Length;
            bool startOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            bool endOk = end == text.Length || !char.IsLetterOrDigit(text[end]);

            string before = text.Substring(0, index);
            bool negated = false;
            foreach (string notWord in notWords)
            {
                if (before.EndsWith(notWord))
                {
                    negated = true;
                }
            }

            if (startOk && endOk && !negated)
            {
                return true;
            }

            index = text.IndexOf(word, index + 1);
        }

        return false;
    }
}
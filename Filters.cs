using System.Text.RegularExpressions;


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
    public static bool IsEvening()
    {
        int hour = DateTime.Now.Hour;
        return hour >= 17 && hour < 23;
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
    private static string[] oldCpuWords =
    {
        "celeron", "pentium", "atom", "core 2 duo", "core2duo", "core 2 quad"
    };

    private static Regex oldIntelCpu = new Regex(@"\bi[3579][- ]?[2-6]\d{3}[a-z]{0,2}\b");
    private static Regex oldAmdCpu = new Regex(@"\b(a4|a6|a8|a9|a10|a12|e1|e2)[- ]\d{4}");

    public static string FindOldCpu(Listing ad)
    {
        string text = (ad.Title + " " + ad.Description).ToLower();

        foreach (string word in oldCpuWords)
        {
            if (text.Contains(word))
            {
                return word;
            }
        }

        Match intel = oldIntelCpu.Match(text);
        if (intel.Success)
        {
            return intel.Value;
        }

        Match amd = oldAmdCpu.Match(text);
        if (amd.Success)
        {
            return amd.Value;
        }

        return "";
    }
    private static string[] payOutsideWords =
    {
        "tikkie", "betaalverzoek", "overmaken", "bankoverschrijving", "vooruitbetalen", "vooruit betalen",
        "alleen ideal", "niet via marktplaats", "buiten marktplaats", "paypal vrienden", "paypal friends"
    };

    private static string[] contactOutsideWords = { "whatsapp", "telegram", "bel me", "app me", "sms me" };

    public static string FindScamSigns(Listing ad, bool defectSearch)
    {
        string text = (ad.Title + " " + ad.Description).ToLower();
        List<string> strong = new List<string>();
        List<string> weak = new List<string>();

        foreach (string word in payOutsideWords)
        {
            if (text.Contains(word))
            {
                strong.Add($"wants payment outside Marktplaats (\"{word}\")");
                break;
            }
        }

        foreach (string word in contactOutsideWords)
        {
            if (text.Contains(word))
            {
                weak.Add($"wants contact outside the chat (\"{word}\")");
                break;
            }
        }

        if (ad.SellerYears == 0)
        {
            weak.Add("new account");
        }

        if (ad.PhotoCount <= 1)
        {
            weak.Add("only 1 photo");
        }

        bool looksWorking = !defectSearch && ad.FaultReason == "";
        if (looksWorking && ad.Price > 0 && ad.TypicalPrice > 0 && ad.Price <= ad.TypicalPrice * 0.4)
        {
            weak.Add("working item, way too cheap");
        }

        if (strong.Count > 0 || weak.Count >= 2)
        {
            strong.AddRange(weak);
            return string.Join(" · ", strong);
        }

        return "";
    }
}
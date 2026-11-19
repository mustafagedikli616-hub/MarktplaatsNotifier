

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
    
}
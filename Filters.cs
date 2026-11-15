

public static class Filters
{
    private static string[] skipWords = { "gebarsten", "barst", "strepen", "lijnen", "waterschade" };
    
    public static string FindBadFault(string text)
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

    public static bool IsNotForSale(string title)
    {
        string lowerTitle = title.ToLower();
        if (lowerTitle.Contains("gezocht") || lowerTitle.Contains("reparatie") || lowerTitle.Contains("inkoop"))
        {
            return true;
        }

        return false;
    }
}
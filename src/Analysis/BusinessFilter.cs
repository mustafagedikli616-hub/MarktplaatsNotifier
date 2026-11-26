using System.Globalization;
using System.Text.RegularExpressions;

namespace MarktplaatsNotifier;

public static class BusinessFilter
{
    private static readonly string[] NameWords =
    {
        "b.v.", "shop", "store", "computers", "trading", "outlet", "refurb", ".nl", ".com", "|"
    };

    private static readonly Regex StandaloneBv = new Regex(@"\bbv\b", RegexOptions.Compiled);

    private static readonly string[] TextWords =
    {
        "factuur", "btw", "kvk", "webshop", "showroom", "op voorraad",
        "maanden garantie", "jaar garantie", "inruil mogelijk"
    };

    public static int Score(Listing ad)
    {
        int score = 0;

        // 1. Non-round price with cents (e.g. €99.95)
        long cents = (long)Math.Round(ad.Price * 100) % 100;
        if (ad.Price > 0 && cents != 0)
        {
            score++;
        }

        // 2. The listing links to a website
        if (ad.HasWebsiteLink)
        {
            score++;
        }

        // 3. Title starts with an emoji or symbol
        string title = ad.Title.Trim();
        if (title.Length > 0 && (char.IsSurrogate(title[0]) || char.GetUnicodeCategory(title[0]) == UnicodeCategory.OtherSymbol))
        {
            score++;
        }

        // 4. Business-like seller name
        string name = ad.SellerName.ToLowerInvariant();
        bool businessName = StandaloneBv.IsMatch(name);
        if (!businessName)
        {
            foreach (string word in NameWords)
            {
                if (name.Contains(word))
                {
                    businessName = true;
                    break;
                }
            }
        }

        if (businessName)
        {
            score++;
        }

        // 5. Business-like terms in title or description
        string text = (ad.Title + " " + ad.Description).ToLowerInvariant();
        foreach (string word in TextWords)
        {
            if (text.Contains(word))
            {
                score++;
                break;
            }
        }

        return score;
    }
}

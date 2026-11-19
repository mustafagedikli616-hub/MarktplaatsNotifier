using System.Globalization;
namespace MarktplaatsNotifier;

public static class BusinessFilter
{
    private static string[] nameWords = { " bv", "b.v.", "shop", "store", "computers", "trading", "outlet", "refurb", ".nl", ".com", "|" };
    private static string[] textWords = { "factuur", "btw", "kvk", "webshop", "showroom", "op voorraad", "maanden garantie", "jaar garantie", "inruil mogelijk" };


    public static int Score(Listing ad)
    {
        int score = 0;

       
        int cents = (int)Math.Round(ad.Price * 100) % 100;
        if (ad.Price > 0 && cents != 0)
        {
            score++;
        }

        // 2. The listing links to a website
        if (ad.HasWebsiteLink)
        {
            score++;
        }

        
        string title = ad.Title.Trim();
        if (title.Length > 0 && (char.IsSurrogate(title[0]) || char.GetUnicodeCategory(title[0]) == UnicodeCategory.OtherSymbol))
        {
            score++;
        }

       
        string name = " " + ad.SellerName.ToLower();
        foreach (string word in nameWords)
        {
            if (name.Contains(word))
            {
                score++;
                break;
            }
        }

      
        string text = (ad.Title + " " + ad.Description).ToLower();
        foreach (string word in textWords)
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
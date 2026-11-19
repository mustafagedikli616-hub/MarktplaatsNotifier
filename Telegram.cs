using System.Text.Json;

public class Telegram
{
    private HttpClient client;
    private string token;
    private string chatId;


    public Telegram(HttpClient client, string token, string chatId)
    {
        this.client = client;
        this.token = token;
        this.chatId = chatId;
    }

    public async Task SendText(string text, string keyboard = "")
    {
        string url =
            $"https://api.telegram.org/bot{token}/sendMessage?chat_id={chatId}&text={Uri.EscapeDataString(text)}";
        if (keyboard != "")
        {
            url += $"&reply_markup={Uri.EscapeDataString(keyboard)}";
        }

        await client.GetStringAsync(url);
    }

    public async Task SendListing(Listing ad, bool night, string alert)
    {
        string place = ad.City;
        if (ad.DistanceKm >= 0)
        {
            place += $" ({ad.DistanceKm} km)";
        }
        


        string safeTitle = System.Net.WebUtility.HtmlEncode(ad.Title);
        string message = $"<b>{safeTitle}</b>\n";

        message += $"💶 {ad.PriceText}";
        if (ad.Condition != "") { message += $" · 🏷️ {ad.Condition}"; }
        message += "\n";
        if (ad.OldPrice > 0) { message += $"📉 Price drop: €{ad.OldPrice} → €{ad.Price}\n"; }

        message += $"📍 {place}";
        if (ad.Delivery != "") { message += $" · 📦 {ad.Delivery}"; }
        message += "\n";

        if (ad.PostedAt != DateTime.MinValue)
        {
            int minutes = (int)(DateTime.Now - ad.PostedAt).TotalMinutes;
            if (minutes < 60) { message += $"🕒 online {minutes} min"; }
            else { message += $"🕒 online {minutes / 60} h"; }
        }
        else
        {
            message += $"📅 {ad.Date}";
        }
        if (ad.Views >= 0) { message += $" · 👀 {ad.Views} · ❤️ {ad.Saves}"; }
        message += "\n";

        if (ad.BidCount > 0) { message += $"💰 {ad.BidCount} bids, highest €{ad.HighestBid}\n"; }

        message += $"👤 {System.Net.WebUtility.HtmlEncode(ad.SellerName)}";
        if (ad.SellerVerified) { message += " ✓"; }
        if (ad.SellerYears >= 0) { message += $" · {ad.SellerYears} yr on Marktplaats"; }
        if (ad.SellerType == "CONSUMER") { message += " · private"; }
        else if (ad.SellerType != "") { message += " · business"; }
        message += $" · 📸 {ad.PhotoCount}\n";

        if (ad.Description != "")
        {
            string snippet = ad.Description;
            if (snippet.Length > 150) { snippet = snippet.Substring(0, 150) + "..."; }
            message += $"📝 <i>{System.Net.WebUtility.HtmlEncode(snippet)}</i>\n";
        }
        if (ad.Hint != "")
        {
            message += $"{System.Net.WebUtility.HtmlEncode(ad.Hint)}\n";
            if (ad.HintQuestion != "")
            {
                message += $"💬 Ask: <code>{System.Net.WebUtility.HtmlEncode(ad.HintQuestion)}</code>\n";
            }
        }
        message += $"🔍 via: {System.Net.WebUtility.HtmlEncode(ad.MatchedSearch)}\n";
        if (ad.IsBumped) { message += "🔁 Dagtopper (paid to the top, may be an old listing)\n"; }
        if (ad.LikelyBusiness) { message += "🏢 Probably a business\n"; }

        message += $"\n🔗 <a href=\"https://www.marktplaats.nl{ad.Link}\">Bekijk advertentie</a>";
        if (alert != "")
        {
            message = $"🚨 <b>{alert}</b> 🚨\n\n" + message;
        }
        
        string encoded = Uri.EscapeDataString(message);
        string silent = "";
        if (night && alert == "")
        {
            silent = "&disable_notification=true";
        }

        if (ad.Photo != "")
        {
            try
            {
                string encodedPhoto = Uri.EscapeDataString(ad.Photo);
                await client.GetStringAsync(
                    $"https://api.telegram.org/bot{token}/sendPhoto?chat_id={chatId}&photo={encodedPhoto}&caption={encoded}&parse_mode=HTML{silent}");
                return;
            }
            catch (HttpRequestException)
            {
               
                Console.WriteLine("Photo message failed, sending as text instead.");
            }
        }

        await client.GetStringAsync(
            $"https://api.telegram.org/bot{token}/sendMessage?chat_id={chatId}&text={encoded}&parse_mode=HTML{silent}");
        
    }
}

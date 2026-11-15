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

    public async Task SendListing(Listing ad, bool night)
    {
        string place = ad.City;
        if (ad.DistanceKm >= 0)
        {
            place += $" ({ad.DistanceKm} km)";
        }

        if (!ad.CanShip)
        {
            place += " · alleen ophalen";
        }


        string safeTitle = System.Net.WebUtility.HtmlEncode(ad.Title);
        string message =
            $"<b>{safeTitle}</b>\n💶 {ad.PriceText}\n📍 {place}\n📅 {ad.Date}\n\n🔗 <a href=\"https://www.marktplaats.nl{ad.Link}\">Bekijk advertentie</a>";
        string encoded = Uri.EscapeDataString(message);
        string silent = "";
        if (night)
        {
            silent = "&disable_notification=true";
        }

        if (ad.Photo != "")
        {
            string encodedPhoto = Uri.EscapeDataString(ad.Photo);
            await client.GetStringAsync(
                $"https://api.telegram.org/bot{token}/sendPhoto?chat_id={chatId}&photo={encodedPhoto}&caption={encoded}&parse_mode=HTML{silent}");
        }
        else
        {
            await client.GetStringAsync(
                $"https://api.telegram.org/bot{token}/sendMessage?chat_id={chatId}&text={encoded}&parse_mode=HTML{silent}");
        }
    }
}
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MarktplaatsNotifier;

public partial class CommandHandler
{
    public async Task StartOnboarding()
    {
        step = "onboard_postcode";
        await session.Telegram.SendText(
            "👋 Welcome to Marktplaats Notifier!\n\nLet's set up two quick things so pickup distances work.\n📍 What's your Dutch postcode? (like 1234AB)");
    }
    private async Task HandleOnboardingInput(string text)
    {
        if (step == "onboard_km")
        {
            string clean = text.ToLowerInvariant().Replace("km", "").Trim();
            if (int.TryParse(clean, out int km) && km >= 0)
            {
                session.PickupKm = km;
                session.OnboardingComplete = true;
                session.SaveProfile();
                step = "";
                pendingOnboardingTour = true;
                await session.Telegram.SendText(
                    $"✅ All set! (Postcode {session.Postcode}, pickup {session.PickupKm} km)");
                await SendStarterPacksMenu();
            }
            else
            {
                await session.Telegram.SendText("❌ Type a whole number in km (0 = shipping only), like 10.");
            }

            return;
        }

        string cleanPostcode = text.Trim().ToUpperInvariant().Replace(" ", "");
        if (cleanPostcode.Length == 6 && int.TryParse(cleanPostcode.Substring(0, 4), out _) &&
            char.IsLetter(cleanPostcode[4]) && char.IsLetter(cleanPostcode[5]))
        {
            session.Postcode = cleanPostcode;
            session.SaveProfile();
            step = "onboard_km";
            await session.Telegram.SendText("🚗 How many km will you travel to pick something up? (0 = shipping only)");
        }
        else
        {
            await session.Telegram.SendText("❌ That's not a Dutch postcode. Use 4 numbers and 2 letters, like 1234AB.");
        }
    }
    private async Task<bool> TryHandleReplyShortcut(string text, SentListingRef listing)
    {
        string lower = text.ToLowerInvariant().Trim();

        if (lower == "watch" || lower.StartsWith("watch "))
        {
            double targetPrice = 0;
            if (lower.Length > 5)
            {
                string priceArg = lower.Substring(5).Replace("€", "").Trim();
                if (!double.TryParse(priceArg, NumberStyles.Float, CultureInfo.InvariantCulture, out targetPrice) || targetPrice < 0)
                {
                    await session.Telegram.SendText("❌ Use <code>watch</code> or <code>watch 80</code>.", parseMode: "HTML");
                    return true;
                }
            }

            session.Watchlist.AddOrUpdate(new WatchItem
            {
                Id = listing.Id,
                Title = listing.Title,
                Link = listing.Link,
                LastPrice = listing.Price,
                HighestBid = listing.HighestBid,
                BidCount = listing.BidCount,
                TargetPrice = targetPrice,
                AddedAt = DateTime.Now,
                LastCheckedAt = DateTime.Now
            });

            string targetNote = targetPrice > 0 ? $" (target ≤ {Listing.FormatEuro(targetPrice)})" : "";
            await session.Telegram.SendText($"👀 Watching <b>{Encode(listing.Title)}</b>{targetNote}.", parseMode: "HTML");
            return true;
        }

        if (lower.StartsWith("bought "))
        {
            string priceArg = lower.Substring(7).Replace("€", "").Trim().Replace(",", ".");
            if (!double.TryParse(priceArg, NumberStyles.Float, CultureInfo.InvariantCulture, out double buyPrice) || buyPrice < 0)
            {
                await session.Telegram.SendText("❌ Reply with <code>bought 50</code> to log the purchase price.", parseMode: "HTML");
                return true;
            }

            FlipItem flip = session.FlipTracker.AddPurchase(listing.Title, buyPrice);
            await session.Telegram.SendText(
                $"🛒 Logged purchase #{flip.Id}: <b>{Encode(flip.Name)}</b> for {Listing.FormatEuro(flip.BuyPrice)}.",
                Telegram.InlineKeyboard(new[] { new[] { Telegram.CallbackButton("💰 Open Flips", "flip:home") } }),
                parseMode: "HTML");
            return true;
        }

        if (lower == "block")
        {
            if (string.IsNullOrWhiteSpace(listing.SellerName))
            {
                await session.Telegram.SendText("❌ Couldn't find the seller name on that listing.");
                return true;
            }

            bool added = session.Blocklist.AddSeller(listing.SellerName);
            await session.Telegram.SendText(added
                ? $"🚫 Blocked seller <b>{Encode(listing.SellerName)}</b>."
                : $"ℹ️ Seller <b>{Encode(listing.SellerName)}</b> is already blocked.",
                parseMode: "HTML");
            return true;
        }

        return false;
    }
    private async Task SendTestListing()
    {
        Listing sample = new Listing
        {
            Id = "m9999999999",
            Title = "Samsung Odyssey G5 27 inch monitor — geen beeld wel geluid",
            Price = 55,
            OldPrice = 110,
            PriceText = "€55",
            PriceType = "FIXED",
            Condition = "Gebruikt",
            City = "Utrecht",
            DistanceKm = 4,
            Delivery = "Ophalen of Verzenden",
            CanShip = true,
            PostedAt = DateTime.Now.AddMinutes(-3),
            Views = 12,
            Saves = 2,
            SellerName = "Jan",
            SellerVerified = true,
            SellerRating = 4.9,
            SellerReviewCount = 34,
            SellerYears = 8,
            SellerType = "CONSUMER",
            PhotoCount = 4,
            Description = "Monitor gaat aan en lampje brandt, geen barst in het scherm maar blijft zwart.",
            Hint = "💡 Sound but no picture: usually the backlight or the power board",
            HintQuestion = "Als je met een zaklamp op het scherm schijnt, zie je dan iets?",
            MatchedSearch = "defect monitor",
            CategoryId = 322,
            CategoryName = "Monitoren",
            TypicalPrice = 105,
            TypicalCount = 19,
            AiVerdict = "BUY",
            AiReason = "No cracks visible; likely backlight or power board",
            AiResale = "€130-160",
            AiMaxBid = "€60",
            Link = "https://www.marktplaats.nl"
        };

        long msgId = await session.Telegram.SendListing(sample, night: false, alert: "TEST ALERT", isWatched: false, dutch: session.Settings.IsDutch);
        session.RememberSentMessage(sample, msgId);
    }
}

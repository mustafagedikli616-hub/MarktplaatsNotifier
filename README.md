# Marktplaats Notifier

A C# console app that watches [Marktplaats.nl](https://www.marktplaats.nl) searches and sends new listings straight to your phone via Telegram — much faster than Marktplaats' own saved-search emails.

I built this to catch cheap broken monitors, PCs and PC parts to repair and resell, before anyone else sees them.

## Features

- Checks multiple searches every ~minute, each with its **own max price**
- Only sends **new** listings — seen listing IDs are saved to a file, so nothing is sent twice, even after a restart
- Filters out listings that don't offer shipping
- Shows "Bieden", "Gratis" or "Zie omschrijving" instead of a fake €0 price
- Telegram messages with the listing **photo**, bold title, price, city and a link
- Crash protection: a network hiccup or an odd listing is logged and skipped instead of stopping the bot
- Quiet first run, so you don't get flooded with hundreds of old listings

## How it works

1. Fetches search results as JSON from Marktplaats' search endpoint (`/lrp/api/search`)
2. Parses each listing with `System.Text.Json`
3. Skips listings it has already seen, that are too expensive or that are pickup-only
4. Sends the rest to Telegram with the Bot API (`sendPhoto` / `sendMessage`, HTML formatting)

## Setup

**Requirements:** .NET 6 or newer, and a Telegram account.

1. **Create a Telegram bot:** message [@BotFather](https://t.me/BotFather), send `/newbot` and copy the token it gives you.
2. **Get your chat ID:** send any message to your new bot, then open  
   `https://api.telegram.org/bot<YOUR_TOKEN>/getUpdates` and copy the `chat.id` number.
3. **Create `secrets.txt`** in the project folder with two lines:
   ```
   <your bot token>
   <your chat id>
   ```
   This file is in `.gitignore`, so it never gets uploaded.
4. **Run it:**
   ```
   dotnet run
   ```

## Changing the searches

Edit the `searches` dictionary in `Program.cs`. Each line is a search term and its max price in euros:

```csharp
{ "defect monitor", 60 },
{ "rtx 3060", 110 },
```

Tip: after adding a new search, delete `seen.txt` so the first round is quiet again and you don't get all of that search's old listings.

## Note

This uses an unofficial Marktplaats endpoint, so it may change or break at any time. Keep the check interval reasonable.

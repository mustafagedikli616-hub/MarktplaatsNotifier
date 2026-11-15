# 🛒 Marktplaats Notifier

![C#](https://img.shields.io/badge/C%23-239120?logo=csharp&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-512BD4?logo=dotnet&logoColor=white)
![Telegram](https://img.shields.io/badge/Telegram_Bot-26A5E4?logo=telegram&logoColor=white)

A C# console app that watches [Marktplaats.nl](https://www.marktplaats.nl) searches and sends new listings straight to your phone via Telegram, within about a minute of being posted. Much faster than Marktplaats' own saved searches.

I built this to catch cheap broken monitors, PCs and PC parts to repair and resell, before anyone else sees them.

<p align="center">
  <img src="docs/screenshot.png" alt="Telegram notifications from the bot" width="320">
</p>

## Features

- Checks multiple searches every ~minute, each with its **own max price**
- Only sends **new** listings — seen listing IDs are saved to a file, so nothing is sent twice, even after a restart
- Filters out listings that don't offer shipping
- Shows "Bieden", "Gratis" or "Zie omschrijving" instead of a fake €0 price
- Telegram messages with the listing **photo**, bold title, price, city and a link
- Crash protection: a network hiccup or an odd listing is logged and skipped instead of stopping the bot
- Optional **category per search**, so "pc defect" doesn't send you car parts
- Skips "Gezocht" (wanted) ads and repair-service ads
- Quiet first run, so you don't get flooded with hundreds of old listings
- Friendly error messages: a typo in `searches.txt` or a wrong token is explained instead of crashing

## Built with

- **C# / .NET** – console app with top-level statements
- **HttpClient** + **async/await** – fetching search results
- **System.Text.Json** – parsing the listing data
- **Telegram Bot API** – sending the notifications

## How it works

1. Fetches search results as JSON from Marktplaats' search endpoint (`/lrp/api/search`)
2. Parses each listing with `System.Text.Json`
3. Skips listings it has already seen, that are too expensive or that are pickup-only
4. Sends the rest to Telegram with the Bot API (`sendPhoto` / `sendMessage`, HTML formatting)

## Setup

👉 **Not a programmer?** Follow the step-by-step [**Setup guide**](SETUP.md). No coding needed.

**For developers:** you need .NET 6 or newer. Clone the repo and run:

```
dotnet run
```

On the first launch, the app asks for your Telegram bot token and finds your chat ID by itself. Both are saved to `secrets.txt`, which is in `.gitignore`.

## Changing the searches

Edit `searches.txt`. Each line is a search term, a `;`, its max price in euros and optionally a `;` with a Marktplaats category ID:

```
defect monitor;60;322
rtx 3060;110;322
iphone 13;250
```

With a category, only that category is searched (`322` = Computers en Software). Without one, all of Marktplaats is searched. A list of common category IDs is at the top of `searches.txt`.

If a line has a mistake, the app tells you which line and what's wrong.

Tip: after adding a new search, delete `seen.txt` so the first round is quiet again and you don't get all of that search's old listings.

## Roadmap

- [ ] Allow pickup-only listings near my own city
- [x] Limit searches to specific Marktplaats categories
- [ ] Run 24/7 on a small server

## Note

This uses an unofficial Marktplaats endpoint, so it may change or break at any time. Keep the check interval reasonable.

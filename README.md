# 🛒 Marktplaats Notifier

![C#](https://img.shields.io/badge/C%23-239120?logo=csharp&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-512BD4?logo=dotnet&logoColor=white)
![Telegram](https://img.shields.io/badge/Telegram_Bot-26A5E4?logo=telegram&logoColor=white)

A C# console app that watches [Marktplaats.nl](https://www.marktplaats.nl) searches and sends new listings straight to your phone via Telegram, usually within a few minutes of being posted. Much faster than Marktplaats' own saved searches.

I built this to catch cheap broken monitors, PCs and PC parts to repair and resell, before anyone else sees them.

<p align="center">
  <img src="docs/screenshot.png" alt="Telegram notifications from the bot" width="320">
</p>

## Features

- Checks all your searches in rounds, each with its **own max price**, newest listings first (a round takes about 3 seconds per search)
- Only sends **new** listings — seen listing IDs are saved to a file, so nothing is sent twice, even after a restart
- Telegram messages with the listing **photo**, bold title, price, city, distance, date and a link
- Shows "Bieden", "Gratis" or "Zie omschrijving" instead of a fake €0 price
- Optional **category per search**, so "pc defect" doesn't send you car parts
- Optional **skip words per search**: skip listings that mention e.g. `barst` or `waterschade`. Pieces of text with "geen" or "zonder" are ignored, so "geen waterschade" is not skipped
- 🚨 **Super deal alerts**: a fixed price at or below half of the search's max price gets a loud 🚨 SUPER DEAL 🚨 message
- 🚨 **Urgent searches**: mark a search as `urgent` and every new listing from it gets a loud 🚨 URGENT 🚨 message
- **Quiet nights**: between 23:00 and 07:00 normal listings arrive silently, super deals and urgent searches still make a sound
- **Shipping or nearby**: listings must offer shipping, or be pickup-only within the distance you chose from your postcode
- **Telegram commands** to manage your searches from your phone: `/add` (with category buttons), `/remove`, `/list`, `/cancel`, `/help`
- Skips "Gezocht" (wanted), "Inkoop" (buying) and repair-service ads
- Quiet first round after every start, so you don't get flooded with hundreds of old listings
- Crash protection: a network hiccup or an odd listing is logged and skipped instead of stopping the bot
- Friendly error messages: a typo in `searches.txt` or a wrong token is explained instead of crashing

## Built with

- **C# / .NET** – console app with top-level statements
- **HttpClient** + **async/await** – fetching search results
- **System.Text.Json** – parsing the listing data
- **Telegram Bot API** – sending the notifications

## How it works

1. Fetches search results as JSON from Marktplaats' search endpoint (`/lrp/api/search`)
2. Parses each listing with `System.Text.Json`
3. Skips listings it has already seen, wanted/repair ads, listings with one of the search's skip words, listings above the max price, and pickup-only listings that are too far away
4. Decides if a listing is a super deal or comes from an urgent search
5. Sends the rest to Telegram with the Bot API (`sendPhoto` / `sendMessage`, HTML formatting), silently at night unless it's urgent

📖 Want to know what every file and method does? See [**How it works (code guide)**](docs/HOW-IT-WORKS.md).

## Setup

👉 **Not a programmer?** Follow the step-by-step [**Setup guide**](SETUP.md). No coding needed.

**For developers:** you need .NET 6 or newer. Clone the repo and run:

```
dotnet run
```

On the first launch, the app asks for your Telegram bot token, finds your chat ID by itself, and asks for your postcode and how far you'll travel for pickup. Everything is saved to `secrets.txt`, which is in `.gitignore`, so your token and postcode never end up on GitHub.

## Changing the searches

Edit `searches.txt`, or use `/add` and `/remove` in Telegram. Each line is one search, with up to 5 fields separated by `;`:

```
search words;max price;category;skip words;urgent
```

| Field | Required | Example | Meaning |
|---|---|---|---|
| search words | ✅ | `defect monitor` | What you type in the Marktplaats search bar |
| max price | ✅ | `60` | Max price in whole euros |
| category | optional | `322` | Only search this Marktplaats category |
| skip words | optional | `barst,strepen` | Comma separated. Listings that mention one of these are skipped |
| urgent | optional | `urgent` | Every new listing from this search gets a loud 🚨 URGENT 🚨 message |

Examples:

```
defect monitor;60;322;barst,gebarsten,strepen,lijnen,waterschade
rtx 3060;110;322
galaxy a36 defect;90;820;;urgent
iphone 13;250
```

To use a later field without an earlier one, leave the earlier one empty: `galaxy a36 defect;90;820;;urgent` has no skip words but is urgent.

Common category IDs: `322` = Computers en Software, `356` = Spelcomputers en Games, `31` = Audio, Tv en Foto, `820` = Telecommunicatie. The list is also at the top of `searches.txt`.

- Lines starting with `#` are switched off (comments). Put comments on their own line, never after a search.
- If a line has a mistake, the app tells you which line and what's wrong.
- The first round after every start is quiet, so new searches don't flood you with their old listings.

**Super deals** need no setup: a listing with a fixed price (not "Bieden") at or below **half** of the search's max price is always a super deal. With `rtx 3070;140`, a 3070 for €70 or less is a super deal.

## Roadmap

- [x] Limit searches to specific Marktplaats categories
- [x] Allow pickup-only listings near my own city
- [x] Manage searches from Telegram
- [x] Skip words per search
- [x] Super deal and urgent alerts
- [x] Postcode and pickup distance asked at the first launch
- [ ] Skip words and urgent in the Telegram `/add` flow
- [ ] Telegram warning after several errors in a row, and slowing down on a 429 "too many requests"
- [ ] One request per category instead of one per search, so a round takes seconds instead of minutes
- [ ] Multi-user: one bot for friends, each with their own searches
- [ ] Run 24/7 on a small server

## Note

This uses an unofficial Marktplaats endpoint, so it may change or break at any time. Keep the check interval reasonable.

# Marktplaats Notifier

![C#](https://img.shields.io/badge/C%23-239120?logo=csharp&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-512BD4?logo=dotnet&logoColor=white)
![Telegram](https://img.shields.io/badge/Telegram_Bot-26A5E4?logo=telegram&logoColor=white)
![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)

A C# console app that watches [Marktplaats.nl](https://www.marktplaats.nl) — plus **Tweakers V&A**, **2dehands.be**, **Kleinanzeigen.de**, **Coolblue Tweedekans**, and **Vinted.nl** — and pings you on Telegram and in a local web dashboard as soon as something interesting pops up. Much faster than Marktplaats' own saved searches.

I built this to catch cheap broken monitors, PCs, and PC parts to repair and resell before anyone else sees them.

<p align="center">
  <img src="docs/screenshot.png" alt="Telegram notifications from the bot" width="320">
</p>

## What it does

- **Fast polling without hammering Marktplaats:** active and `urgent` searches run as often as every 60 seconds, quiet ones slow down automatically. Requests have jitter, proper browser headers, and automatic backoff if Marktplaats ever returns 429 or 403.
- **Built-in Web Dashboard (`http://localhost:5050`):** starts right alongside the Telegram bot with live updates (SSE), browser notifications + sound chime, skip-reason breakdown, search management, watchlist, flip tracker with monthly profit chart, market price checker, filter/hint tester, and `.zip` backup download. Optional password via `WEB_PASSWORD`.
- **Smart filtering:** max price per search, category/subcategory (`322` or `356/2954`), shipping or pickup within your km limit, skip words (ignoring `"geen waterschade"` / `"zonder barst"`), business (`TRADER`) accounts, blocked sellers, and `"Bieden"` ads where the highest bid is already above your max.
- **Full listing check:** fetches the ad page for the complete description, views, saves, post time, bids, seller age, and review score. Warns you if a shipped item is from a brand-new account with 0 reviews.
- **30-day history (`history.txt`):** catches price drops, shows the typical median price (`/price`), flags reposts by text or perceptual photo hash (`dHash`), and finds old unsold ads (`/stale`) where sellers are more likely to take a lower offer.
- **Extra sources (each toggleable in `/settings` and the Web UI):**
  - **Tweakers Vraag & Aanbod** RSS feed (`tweakers.net/feeds/va/aanbod.xml`)
  - **2dehands.be** (Belgium) for shipped deals
  - **Kleinanzeigen.de** (Germany) filtered to `Versand möglich`
  - **Coolblue Tweedekans** open-box/returned tech deals with retail discount + warranty
  - **Vinted.nl** for consoles, controllers, and electronics
- **Repair hints (`hints.txt`):** matches common fault descriptions and adds a repair tip + a Dutch question you can copy and send to the seller.
- **Optional Vision AI check (`/ai`):** sends ads that pass all filters (plus up to 2 photos) to Gemini (free tier works great), Groq, OpenRouter, Claude, OpenAI, or local Ollama to spot things like cracked screens in photos.
- **Manage everything from Telegram (`/menu`):**
  - Add searches via `/add`, by pasting a Marktplaats search URL, or by typing `rtx 3070 under 140`.
  - Browse, search (`/find`), edit, live-test, or delete searches (with an Undo button).
  - Tap **Watch** under any listing (or share a listing link from the Marktplaats app) to get pinged on price drops, new bids, or reserved/removed status.
  - Snooze alerts for 1h, 3h, or until tomorrow morning, with a morning summary of overnight listings.
  - Track buys, repair parts, and profit in `/profit`, back up/restore via `.zip`, and share one running bot with friends using `/invite`.
- **Live reload:** editing `searches.txt` or `hints.txt` on disk reloads automatically without restarting.

## Docs

- First time setting this up? Follow [SETUP.md](SETUP.md).
- All Telegram commands, file formats, and env vars: [docs/COMMANDS.md](docs/COMMANDS.md).
- Something not working or want to view the web UI on your phone? Check [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md).
- Want to read or edit the code? See [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md) and [CONTRIBUTING.md](CONTRIBUTING.md).
- Recent changes: [CHANGELOG.md](CHANGELOG.md).

## Quick start

With [.NET 10](https://dotnet.microsoft.com/download) installed:

```bash
dotnet run
```

First launch walks you through entering your Telegram bot token, detecting your chat ID, and setting your postcode + max pickup distance. Everything gets saved to `secrets.txt` (gitignored). Once it's running, open `http://localhost:5050` in your browser for the web dashboard.

## Running 24/7 on a Mini PC, Raspberry Pi, or server

### Docker

1. Put your `searches.txt` (and `secrets.txt` if you already have one) in `./data`, or set the environment variables in [docker-compose.yml](docker-compose.yml).
2. Start it:

```bash
docker compose up -d --build
```

### Linux systemd (without Docker)

Run `dotnet run` once so `secrets.txt` is created, then:

```bash
chmod +x deploy/install-service.sh
./deploy/install-service.sh
```

To keep it running after a reboot without logging in: `sudo loginctl enable-linger $USER`.

## `searches.txt` format

You can manage searches from Telegram or the Web UI, or edit `searches.txt` directly:

```text
search words;max price;category;skip words;urgent
```

Examples:

```text
defect monitor;60;322;barst,gebarsten,strepen,lijnen,waterschade
rtx 3060;110;322
ps5 defect;120;356/2954;;urgent
iphone 13;250
```

- Only search words and max price are required.
- Category can be a main category (`322`) or main/subcategory (`356/2954`).
- Common categories: `322` Computers en Software, `356` Spelcomputers, `31` Audio/Tv/Foto, `820` Telecommunicatie.
- Lines starting with `#` are ignored.

## Note

This uses Marktplaats' internal search endpoint, which isn't an official public API and can change at any time.

# How it works (code guide)

A reminder of what each file does, written for future me.
Method names are used instead of line numbers since line numbers move around.

---

## How a listing flows through the app

```text
Main loop (Program.cs):
  Check auto-reload (searches.txt / hints.txt), snooze timers, morning digest, watchlist
        │
        ▼
  Pick next due (user, search) via SearchScheduler
        │
        ▼
  FetchListings() ──▶ 30 newest listings from /lrp/api/search (45s shared JSON cache)
        │
        ▼
  ListingHistory.Remember() ──▶ dropped in price? ──yes──▶ TrySend(..., "PRICE DROP")
        │
        ▼
  First check (quiet) or already seen ID? ──yes──▶ skip
        │ no
        ▼
  TrySend():
    1. Free checks: subcategory, blocked seller, gezocht/reparatie, skip words, max price, shipping/distance
    2. AddPageDetails(): fetch listing page for full description, views, saves, bids, seller age/rating/type
    3. Page checks: HighestBid > maxPrice, price in description, skip words in full description, TRADER / likely business
    4. Extras: RepairHints, TypicalPrice, FindRepost, optional AiGrader (text + up to 2 photos)
    5. Telegram.SendListing() with [Bekijk op Marktplaats] [Watch] buttons
    6. Mark seen in seen.txt, clear pending price drop, record stats & daily digest
```

---

## The files

| File | What it does |
|---|---|
| `Program.cs` | Startup, main polling loop, Marktplaats search + listing page scraping, watchlist checker, and Telegram update routing. |
| **Core (`src/Core/`)** | |
| `src/Core/Listing.cs` | Data model for one listing + `FromJson` parser and euro formatting (`FormatEuro`). |
| `src/Core/BotSettings.cs` | Per-user settings saved in `settings.json` (language, night hours, business/price filters, AI config). |
| `src/Core/UserSession.cs` & `UserManager.cs` | Per-user state isolation (owner uses root folder, invited users get `users/<chatId>/`), `/invite` codes, and `.zip` backup/restore. |
| `src/Core/AppFiles.cs` & `BotLog.cs` | Resolves the data folder (`MARKTPLAATS_DATA_DIR`, project folder, or exe folder) and keeps recent log lines in memory for `/logs` and the web UI. |
| **Sources (`src/Sources/`)** | |
| `src/Sources/SearchList.cs` | Reads, validates, writes, and auto-reloads `searches.txt` into `SearchRule` objects. |
| `src/Sources/SearchScheduler.cs` | Tracks when each search should run next (60s–900s adaptive wait, faster for `urgent`, slower at night). |
| `src/Sources/RequestLimiter.cs` | Adaptive delay between Marktplaats requests (2s with ±20% jitter, up to 60s on 429/403). |
| `src/Sources/MarktplaatsUrlParser.cs` | Parses Marktplaats search URLs (for `/add`) and shared listing links (for Watchlist). |
| `src/Sources/TweakersSource.cs` | Polls the Tweakers Vraag & Aanbod RSS feed (`tweakers.net/feeds/va/aanbod.xml`) and converts offers into listings. |
| `src/Sources/TweedehandsSource.cs` | Round-robins eligible component searches (GPUs, CPUs, RAM) against `2dehands.be/lrp/api/search` with a strict 85% price cap. |
| `src/Sources/CoolblueSource.cs` | Rotates through Coolblue Tweedekans tech categories and only alerts on real price errors (40%+ off retail & strict cap). |
| `src/Sources/VintedSource.cs` | Round-robins searches against Vinted.nl's newest-first SSR catalog cards. |
| **Analysis (`src/Analysis/`)** | |
| `src/Analysis/Filters.cs` | Skip-word matching (with clause-safe negation for `"geen"` / `"zonder"`), night-hour check, super-deal check, not-for-sale filter, and description price extraction. |
| `src/Analysis/BusinessFilter.cs` | Heuristic score (non-round cents, website link, emoji title, business name/words) to spot commercial sellers. |
| `src/Analysis/RepairHints.cs` | Loads and auto-reloads `hints.txt`, matches fault patterns (`+` for AND, `,` for OR), returns a tip + Dutch seller question. |
| `src/Analysis/ImageHasher.cs` | Pure-C# JPEG luminance `dHash` (64-bit) to catch reposted listings that reuse the same photo under a new title or seller. |
| `src/Analysis/PriceLookup.cs` | Estimates typical market price (median + range) from local history and live Marktplaats listings for `/price` and the web UI. |
| `src/Analysis/AiGrader.cs` | Optional vision AI check (Gemini, Groq, OpenRouter, Claude, OpenAI, or local Ollama) with daily cap. |
| **Tracking (`src/Tracking/`)** | |
| `src/Tracking/ListingHistory.cs` | Keeps 30 days of listings in `history.txt` (thread-safe, atomic saves). Powers price drops, median typical price, photo/text repost detection, and `/stale`. |
| `src/Tracking/Watchlist.cs` | Stores watched listings in `watchlist.json` for price drop, bid, reserved, and removed alerts. |
| `src/Tracking/Blocklist.cs` | Stores global skip words and blocked seller names in `blocklist.json`. |
| `src/Tracking/FlipTracker.cs` | Tracks bought items, repair part costs, sales, and ROI in `flips.json`. |
| `src/Tracking/SearchStats.cs` | Per-search counters (checks, sent, skipped, average price) saved in `stats.json` for `/stats`. |
| `src/Tracking/DailyDigest.cs` | Tracks today's counts and silent overnight listings for `/today` and the morning summary. |
| `src/Tracking/ActivityFeed.cs` | Keeps recent sent/skipped/error events and daily skip-reason breakdown in memory for the web dashboard. |
| **Telegram (`src/Telegram/`)** | |
| `src/Telegram/CommandHandler.cs` | All Telegram commands, `/menu`, inline button callbacks, natural quick-add (`rtx 3070 under 140`), swipe-replies, starter packs, and `.zip` restore. |
| `src/Telegram/Telegram.cs` | Talks to the Telegram Bot API over HTTP POST (`sendMessage`, `sendPhoto`, `sendDocument`, `editMessageText`, `editMessageReplyMarkup`, `getFile`). |
| **Web (`src/Web/` & `wwwroot/`)** | |
| `src/Web/WebDashboard.cs` | Built-in `HttpListener` server (default `http://localhost:5050`), JSON REST API, SSE live feed (`/api/events`), and optional `WEB_PASSWORD` auth. |
| `wwwroot/index.html` | Single-file web dashboard embedded into the binary (live feed, searches, watchlist, flips, price check, filter tester, logs, settings). |

---

## Data files (all gitignored except `searches.txt`)

- `secrets.txt` — bot token, owner chat ID, postcode, pickup km (4 lines).
- `ai_key.txt` — optional AI API key (never included in `/backup`).
- `searches.txt` — your searches.
- `hints.txt` — repair hint rules (auto-created on first run).
- `seen.txt` — listing IDs already sent (trimmed to 50,000 on startup).
- `history.txt` — 30-day listing history.
- `settings.json`, `blocklist.json`, `watchlist.json`, `flips.json`, `stats.json` — saved bot state.
- `users/<chatId>/` — same files per invited user in multi-user mode.

---

## Where to change common things

| Want to change | Look in |
|---|---|
| Scheduler intervals (min/max/urgent/night) | Constants at the top of `src/Sources/SearchScheduler.cs` |
| Rate-limit delay / backoff | `src/Sources/RequestLimiter.cs` |
| Skip-word / description-price rules | `src/Analysis/Filters.cs` |
| Business detection words | `src/Analysis/BusinessFilter.cs` |
| How Telegram listing messages look | `src/Telegram/Telegram.cs` (`SendListing`) |
| AI grading prompt or default models | `src/Analysis/AiGrader.cs` |
| Starter packs or category list | Top of `src/Telegram/CommandHandler.cs` |
| Web dashboard UI / API | `wwwroot/index.html` and `src/Web/WebDashboard.cs` |

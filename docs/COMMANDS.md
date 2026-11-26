# Commands & Configuration

You don't need to memorize these — `/menu` has buttons for almost everything, and the Web UI at `http://localhost:5050` lets you edit the same data from a browser.

## Telegram commands

### Searches
| Command | What it does |
|---|---|
| `/menu` or `/start` | Opens the main button menu. |
| `/add` | Step-by-step wizard to add a search. Also accepts a Marktplaats URL, a semicolon line, or plain text like `/add rtx 3070 under 140`. |
| `/list` | Browse, edit, test (`Check now`), toggle urgent, or delete your searches. |
| `/find <word>` | Filter your search list (e.g. `/find rtx`). |
| `/remove <search>` | Delete a search (comes with an Undo button). |
| `/price <item>` | Check typical median price and suggested buy targets (e.g. `/price rtx 3070`). |
| `/stale [days]` | Show unsold listings from your history that have been sitting for at least `days` (default: 7). |

### Watching, blocking & flips
| Command | What it does |
|---|---|
| `/watchlist` | View and manage watched listings (price drops, new bids, reserved/removed alerts). |
| `/block <seller or word>` | Block a seller or add a global skip word. |
| `/unblock <seller or word>` | Remove a seller or global skip word from the blocklist. |
| `/bought ...` | Log a bought item for flip tracking. |
| `/parts ...` | Add repair part costs to an open flip. |
| `/sold ...` | Mark a flip as sold and calculate profit. |
| `/profit` | Open the flips & profit menu. |

### Control, stats & settings
| Command | What it does |
|---|---|
| `/pause` / `/resume` | Pause or resume checking. |
| `/snooze <1h\|3h\|morning>` | Temporarily mute notifications (also works without the slash, like `snooze 2h`). |
| `/today` | Today's summary (sent, skipped, silent overnight deals). |
| `/stats` | Per-search hit rates and average prices. |
| `/status` | Current bot status, uptime, and queue state. |
| `/settings` | Change postcode, pickup distance, night hours, language (EN/NL), and filters. |
| `/ai` | Configure optional Vision AI grading (provider, model, API key, daily cap). |
| `/backup` | Sends a `.zip` backup of your settings, searches, history, and flips (never includes `secrets.txt` or `ai_key.txt`). Send that `.zip` back to restore it. |
| `/test` | Send a fake test notification to verify formatting and delivery. |
| `/cancel` | Cancel whatever step you're currently in. |
| `/help` | Short command list inside Telegram. |

### Owner-only commands
| Command | What it does |
|---|---|
| `/invite` | Generate a one-time 24h invite code (`/join <code>`) so a friend can use your bot with their own searches. |
| `/users` | View or remove invited users. |
| `/logs` | Show the last 25 log lines. |

## Shortcuts that work without commands

- **Quick-add:** just type `rtx 3070 under 140` or `ps5 onder 180` in chat.
- **Paste a Marktplaats search URL:** automatically parses the search query, category, and max price.
- **Share a Marktplaats listing link:** adds it straight to your Watchlist.
- **Swipe-reply on a listing message:** quickly block the seller, log it as bought, or adjust the search.

---

## File formats

### `searches.txt`

One search per line, auto-reloaded whenever you save the file:

```text
search words;max price;category;skip words;urgent
```

- `search words` and `max price` are required.
- `category` is optional (`322` for main category, or `356/2954` for main/subcategory).
- `skip words` is a comma-separated list (e.g. `barst,waterschade,strepen`).
- Put `urgent` in the 5th column to check that search more often.

### `hints.txt`

Auto-created on first run and auto-reloaded on save:

```text
trigger words;repair tip;Dutch question to ask the seller
```

- Separate OR triggers with commas (`,`).
- Join AND words with `+`, e.g. `ps5+geen beeld,xbox+geen beeld`.
- Checked top-to-bottom; the first matching rule is shown.

---

## Environment variables

All of these are optional if you use `secrets.txt` and `/settings`. Useful for Docker or systemd.

| Variable | What it sets |
|---|---|
| `TELEGRAM_BOT_TOKEN` | Telegram bot token (overrides `secrets.txt`). |
| `TELEGRAM_CHAT_ID` | Owner Telegram chat ID. |
| `MARKTPLAATS_POSTCODE` | Your 4-digit Dutch postcode (e.g. `1012`). |
| `MARKTPLAATS_PICKUP_KM` | Max pickup distance in km for pickup-only ads. |
| `MARKTPLAATS_DATA_DIR` | Where state files (`searches.txt`, `history.txt`, `*.json`) are stored. |
| `WEB_PORT` | Port for the web dashboard (default: `5050`). |
| `WEB_PASSWORD` | Require a password to open the web dashboard. |
| `AI_PROVIDER` | AI provider (`gemini`, `groq`, `openrouter`, `claude`, `openai`, `ollama`). |
| `AI_MODEL` | Override the default model for the selected AI provider. |
| `AI_API_KEY` | Generic API key (or use `GEMINI_API_KEY`, `GROQ_API_KEY`, `OPENROUTER_API_KEY`, `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`). |

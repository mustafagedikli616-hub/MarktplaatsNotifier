# Troubleshooting & FAQ

Stuff that actually comes up when running this bot, and how to fix it.

## Why didn't the bot send a listing I saw on Marktplaats?

A few common reasons:
- **First check is quiet:** when you add a brand-new search, the first poll records existing listings without spamming your phone. Only listings posted after that (or price drops on existing ones) get sent.
- **Already seen:** check the **Skip Breakdown** in the Web UI (`http://localhost:5050`) or run `/today` in Telegram to see what got filtered out and why.
- **Hidden price / high bid:** if a `"Bieden"` ad already has a highest bid above your max price, or the description says `"vraagprijs €250"`, it gets skipped.
- **Pickup too far:** pickup-only listings beyond your `pickup km` distance are skipped. Listings that offer shipping (`Verzenden`) always pass distance checks.
- **Business account:** `TRADER` accounts and likely shops are filtered out if business filtering is turned on in `/settings`.
- **Skip words:** check both your per-search skip words and your global `/block` list. You can test any title/description in the Web UI's **Filter Tester**.

## I'm seeing 403 or 429 errors in the logs

Marktplaats rate-limits aggressive scraping. The bot already waits at least 2 seconds (with random jitter) between requests and backs off automatically up to 60 seconds when it sees a 403 or 429.

If it keeps happening:
- Don't run two copies of the bot from the same IP at the same time.
- Avoid marking dozens of searches as `urgent`. Keep `urgent` for your top 2–5 searches.
- If you're on a cheap VPS / cloud IP, Marktplaats sometimes blocks datacenter IP ranges outright. Running it on a home connection (Mini PC, Raspberry Pi, or your own PC) is way more reliable.

## How do I find a Marktplaats category or subcategory ID?

Easiest way: search on Marktplaats.nl, pick the category you want in the sidebar, copy the URL, and paste it straight into Telegram (`/add <url>`). The bot extracts the category ID automatically.

Common ones:
- `322` — Computers en Software
- `356` — Spelcomputers en Games (`356/2954` for PlayStation 5, etc.)
- `31` — Audio, Tv en Foto
- `820` — Telecommunicatie (phones)

## How do I open the Web Dashboard from my phone?

By default, the web UI runs on port `5050` on the machine running the bot (`http://localhost:5050`).

- **Same home Wi-Fi:** find your PC/Pi's local IP (e.g. `192.168.1.42`) and open `http://192.168.1.42:5050` on your phone. Make sure port `5050` isn't blocked by your firewall.
- **Outside your home network:** don't port-forward it directly to the internet. Install [Tailscale](https://tailscale.com) (free for personal use) on your PC/Pi and phone, set `WEB_PASSWORD` just in case, and open `http://<tailscale-ip>:5050` from anywhere.

On Linux/macOS, binding to all interfaces (`http://+:5050/`) works out of the box. On Windows, if `HttpListener` only binds to `localhost` because of URL ACL permissions, run once in an elevated PowerShell:

```powershell
netsh http add urlacl url=http://+:5050/ user=Everyone
```

## How do I set up free Vision AI grading?

1. Go to [Google AI Studio](https://aistudio.google.com/apikey) and create a free Gemini API key.
2. In Telegram, send `/ai`, choose **Gemini**, and paste your key (or set `GEMINI_API_KEY` as an environment variable).
3. Turn AI grading on. Only listings that pass all your other filters get sent to the AI, so a free tier key easily lasts all day.

If you want 100% local AI with no API keys, run [Ollama](https://ollama.com) with a vision model (like `llama3.2-vision` or `qwen2.5vl`) and select **Ollama** in `/ai`.

## `dotnet run` complains about missing .NET 10

This project targets `.NET 10` (`net10.0`). Check `dotnet --info` to confirm you have the .NET 10 SDK installed. On Arch/Manjaro Linux, make sure `dotnet-sdk` and `dotnet-runtime` are the same minor version (don't mix AUR `dotnet-runtime-bin` with official `dotnet-sdk`).

## Where are my files stored?

When running with `dotnet run`, files live right in the project folder (`searches.txt`, `secrets.txt`, `seen.txt`, `history.txt`, `*.json`). In Docker or systemd, set `MARKTPLAATS_DATA_DIR` to point wherever you want your data stored.

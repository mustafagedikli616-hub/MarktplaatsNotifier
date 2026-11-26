# Contributing

PRs, bug fixes, and ideas are welcome. Keep things simple and readable.

## Running locally

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download):

```bash
dotnet build MarktplaatsNotifier.csproj
dotnet run
```

## Running the tests

The test suite lives in `tests/MarktplaatsNotifier.Tests` and doesn't hit live Marktplaats servers:

```bash
dotnet run --project tests/MarktplaatsNotifier.Tests/MarktplaatsNotifier.Tests.csproj
```

If you fix a bug in filtering, parsing, history, or the web API, please add a test case there so it stays fixed.

## Project layout

- `Program.cs` — startup, polling loop, scraping, update routing.
- `src/Core/` — `Listing`, settings, user sessions, paths, logging.
- `src/Sources/` — Marktplaats URL parsing, rate limiter, scheduler, search list, Tweakers RSS.
- `src/Analysis/` — skip-word filters, business detection, repair hints, image hashing, price lookup, AI grader.
- `src/Tracking/` — 30-day history, watchlist, blocklist, flip tracker, stats, digest, activity feed.
- `src/Telegram/` — Telegram Bot API client and command/button handler.
- `src/Web/` + `wwwroot/index.html` — built-in `HttpListener` dashboard and single-file frontend.

See [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md) for how a listing flows through the app.

## Guidelines

- **Zero external NuGet packages:** the whole app runs on plain .NET 10 standard libraries (`System.Net.Http`, `System.Text.Json`, `HttpListener`, etc.) so single-file publishing and offline builds stay fast and clean. Please don't add heavy dependencies unless there's a really good reason.
- **Be polite to Marktplaats:** never lower the request delay floor or strip out jitter/backoff.
- **Keep private files private:** never commit `secrets.txt`, `ai_key.txt`, `seen.txt`, or personal data.

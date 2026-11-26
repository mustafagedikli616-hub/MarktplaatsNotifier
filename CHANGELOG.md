# Changelog

All notable changes to Marktplaats Notifier are listed here.

## [Unreleased]

### Added
- **Built-in Web Dashboard (`http://localhost:5050`):** runs next to the Telegram bot with live Server-Sent Events (`/api/events`), browser notifications + sound chime, skip-reason breakdown, bulk search actions, watchlist, flip calculator + monthly profit chart, live market price check, filter/hint tester, logs, settings, and `.zip` backup download. Supports `WEB_PORT` and `WEB_PASSWORD`.
- **Multi-source deal monitoring (each toggleable in `/settings` and Web UI):**
  - **Tweakers Vraag & Aanbod** (`tweakers.net/feeds/va/aanbod.xml`, skipping `V:` / `Gevraagd:` wanted posts).
  - **2dehands.be** (Belgium, same JSON search API as Marktplaats).
  - **Kleinanzeigen.de** (Germany, newest-first with `Versand möglich` shipping detection).
  - **Coolblue Tweedekans** (open-box/returned tech deals with retail discount and condition).
  - **Vinted.nl** (newest-first catalog cards with price, brand, and condition).
- **Photo repost detection (`ImageHasher`):** pure-C# 64-bit JPEG `dHash` stored in `history.txt` to catch relisted items even when the title or seller changes.
- **Market price lookup (`/price`):** estimates typical median price and suggested buy targets from 30-day history and live listings.
- **Automated test suite (`tests/MarktplaatsNotifier.Tests`):** 39 tests covering filters, search matching, business detection, Tweakers RSS, JPEG hashing, history concurrency, URL parsing, and live WebDashboard + SSE endpoints.
- **Docker, systemd & GitHub Actions:** added `Dockerfile`, `docker-compose.yml`, `deploy/install-service.sh`, and `.github/workflows/ci-release.yml` for builds and tagged releases (`win-x64`, `linux-x64`, `linux-arm64`).

### Changed
- Organized the C# source files into `src/Core`, `src/Sources`, `src/Analysis`, `src/Tracking`, `src/Telegram`, and `src/Web`.
- Hardened Marktplaats HTTP requests (`SocketsHttpHandler` with decompression and cookies, per-endpoint browser `Accept` / `Sec-Fetch-*` headers, and 2s minimum delay with ±20% jitter) to avoid 403/429 blocks.

### Fixed
- Clause-safe skip-word negation (`"geen beeld, wel barst"` now catches `barst`, while `"geen waterschade"` and `"zonder barst"` are still ignored).
- Business name false positives (e.g. `BVermeer` no longer matches `BV`).
- Large `priceCents` integer overflow and null city/attribute handling when parsing Marktplaats JSON.
- Thread-safety and atomic writes across `SearchList`, `SearchScheduler`, `RepairHints`, and `ListingHistory`, plus multi-search price-drop tracking.

## [1.0.0] - 2026-11-19

### Added
- Marktplaats polling loop with adaptive scheduling, night hours, and automatic backoff.
- Full Telegram bot with `/menu`, interactive search adding/editing, URL paste, natural quick-add, watchlist, blocklist, flip profit tracker, starter packs, snooze, morning digest, and multi-user `/invite` + `/backup`.
- Listing page scraping (full description, views, saves, bids, seller age/rating), repair hints (`hints.txt`), 30-day listing history, `/stale` deals, and optional Vision AI grading (`/ai`).

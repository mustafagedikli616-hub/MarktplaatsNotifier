# 📖 How it works (code guide)

A reminder of what every part of the code does, written for future me.
Method names are used instead of line numbers, because line numbers change all the time. Use **Ctrl+F** in Rider to jump to them.

---

## The big picture: the journey of one listing

```
Marktplaats ──▶ Program.cs asks for the newest listings of one search
                     │
                     ▼
              Listing.FromJson()   fills in a "form" with title, price, city...
                     │
                     ▼
              Seen before? ──yes──▶ skip  (seen.txt)
                     │ no
                     ▼
              First round / new search? ──yes──▶ remember it, but don't send
                     │ no
                     ▼
              Filters: wanted ad? skip word? too expensive? too far for pickup?
                     │ all fine
                     ▼
              Super deal? Urgent search? ──▶ alert = "SUPER DEAL" / "URGENT" / ""
                     │
                     ▼
              Telegram.SendListing()  ──▶ 📱 your phone
```

---

## The files

| File | What it is | Analogy |
|---|---|---|
| `Program.cs` | The start of the app and the main loop. Ties everything together. | The **boss** who gives the orders |
| `Listing.cs` | One Marktplaats listing: title, price, city, photo... | A **filled-in form** |
| `SearchList.cs` | All your searches with their settings. Reads and writes `searches.txt`. | Your **address book** of searches |
| `Filters.cs` | Small yes/no checks: is it night? is it a super deal? | A **toolbox** |
| `Telegram.cs` | Sends messages and listings to your phone. | The **mail carrier** |
| `searches.txt` | Your searches, one per line. | The paper version of the address book |
| `secrets.txt` | Token, chat ID, postcode, pickup km (4 lines). Never on GitHub. | Your **house keys** |
| `seen.txt` | IDs of every listing the bot has already seen. Never on GitHub. | A **guest list** at the door |

---

## Program.cs, from top to bottom

`Program.cs` uses *top-level statements*: there's no `Main` method, the code simply runs from top to bottom.

1. **Crash catcher** (`UnhandledException`): if something goes really wrong, show the error and wait for Enter instead of the window closing at once.
2. **`StopWithError`**: a small helper that shows a message and stops the app nicely.
3. **`HttpClient`**: the one "browser" used for all internet requests. The `User-Agent` makes it look like a normal browser.
4. **Load the searches**: `searchList.Load()` reads `searches.txt`. If a line is wrong, it stops with a clear message.
5. **First-time setup**: if `secrets.txt` doesn't exist, ask for the token and find the chat ID.
6. **Postcode setup**: if `secrets.txt` has fewer than 4 lines, ask for the postcode and pickup km. This is a separate check, so people who already had a `secrets.txt` with 2 lines get asked too.
7. **Read `seen.txt`** into the `seenIds` HashSet.
8. **Telegram setup**: the category buttons for `/add`, the `/` command menu (`setMyCommands`), and the "Bot started" message.
9. **The `/add` conversation**: `StartAdd`, `HandleAddStep` and `CheckCommands` (see below).
10. **The main loop** (`while (true)`), forever:
    - Start a `Stopwatch`.
    - For **every search**: ask Marktplaats, go through the listings, send the good ones, then `CheckCommands()` and wait 1 second.
    - Print `Round done in ... seconds`.
    - Rest for 15 × 2 seconds, checking commands in between, then start again.

### Why 1 second between searches?
Marktplaats blocks you (error **429** "too many requests") if you ask too often. `CheckCommands()` already takes about 1 second and talks to Telegram, not Marktplaats, so with `Task.Delay(1000)` there are about 2 seconds between two Marktplaats requests. A round takes about **3 seconds per search**.

### Why is the first round quiet?
After every start, all current listings are added to `seen.txt` without sending them. Otherwise you'd get hundreds of old listings at once. A search added with `/add` is also quiet for its first check (`NewSearches`).

---

## The Telegram commands

`CheckCommands()` asks Telegram "any new messages?" (`getUpdates`).

- **`lastUpdateId`** remembers the last message that was handled, so the same message is never handled twice.
- **Only you can control the bot**: messages from another chat ID are ignored.
- Any message starting with `/` cancels a half-finished `/add`.

### `/add` is a conversation in steps
`addStep` remembers where you are, like a form you fill in one field at a time:

| `addStep` | The bot is waiting for... | Handled in |
|---|---|---|
| `""` | nothing, normal mode | |
| `"search"` | the search words | `HandleAddStep` → `StartAdd` |
| `"price"` | the max price | `HandleAddStep` |
| `"category"` | a category button or number | `HandleAddStep` → `searchList.Add()` |

`pendingSearch` and `pendingPrice` hold the answers until the last step.
Quick version: `/add rtx 3070;140;322` skips the conversation.

---

## Listing.cs: the form

A **class with properties**. Every listing gets its own object.

- **`FromJson(JsonElement json)`** is `static`: you call it on the class, not on a listing (`Listing.FromJson(...)`). It takes a blank form (`new Listing()`), fills it in from the Marktplaats data and returns it.
- **`PriceType`** is the price type from Marktplaats:

| `PriceType` | You see | Real price known? |
|---|---|---|
| `FIXED` | €70 | ✅ |
| `MIN_BID` | Bieden vanaf €5 | ❌ only the minimum |
| `FAST_BID` | Bieden | ❌ |
| `SEE_DESCRIPTION` | Zie omschrijving | ❌ |
| `FREE` | Gratis | €0 |

- **`DistanceKm`** starts at `-1`, which means "unknown".
- **`CanShip`** is `true` when the listing's delivery option contains "Verzenden".

---

## SearchList.cs: the address book

Every search is stored in **several dictionaries**, all with the search words as the key:

| Property | Type | Example value for `"defect monitor"` |
|---|---|---|
| `Prices` | `Dictionary<string, int>` | `100` |
| `Categories` | `Dictionary<string, string>` | `"322"` (or `""` = all of Marktplaats) |
| `SkipWords` | `Dictionary<string, string[]>` | `["barst", "gebarsten", "strepen", "lijnen", "waterschade"]` |
| `UrgentSearches` | `HashSet<string>` | contains it only if the line ends with `urgent` |
| `NewSearches` | `HashSet<string>` | searches just added with `/add` (quiet first check) |

⚠️ **Golden rule:** when you add or remove a search, update **all** of them, in `Load()`, `Add()` and `Remove()`. If a search is in `Prices` but missing from `SkipWords`, the bot crashes with a `KeyNotFoundException`.

| Method | What it does | Returns |
|---|---|---|
| `Load()` | Reads `searches.txt`, checks every line, fills the dictionaries. Creates the file if it's missing. | `""` if all is fine, otherwise an error text |
| `Add(line)` | Adds a search (from `/add`) and appends it to `searches.txt`. | A ✅/❌ message for Telegram |
| `Remove(search)` | Removes a search and rewrites `searches.txt` without it. | A 🗑️/❌ message |
| `ListText()` | Makes the text for `/list`. | The text |

### The 5 parts of a line in searches.txt
```
search words ; max price ; category ; skip words ; urgent
     [0]          [1]         [2]         [3]        [4]     ← index in parts[]
```
`parts.Length > 3` means "is there a 4th part?" (counting starts at 0).

---

## Filters.cs: the toolbox

A **`static class`**: a toolbox, not an object. You never write `new Filters()`, you just use the tools: `Filters.IsNight()`.

| Method | Question it answers | How |
|---|---|---|
| `FindBadFault(text, skipWords)` | Does the listing mention a skip word? | Splits the text into pieces at `. , ! ? newline`. Pieces with "geen" or "zonder" are ignored, so "geen waterschade" is fine. Returns the word it found, or `""`. |
| `IsNight()` | Is it between 23:00 and 07:00? | `hour >= 23 \|\| hour < 7` |
| `IsSuperDeal(ad, maxPrice)` | Fixed price of half the max price or less? | Only `FIXED` prices count. `maxPrice / 2.0` instead of `/ 2`, so 75 / 2.0 = 37.5 and not 37. |
| `IsNotForSale(title)` | Is it a wanted/buying/repair ad? | Title contains "gezocht", "reparatie" or "inkoop". |

---

## Telegram.cs: the mail carrier

A **normal class with fields and a constructor**: an object that *remembers things*. It's made once (`new Telegram(client, token, chatId)`), and after that it knows the token and chat ID itself, so you don't have to pass them every time.

| Method | What it does |
|---|---|
| `SendText(text, keyboard = "")` | Sends a normal message. `keyboard` is optional (used for the category buttons). |
| `SendListing(ad, night, alert)` | Builds the listing message (photo, title, price, place, date, link) and sends it. |

How `SendListing` decides:
- `alert != ""` → put `🚨 <b>SUPER DEAL</b> 🚨` (or URGENT) on top.
- `night && alert == ""` → add `disable_notification=true`, so it arrives silently.
- Has a photo → `sendPhoto`, otherwise `sendMessage`.
- `HtmlEncode` on the title, so a `<` or `&` in a title doesn't break the HTML formatting.

---

## C# concepts used, in one sentence each

| Concept | Remember it as |
|---|---|
| `static class` | A toolbox: no `new`, just use the tools. |
| class with fields + constructor | An object that remembers things (like `Telegram`). |
| parameter | Something a method can't see on its own, so you hand it over. |
| `Dictionary<key, value>` | An address book: look something up by its name. |
| `HashSet<string>` | A guest list: only "is it on the list?" matters. Very fast. |
| `string[]` vs `List<string>` | Array = fixed size. List = can grow. |
| `new string[0]` | An empty list: safe to loop over (0 rounds), unlike `null`. |
| `return` early | Stop the method right here. Checks that *prevent* something go **before** the line that does it. |
| `continue` | Skip the rest of this loop round, go to the next item. |
| `async` / `await` | "Wait for the internet without freezing the app." |
| `try` / `catch` | "Try this, and if it fails, don't crash, do this instead." |
| `int.TryParse(text, out int x)` | Try to turn text into a number, without crashing if it isn't one. |
| `out _` | "I don't need the result, I only want to know if it worked." |
| `Stopwatch` | A real stopwatch: `StartNew()`, then `Elapsed`. |
| `$"...{x}..."` | Put a variable inside a string. |

---

## Cheat sheet: "I want to change..."

| I want to change... | Go to |
|---|---|
| Night hours | `Filters.IsNight()` |
| When something is a super deal | `Filters.IsSuperDeal()` |
| Which ads count as "not for sale" | `Filters.IsNotForSale()` |
| What a Telegram message looks like | `Telegram.SendListing()` |
| Time between searches | `Task.Delay(1000)` inside the `foreach` in the main loop |
| Rest time after a round | the `for (int i = 0; i < 15; i++)` loop at the end of `Program.cs` |
| The category buttons for `/add` | `categoryButtons` in `Program.cs` |
| Add a new Telegram command | `CheckCommands()`, plus `helpText` and the `commands` list for the `/` menu |
| A new part in searches.txt | `SearchList.cs` (`Load`, `Add`, `Remove`), then where it's used in `Program.cs` |
| Postcode or pickup km | lines 3 and 4 of `secrets.txt` |

---

## Lessons learned

- Don't use **Replace All** on `searches`: it also changes the string `"searches.txt"`. **Ctrl+R** replaces in the current file only.
- When moving a method: paste the whole method *including* its first line, **or** only the inside, never both (method-inside-a-method bug).
- Rider adds `namespace ...;` and random `using` lines by itself. Delete them.
- Run with `dotnet run` from the project folder, **not** Rider's ▶ button. ▶ runs from `bin/Debug`, where `searches.txt` and `secrets.txt` don't exist.
- Never put a comment after a search in `searches.txt`, only on its own `#` line.
- When changing a method's parameters, the old ones don't disappear by themselves: remove what you don't use anymore.

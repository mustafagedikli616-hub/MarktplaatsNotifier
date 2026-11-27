using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace MarktplaatsNotifier;

public class WebDashboard : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly UserSession owner;
    private readonly RepairHints hints;
    private readonly RequestLimiter limiter;
    private readonly DateTime startedAt;
    private readonly Func<UserSession, SearchRule, Task<string>> checkSearchNow;
    private readonly Func<UserSession, string, Task<PriceCheckResult>> lookupMarketPrice;
    private readonly string password;
    private readonly int port;

    private readonly object sseLock = new object();
    private readonly List<HttpListenerResponse> sseClients = new List<HttpListenerResponse>();
    private DateTime lastBroadcast = DateTime.MinValue;

    private HttpListener? listener;
    private CancellationTokenSource? cts;

    public string Url { get; private set; } = "";

    public WebDashboard(
        UserSession owner,
        RepairHints hints,
        RequestLimiter limiter,
        DateTime startedAt,
        Func<UserSession, SearchRule, Task<string>> checkSearchNow,
        Func<UserSession, string, Task<PriceCheckResult>> lookupMarketPrice)
    {
        this.owner = owner;
        this.hints = hints;
        this.limiter = limiter;
        this.startedAt = startedAt;
        this.checkSearchNow = checkSearchNow;
        this.lookupMarketPrice = lookupMarketPrice;

        password = (Environment.GetEnvironmentVariable("WEB_PASSWORD") ?? "").Trim();
        string? envPort = Environment.GetEnvironmentVariable("WEB_PORT");
        port = int.TryParse(envPort, out int parsed) && parsed > 0 && parsed <= 65535 ? parsed : 5050;
    }

    public void Start()
    {
        bool bound = password != ""
            ? (TryBind($"http://+:{port}/") || TryBind($"http://localhost:{port}/"))
            : TryBind($"http://localhost:{port}/");

        if (!bound)
        {
            BotLog.Write($"Couldn't start Web UI on port {port} (port may already be in use).");
            return;
        }

        Url = $"http://localhost:{port}";
        cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(cts.Token));
        BotLog.Write(password != ""
            ? $"Web UI: {Url} (password protected)"
            : $"Web UI: {Url} (localhost only; set WEB_PASSWORD to allow LAN/remote access)");
    }

    private bool TryBind(string prefix)
    {
        HttpListener candidate = new HttpListener();
        candidate.Prefixes.Add(prefix);
        try
        {
            candidate.Start();
            listener = candidate;
            return true;
        }
        catch
        {
            candidate.Close();
            return false;
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener != null && listener.IsListening)
        {
            try
            {
                HttpListenerContext ctx = await listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(ctx), ct);
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                BotLog.Write($"Web UI error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        HttpListenerRequest req = ctx.Request;
        HttpListenerResponse res = ctx.Response;

        try
        {
            if (password != "" && !IsAuthorized(req))
            {
                res.StatusCode = 401;
                res.AddHeader("WWW-Authenticate", "Basic realm=\"Marktplaats Notifier\"");
                await WriteTextAsync(res, "Unauthorized", "text/plain");
                return;
            }

            string path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/');
            if (path == "")
            {
                path = "/";
            }

            string method = req.HttpMethod.ToUpperInvariant();

            if (method == "GET" && (path == "/" || path == "/index.html"))
            {
                await WriteTextAsync(res, LoadIndexHtml(), "text/html; charset=utf-8");
                return;
            }

            if (method == "GET" && path == "/api/events")
            {
                await HandleSseClientAsync(res, cts?.Token ?? CancellationToken.None);
                return;
            }

            if (method == "POST" && path == "/api/price-check")
            {
                using JsonDocument body = await ReadJsonAsync(req);
                string query = GetStr(body.RootElement, "query").Trim();
                if (query == "")
                {
                    res.StatusCode = 400;
                    await WriteJsonAsync(res, new { error = "Query is required" });
                    return;
                }

                PriceCheckResult result = await lookupMarketPrice(owner, query);
                await WriteJsonAsync(res, new
                {
                    result.Query,
                    result.SampleCount,
                    result.MinPrice,
                    result.MedianPrice,
                    result.MaxPrice,
                    result.SuggestedBrokenBuy,
                    result.SuggestedWorkingBuy,
                    html = PriceLookup.FormatHtml(result).Replace("\n", "<br>")
                });
                return;
            }

            if (method == "GET" && path == "/api/state")
            {
                await WriteJsonAsync(res, BuildStatePayload());
                return;
            }

            if (method == "POST" && path == "/api/pause")
            {
                owner.Paused = true;
                owner.PausedUntil = null;
                await WriteJsonAsync(res, new { ok = true });
                return;
            }

            if (method == "POST" && path == "/api/resume")
            {
                owner.Paused = false;
                owner.PausedUntil = null;
                await WriteJsonAsync(res, new { ok = true });
                return;
            }

            if (method == "POST" && path == "/api/snooze")
            {
                using JsonDocument body = await ReadJsonAsync(req);
                int minutes = body.RootElement.TryGetProperty("minutes", out JsonElement mEl) &&
                              mEl.ValueKind == JsonValueKind.Number && mEl.TryGetInt32(out int m)
                    ? Math.Clamp(m, 1, 10080)
                    : 60;
                owner.Paused = true;
                owner.PausedUntil = DateTime.Now.AddMinutes(minutes);
                await WriteJsonAsync(res, new { ok = true, pausedUntil = owner.PausedUntil.Value.ToString("HH:mm") });
                return;
            }

            if (method == "POST" && path == "/api/searches")
            {
                using JsonDocument body = await ReadJsonAsync(req);
                JsonElement root = body.RootElement;
                string rawQuery = GetStr(root, "query");
                int maxPrice = GetInt(root, "maxPrice", 50);
                string category = GetStr(root, "category");
                int subCategory = GetInt(root, "subCategory", 0);
                bool urgent = GetBool(root, "urgent", false);
                bool defect = GetBool(root, "defect", false);
                bool vinted = GetBool(root, "vinted", false);
                bool tweedehands = GetBool(root, "tweedehands", false);
                string[] skipWords = GetStringArray(root, "skipWords");

                if (MarktplaatsUrlParser.IsMarktplaatsSearchUrl(rawQuery) &&
                    MarktplaatsUrlParser.TryParse(rawQuery, out ParsedMarktplaatsUrl? parsed, out _))
                {
                    rawQuery = parsed!.Query;
                    if (parsed.MaxPrice.HasValue)
                    {
                        maxPrice = parsed.MaxPrice.Value;
                    }
                    if (parsed.Category != "")
                    {
                        category = parsed.Category;
                        subCategory = parsed.SubCategory;
                    }
                }

                if (string.IsNullOrWhiteSpace(rawQuery))
                {
                    res.StatusCode = 400;
                    await WriteJsonAsync(res, new { ok = false, error = "Query is required" });
                    return;
                }

                SearchRule rule = new SearchRule
                {
                    Query = rawQuery.Trim(),
                    MaxPrice = Math.Max(0, maxPrice),
                    Category = category.Trim(),
                    SubCategory = Math.Max(0, subCategory),
                    SkipWords = skipWords,
                    Urgent = urgent,
                    Defect = defect,
                    Vinted = vinted,
                    Tweedehands = tweedehands
                };

                string msg = owner.SearchList.AddRule(rule);
                await WriteJsonAsync(res, new { ok = msg.StartsWith("✅"), message = msg, id = rule.Id });
                return;
            }

            if (path.StartsWith("/api/searches/"))
            {
                string[] segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segs.Length >= 3 && int.TryParse(segs[2], out int searchId))
                {
                    SearchRule? existing = owner.SearchList.FindById(searchId);
                    if (existing == null)
                    {
                        res.StatusCode = 404;
                        await WriteJsonAsync(res, new { error = "Search not found" });
                        return;
                    }

                    if (segs.Length == 4 && segs[3] == "check" && method == "POST")
                    {
                        string html = await checkSearchNow(owner, existing);
                        await WriteJsonAsync(res, new { html = html.Replace("\n", "<br>") });
                        return;
                    }

                    if (method == "PUT")
                    {
                        using JsonDocument body = await ReadJsonAsync(req);
                        JsonElement root = body.RootElement;
                        existing.MaxPrice = Math.Max(0, GetInt(root, "maxPrice", existing.MaxPrice));
                        existing.Category = GetStr(root, "category", existing.Category);
                        existing.SubCategory = Math.Max(0, GetInt(root, "subCategory", existing.SubCategory));
                        existing.Urgent = GetBool(root, "urgent", existing.Urgent);
                        existing.Defect = GetBool(root, "defect", existing.Defect);
                        existing.Vinted = GetBool(root, "vinted", existing.Vinted);
                        existing.Tweedehands = GetBool(root, "tweedehands", existing.Tweedehands);
                        if (root.TryGetProperty("skipWords", out _))
                        {
                            existing.SkipWords = GetStringArray(root, "skipWords");
                        }

                        owner.SearchList.UpdateRule(existing);
                        await WriteJsonAsync(res, new { ok = true });
                        return;
                    }

                    if (method == "DELETE")
                    {
                        owner.SearchList.Remove(existing.Query, out string removedKey);
                        if (removedKey != "")
                        {
                            owner.NewestIdPerSearch.Remove(removedKey);
                            owner.Scheduler.Remove(removedKey);
                            owner.Stats.Remove(removedKey);
                        }

                        await WriteJsonAsync(res, new { ok = true });
                        return;
                    }
                }
            }

            if (method == "POST" && path == "/api/watchlist")
            {
                using JsonDocument body = await ReadJsonAsync(req);
                JsonElement root = body.RootElement;
                string id = GetStr(root, "id");
                if (id == "")
                {
                    id = GetStr(root, "listingId");
                }

                if (id != "")
                {
                    owner.Watchlist.AddOrUpdate(new WatchItem
                    {
                        Id = id,
                        Title = GetStr(root, "title", $"Listing {id}"),
                        Link = GetStr(root, "link", $"https://link.marktplaats.nl/{id}"),
                        LastPrice = GetDouble(root, "price", GetDouble(root, "lastPrice", 0)),
                        TargetPrice = GetDouble(root, "maxPrice", GetDouble(root, "targetPrice", 0)),
                        AddedAt = DateTime.Now,
                        LastCheckedAt = DateTime.MinValue
                    });
                }

                await WriteJsonAsync(res, new { ok = true });
                return;
            }

            if (path.StartsWith("/api/watchlist/"))
            {
                string id = Uri.UnescapeDataString(path.Substring("/api/watchlist/".Length));
                if (method == "PUT")
                {
                    WatchItem? item = owner.Watchlist.Get(id);
                    if (item != null)
                    {
                        using JsonDocument body = await ReadJsonAsync(req);
                        item.TargetPrice = Math.Max(0, GetDouble(body.RootElement, "targetPrice", 0));
                        owner.Watchlist.Save();
                    }

                    await WriteJsonAsync(res, new { ok = true });
                    return;
                }

                if (method == "DELETE")
                {
                    owner.Watchlist.Remove(id);
                    await WriteJsonAsync(res, new { ok = true });
                    return;
                }
            }

            if (method == "POST" && path == "/api/flips")
            {
                using JsonDocument body = await ReadJsonAsync(req);
                string name = GetStr(body.RootElement, "name");
                double buyPrice = Math.Max(0, GetDouble(body.RootElement, "buyPrice", 0));
                FlipItem created = owner.FlipTracker.AddPurchase(name, buyPrice);
                await WriteJsonAsync(res, new { ok = true, id = created.Id });
                return;
            }

            if (path.StartsWith("/api/flips/"))
            {
                string[] segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segs.Length >= 3 && int.TryParse(segs[2], out int flipId))
                {
                    if (segs.Length == 4 && segs[3] == "parts" && method == "POST")
                    {
                        using JsonDocument body = await ReadJsonAsync(req);
                        double cost = Math.Max(0, GetDouble(body.RootElement, "cost", 0));
                        owner.FlipTracker.AddParts($"#{flipId}", cost);
                        await WriteJsonAsync(res, new { ok = true });
                        return;
                    }

                    if (segs.Length == 4 && segs[3] == "sold" && method == "POST")
                    {
                        using JsonDocument body = await ReadJsonAsync(req);
                        double sellPrice = Math.Max(0, GetDouble(body.RootElement, "sellPrice", 0));
                        owner.FlipTracker.MarkSold($"#{flipId}", sellPrice);
                        await WriteJsonAsync(res, new { ok = true });
                        return;
                    }

                    if (method == "DELETE")
                    {
                        owner.FlipTracker.Delete(flipId);
                        await WriteJsonAsync(res, new { ok = true });
                        return;
                    }
                }
            }

            if (method == "POST" && path == "/api/settings")
            {
                using JsonDocument body = await ReadJsonAsync(req);
                JsonElement root = body.RootElement;
                string newPostcode = GetStr(root, "postcode").ToUpperInvariant().Replace(" ", "");
                if (newPostcode.Length == 6)
                {
                    owner.Postcode = newPostcode;
                }

                int newKm = GetInt(root, "pickupKm", owner.PickupKm);
                if (newKm >= 0)
                {
                    owner.PickupKm = newKm;
                }
                owner.SaveProfile();

                if (root.TryGetProperty("settings", out JsonElement sEl) && sEl.ValueKind == JsonValueKind.Object)
                {
                    owner.Settings.Language = GetStr(sEl, "language", owner.Settings.Language);
                    owner.Settings.QuietNightsEnabled = GetBool(sEl, "quietNightsEnabled", owner.Settings.QuietNightsEnabled);
                    owner.Settings.NightStartHour = GetInt(sEl, "nightStartHour", owner.Settings.NightStartHour);
                    owner.Settings.NightEndHour = GetInt(sEl, "nightEndHour", owner.Settings.NightEndHour);
                    owner.Settings.MorningDigestHour = owner.Settings.NightEndHour;
                    owner.Settings.MorningDigestEnabled = GetBool(sEl, "morningDigestEnabled", owner.Settings.MorningDigestEnabled);
                    owner.Settings.SkipLikelyBusinesses = GetBool(sEl, "skipLikelyBusinesses", owner.Settings.SkipLikelyBusinesses);
                    owner.Settings.SkipHighDescriptionPrice = GetBool(sEl, "skipHighDescriptionPrice", owner.Settings.SkipHighDescriptionPrice);
                    owner.Settings.FastCategoryMode = GetBool(sEl, "fastCategoryMode", owner.Settings.FastCategoryMode);
                    owner.Settings.EnableTweakers = GetBool(sEl, "enableTweakers", owner.Settings.EnableTweakers);
                    owner.Settings.EnableTweedehands = GetBool(sEl, "enableTweedehands", owner.Settings.EnableTweedehands);
                    owner.Settings.EnableCoolblue = GetBool(sEl, "enableCoolblue", owner.Settings.EnableCoolblue);
                    owner.Settings.EnableVinted = GetBool(sEl, "enableVinted", owner.Settings.EnableVinted);
                    owner.Settings.AiEnabled = GetBool(sEl, "aiEnabled", owner.Settings.AiEnabled);
                    owner.Settings.AiProvider = GetStr(sEl, "aiProvider", owner.Settings.AiProvider);
                    owner.Settings.AiModel = GetStr(sEl, "aiModel", owner.Settings.AiModel);
                    owner.Settings.AiDailyLimit = GetInt(sEl, "aiDailyLimit", owner.Settings.AiDailyLimit);
                    owner.Settings.AiAutoSkip = GetBool(sEl, "aiAutoSkip", owner.Settings.AiAutoSkip);
                    owner.Settings.Save();
                }

                string newKey = GetStr(root, "apiKey");
                if (newKey != "")
                {
                    owner.AiGrader.SaveApiKey(newKey);
                }

                await WriteJsonAsync(res, new { ok = true });
                return;
            }

            if (method == "POST" && path == "/api/blocklist/words")
            {
                using JsonDocument body = await ReadJsonAsync(req);
                owner.Blocklist.AddGlobalSkipWords(GetStringArray(body.RootElement, "words"));
                await WriteJsonAsync(res, new { ok = true });
                return;
            }

            if (method == "DELETE" && path.StartsWith("/api/blocklist/words/"))
            {
                string word = Uri.UnescapeDataString(path.Substring("/api/blocklist/words/".Length));
                owner.Blocklist.RemoveGlobalSkipWord(word);
                await WriteJsonAsync(res, new { ok = true });
                return;
            }

            if (method == "POST" && path == "/api/blocklist/sellers")
            {
                using JsonDocument body = await ReadJsonAsync(req);
                owner.Blocklist.AddSeller(GetStr(body.RootElement, "seller"));
                await WriteJsonAsync(res, new { ok = true });
                return;
            }

            if (method == "DELETE" && path.StartsWith("/api/blocklist/sellers/"))
            {
                string seller = Uri.UnescapeDataString(path.Substring("/api/blocklist/sellers/".Length));
                owner.Blocklist.RemoveSeller(seller);
                await WriteJsonAsync(res, new { ok = true });
                return;
            }

            if (method == "POST" && path == "/api/hints")
            {
                using JsonDocument body = await ReadJsonAsync(req);
                string text = GetStr(body.RootElement, "text");
                File.WriteAllText(AppFiles.PathFor("hints.txt"), text);
                hints.Load();
                await WriteJsonAsync(res, new { ok = true });
                return;
            }

            if (method == "GET" && path == "/api/backup")
            {
                byte[] zip = owner.CreateBackupZip();
                res.ContentType = "application/zip";
                res.AddHeader("Content-Disposition", $"attachment; filename=\"marktplaats-backup-{DateTime.Now:yyyy-MM-dd}.zip\"");
                res.ContentLength64 = zip.Length;
                await res.OutputStream.WriteAsync(zip);
                res.Close();
                return;
            }

            if (method == "POST" && path == "/api/restore")
            {
                string fileName = (req.Headers["X-Filename"] ?? "").Trim().ToLowerInvariant();
                using MemoryStream ms = new MemoryStream();
                await req.InputStream.CopyToAsync(ms);
                byte[] bytes = ms.ToArray();

                if (fileName == "searches.txt")
                {
                    string target = Path.Combine(owner.DataDir, "searches.txt");
                    string backup = target + ".before_upload";
                    if (File.Exists(target))
                    {
                        File.Copy(target, backup, overwrite: true);
                    }

                    await File.WriteAllBytesAsync(target, bytes);
                    string err = owner.SearchList.Load();
                    if (err != "")
                    {
                        if (File.Exists(backup))
                        {
                            File.Move(backup, target, overwrite: true);
                            owner.SearchList.Load();
                        }

                        res.StatusCode = 400;
                        await WriteTextAsync(res, err, "text/plain");
                        return;
                    }

                    if (File.Exists(backup))
                    {
                        File.Delete(backup);
                    }

                    await WriteJsonAsync(res, new { ok = true });
                    return;
                }

                if (owner.RestoreFromBackupZip(bytes, out List<string> restored, out string zipErr))
                {
                    if (restored.Contains("hints.txt", StringComparer.OrdinalIgnoreCase))
                    {
                        hints.Load();
                    }

                    await WriteJsonAsync(res, new { ok = true, restored });
                }
                else
                {
                    res.StatusCode = 400;
                    await WriteTextAsync(res, zipErr, "text/plain");
                }

                return;
            }

            res.StatusCode = 404;
            await WriteJsonAsync(res, new { error = "Not found" });
        }
        catch (Exception ex)
        {
            try
            {
                res.StatusCode = 500;
                await WriteTextAsync(res, ex.Message, "text/plain");
            }
            catch
            {
            }
        }
    }

    private object BuildStatePayload()
    {
        TimeSpan running = DateTime.Now - startedAt;
        string hintsPath = AppFiles.PathFor("hints.txt");
        string hintsText = File.Exists(hintsPath) ? File.ReadAllText(hintsPath) : "";

        var searches = owner.SearchList.GetRulesSnapshot().Select(r =>
        {
            SearchStatEntry? st = owner.Stats.GetFor(r.Query);
            return new
            {
                r.Id,
                r.Query,
                r.MaxPrice,
                r.Category,
                r.SubCategory,
                r.SkipWords,
                r.Urgent,
                r.Defect,
                r.Vinted,
                r.Tweedehands,
                sent = st?.Sent ?? 0,
                skipped = st?.FilteredOut ?? 0
            };
        }).ToList();

        var feed = owner.Feed.GetRecent("all", 80).Select(e => new
        {
            timestamp = e.Timestamp.ToString("HH:mm:ss"),
            e.Kind,
            e.ListingId,
            e.Source,
            e.Title,
            e.PriceText,
            e.Price,
            e.OldPrice,
            e.TypicalPct,
            e.Search,
            e.Alert,
            e.Reason,
            e.AiVerdict,
            e.Seller,
            e.SellerRating,
            e.Verified,
            e.City,
            e.Photo,
            e.Link
        }).ToList();

        var stale = owner.History.FindStaleListings(owner.SearchList, owner.Blocklist, 7)
            .Where(x => !x.Entry.Id.Contains('-') && !x.Entry.Id.StartsWith("tw", StringComparison.OrdinalIgnoreCase))
            .Select(x => new
        {
            id = x.Entry.Id,
            title = x.Entry.Title,
            price = x.Entry.Price,
            maxPrice = x.MaxPrice,
            daysOld = x.DaysOld,
            search = x.Entry.Search,
            link = $"https://link.marktplaats.nl/{x.Entry.Id}"
        }).ToList();

        var flips = owner.FlipTracker.AllItems.Select(f => new
        {
            f.Id,
            f.Name,
            f.BuyPrice,
            f.PartsCost,
            f.SellPrice,
            boughtAt = f.BoughtAt.ToString("yyyy-MM-dd"),
            soldAt = f.SoldAt?.ToString("yyyy-MM-dd")
        }).ToList();

        bool isOllama = owner.Settings.AiProvider.Equals("ollama", StringComparison.OrdinalIgnoreCase);

        return new
        {
            status = new
            {
                paused = owner.Paused,
                pausedUntil = owner.PausedUntil?.ToString("HH:mm"),
                postcode = owner.Postcode,
                pickupKm = owner.PickupKm,
                checksToday = owner.Digest.ChecksToday,
                sentToday = owner.Digest.SentTodayCount,
                skippedToday = owner.Digest.FilteredOutToday,
                delayMs = limiter.DelayMs,
                historyCount = owner.History.Count,
                uptime = $"{(int)running.TotalHours}h {running.Minutes}m",
                aiCallsToday = owner.AiGrader.CallsToday,
                aiKeySet = isOllama || owner.AiGrader.GetApiKey(owner.Settings.AiProvider) != "",
                skipBreakdown = owner.Feed.GetSkipBreakdown()
            },
            feed,
            logs = BotLog.GetRecentLines(25),
            searches,
            watchlist = owner.Watchlist.Items.OrderByDescending(w => w.AddedAt).ToList(),
            stale,
            flips,
            settings = owner.Settings,
            blocklist = new
            {
                globalSkipWords = owner.Blocklist.GlobalSkipWords,
                blockedSellers = owner.Blocklist.BlockedSellers
            },
            hints = hintsText
        };
    }

    private bool IsAuthorized(HttpListenerRequest req)
    {
        string? header = req.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Substring(6).Trim()));
            int sep = decoded.IndexOf(':');
            string providedPass = sep >= 0 ? decoded.Substring(sep + 1) : decoded;
            return providedPass == password;
        }
        catch
        {
            return false;
        }
    }

    private static string LoadIndexHtml()
    {
        string diskPath = Path.Combine(AppFiles.DataDirectory, "wwwroot", "index.html");
        if (File.Exists(diskPath))
        {
            return File.ReadAllText(diskPath);
        }

        string basePath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
        if (File.Exists(basePath))
        {
            return File.ReadAllText(basePath);
        }

        Assembly asm = Assembly.GetExecutingAssembly();
        using Stream? stream = asm.GetManifestResourceStream("MarktplaatsNotifier.wwwroot.index.html");
        if (stream != null)
        {
            using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        return "<h1>Marktplaats Notifier</h1><p>index.html not found.</p>";
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpListenerRequest req)
    {
        using StreamReader reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
        string raw = await reader.ReadToEndAsync();
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse res, object payload)
    {
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        await WriteTextAsync(res, json, "application/json; charset=utf-8");
    }

    private static async Task WriteTextAsync(HttpListenerResponse res, string content, string contentType)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        res.ContentType = contentType;
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private static string GetStr(JsonElement el, string name, string fallback = "")
    {
        return el.TryGetProperty(name, out JsonElement prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? fallback
            : fallback;
    }

    private static int GetInt(JsonElement el, string name, int fallback)
    {
        return el.TryGetProperty(name, out JsonElement prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int v)
            ? v
            : fallback;
    }

    private static double GetDouble(JsonElement el, string name, double fallback)
    {
        return el.TryGetProperty(name, out JsonElement prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out double v)
            ? v
            : fallback;
    }

    private static bool GetBool(JsonElement el, string name, bool fallback)
    {
        return el.TryGetProperty(name, out JsonElement prop) &&
               (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False)
            ? prop.GetBoolean()
            : fallback;
    }

    private static string[] GetStringArray(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out JsonElement prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        List<string> list = new List<string>();
        foreach (JsonElement item in prop.EnumerateArray())
        {
            string s = (item.GetString() ?? "").Trim().ToLowerInvariant();
            if (s != "")
            {
                list.Add(s);
            }
        }

        return list.ToArray();
    }

    public void BroadcastUpdate()
    {
        lock (sseLock)
        {
            if (sseClients.Count == 0 || DateTime.Now - lastBroadcast < TimeSpan.FromMilliseconds(400))
            {
                return;
            }
            lastBroadcast = DateTime.Now;
        }

        byte[] payload = Encoding.UTF8.GetBytes("event: update\ndata: {}\n\n");
        List<HttpListenerResponse> snapshot;
        lock (sseLock)
        {
            snapshot = sseClients.ToList();
        }

        List<HttpListenerResponse> dead = new List<HttpListenerResponse>();
        foreach (HttpListenerResponse client in snapshot)
        {
            try
            {
                lock (sseLock)
                {
                    if (!sseClients.Contains(client))
                    {
                        continue;
                    }
                    client.OutputStream.Write(payload, 0, payload.Length);
                    client.OutputStream.Flush();
                }
            }
            catch
            {
                dead.Add(client);
            }
        }

        if (dead.Count > 0)
        {
            lock (sseLock)
            {
                foreach (HttpListenerResponse d in dead)
                {
                    sseClients.Remove(d);
                    try { d.Close(); } catch { }
                }
            }
        }
    }

    private async Task HandleSseClientAsync(HttpListenerResponse res, CancellationToken ct)
    {
        res.ContentType = "text/event-stream; charset=utf-8";
        res.Headers.Add("Cache-Control", "no-cache");
        res.Headers.Add("X-Accel-Buffering", "no");
        res.SendChunked = true;

        byte[] hello = Encoding.UTF8.GetBytes(": connected\n\n");
        byte[] ping = Encoding.UTF8.GetBytes(": ping\n\n");
        try
        {
            lock (sseLock)
            {
                res.OutputStream.Write(hello, 0, hello.Length);
                res.OutputStream.Flush();
                sseClients.Add(res);
            }

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
                lock (sseLock)
                {
                    res.OutputStream.Write(ping, 0, ping.Length);
                    res.OutputStream.Flush();
                }
            }
        }
        catch
        {
        }
        finally
        {
            lock (sseLock)
            {
                sseClients.Remove(res);
                try { res.Close(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        cts?.Cancel();
        lock (sseLock)
        {
            foreach (HttpListenerResponse c in sseClients)
            {
                try { c.Close(); } catch { }
            }
            sseClients.Clear();
        }
        try
        {
            listener?.Close();
        }
        catch
        {
        }
    }
}

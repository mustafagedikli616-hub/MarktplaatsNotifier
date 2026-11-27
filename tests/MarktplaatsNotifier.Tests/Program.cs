using System.Net;
using System.Text;
using System.Text.Json;
using MarktplaatsNotifier;

int passed = 0;
int failed = 0;

void Check(string name, bool condition, string detail = "")
{
    if (condition)
    {
        passed++;
        Console.WriteLine($"  [PASS] {name}");
    }
    else
    {
        failed++;
        Console.WriteLine($"  [FAIL] {name}" + (detail != "" ? $" -> {detail}" : ""));
    }
}

string repoRoot = FindRepoRoot();
string simDir = Path.Combine(Path.GetTempPath(), "mp_tests_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(simDir);

try
{
    Console.WriteLine("=== 1. SearchList & Real searches.txt ===");
    string realSearchesPath = Path.Combine(repoRoot, "searches.txt");
    if (File.Exists(realSearchesPath))
    {
        File.Copy(realSearchesPath, Path.Combine(simDir, "searches.txt"));
    }
    SearchList sl = new SearchList(simDir);
    Check("Loads searches.txt cleanly", sl.Load() == "");
    Check("Add full 5-field line preserves skipWords + urgent + subcategory",
        sl.Add("test item;99;356/2954;barst,water;urgent").StartsWith("✅") &&
        sl.Rules.TryGetValue("test item", out var r) &&
        r.SubCategory == 2954 && r.Urgent && !r.Defect && r.SkipWords.Length == 2);
    Check("Add urgent,defect line round-trips both flags on disk",
        sl.Add("defect gpu;120;322;water;urgent,defect").StartsWith("✅") &&
        sl.Rules.TryGetValue("defect gpu", out var dr) && dr.Urgent && dr.Defect &&
        File.ReadAllText(Path.Combine(simDir, "searches.txt")).Contains("defect gpu;120;322;water;urgent,defect"));
    Check("Rejects unknown 5th-column flag", sl.Add("badflag;50;322;;banana").StartsWith("❌"));
    Check("Rejects /2954 without main category", sl.Add("bad;50;/2954").StartsWith("❌"));
    Check("Rejects negative price", sl.Add("neg;-10;322").StartsWith("❌"));
    Check("Case-insensitive duplicate check", sl.Add("TEST ITEM;50").StartsWith("⚠️"));
    Check("Case-insensitive Remove", sl.Remove("TEST ITEM", out string remKey).StartsWith("🗑️") && remKey == "test item");

    Console.WriteLine("\n=== 2. Filters (Negation, MatchesSearch, Description Price) ===");
    string[] monSkip = { "barst", "gebarsten", "strepen", "waterschade" };
    Check("Catches barst after 'geen beeld'", Filters.FindBadFault("Monitor geen beeld met barst in scherm", monSkip) == "barst");
    Check("Catches barst after 'zonder voet'", Filters.FindBadFault("Monitor zonder voet maar barst", monSkip) == "barst");
    Check("Catches barst after 'bijzonder'", Filters.FindBadFault("Bijzonder mooie monitor met barst", monSkip) == "barst");
    Check("Ignores 'geen waterschade'", Filters.FindBadFault("Geen waterschade, werkt prima", monSkip) == "");
    Check("Ignores 'zonder barst'", Filters.FindBadFault("Scherm zonder barst of strepen", monSkip) == "");
    Check("MatchesSearch matches word starts (monitor -> monitoren)",
        Filters.MatchesSearch("defect monitor", new Listing { Title = "Defecte monitoren te koop" }));
    Check("MatchesSearch rejects substring inside word (ram != frame)",
        !Filters.MatchesSearch("ram", new Listing { Title = "Mooi fiets frame" }));
    Check("MatchesSearch matches squashed model numbers (rtx 3070 <-> RTX3070)",
        Filters.MatchesSearch("rtx 3070", new Listing { Title = "MSI RTX3070 Gaming X" }));
    Check("ExtractMentionedPrice reads Vraagprijs €220", Filters.ExtractMentionedPrice("Goede monitor. Vraagprijs €220.") == 220);
    Check("ExtractMentionedPrice ignores nieuwprijs €500", Filters.ExtractMentionedPrice("Nieuwprijs €500, bieden graag") == 0);
    Check("FindFaultWord catches broken condition & fault words, ignores negated",
        Filters.FindFaultWord(new Listing { Condition = "Niet werkend", Title = "Monitor" }) == "niet werkend" &&
        Filters.FindFaultWord(new Listing { Title = "PS5 geeft geen beeld meer" }) == "geen beeld" &&
        Filters.FindFaultWord(new Listing { Title = "Monitor geen defect of barst, werkt top" }) == "");
    Check("FindOldCpu skips 4th-gen Intel / FX on PC searches, allows modern CPUs & non-PC searches",
        Filters.FindOldCpu(new Listing { Title = "Game PC i5-4570 GTX 970" }, "game pc") != "" &&
        Filters.FindOldCpu(new Listing { Title = "Desktop AMD FX-8350" }, "pc defect") != "" &&
        Filters.FindOldCpu(new Listing { Title = "Game PC Ryzen 5 5600 RTX 3060" }, "game pc") == "" &&
        Filters.FindOldCpu(new Listing { Title = "Monitor gebruikt op i5-4570" }, "defect monitor") == "");
    Check("FindScamSigns flags 0yr/0rev unverified shipping & off-platform payment phrases",
        Filters.FindScamSigns(new Listing { Price = 200, SellerYears = 0, SellerReviewCount = 0, SellerVerified = false, CanShip = true }).Contains("0 yr") &&
        Filters.FindScamSigns(new Listing { Price = 100, Description = "Alleen Tikkie vooruit, geen kopersbescherming" }).Contains("tikkie vooruit") &&
        Filters.FindScamSigns(new Listing { Price = 100, SellerYears = 5, SellerReviewCount = 20, SellerVerified = true, CanShip = true }) == "");

    Console.WriteLine("\n=== 3. BusinessFilter, RepairHints & PriceLookup ===");
    Check("BVermeer is NOT flagged as BV", BusinessFilter.Score(new Listing { SellerName = "BVermeer", Title = "Monitor", Price = 50 }) == 0);
    Check("Tech BV + cents + link scores >= 2", BusinessFilter.Score(new Listing { SellerName = "Tech BV", Title = "Monitor", Price = 49.95, HasWebsiteLink = true }) >= 2);
    Environment.SetEnvironmentVariable("MARKTPLAATS_DATA_DIR", simDir);
    RepairHints hints = new RepairHints();
    hints.Load();
    Check("RepairHints matches ps5+geen beeld", hints.Find("PS5 geeft geen beeld meer")?.Text.Contains("HDMI") == true);
    Check("RepairHints ignores 'geen stickdrift'", hints.Find("Controller heeft geen stickdrift") == null);

    PriceCheckResult priceCheck = PriceLookup.Compute("rtx 3070", new double[] { 150, 180, 200, 220, 240, 9999 }, new double[] { 190, 210 });
    Check("PriceLookup computes median and buy targets", priceCheck.MedianPrice >= 190 && priceCheck.MedianPrice <= 215 && priceCheck.SuggestedBrokenBuy > 60 && priceCheck.SuggestedWorkingBuy > 100);

    Console.WriteLine("\n=== 4. Tweakers V&A RSS Parsing & ImageHasher ===");
    string sampleRss = """
    <?xml version="1.0" encoding="UTF-8"?>
    <rss version="2.0"><channel><title>Tweakers V&amp;A</title>
      <item>
        <title>MSI GeForce RTX 3070 Ventus 2X - € 135,-</title>
        <link>https://tweakers.net/aanbod/3612345/msi-rtx-3070.html</link>
        <description>&lt;p&gt;Werkt goed, fan maakt wat lawaai.&lt;/p&gt;</description>
        <pubDate>Thu, 26 Nov 2026 18:00:00 +0100</pubDate>
      </item>
      <item>
        <title>Gevraagd: RTX 3070</title>
        <link>https://tweakers.net/aanbod/3612346/gezocht.html</link>
        <description>Gezocht</description>
      </item>
    </channel></rss>
    """;
    List<Listing> twAds = TweakersSource.ParseRss(sampleRss);
    Check("TweakersSource parses RSS and skips Gevraagd", twAds.Count == 1 && twAds[0].Id == "tw3612345" && twAds[0].Price == 135 && twAds[0].Source == "Tweakers");

    string sample2dh = """{"listings":[{"itemId":"m2470060087","title":"Gaming Monitor 144Hz","description":"Prima staat","priceInfo":{"priceCents":6500,"priceType":"FIXED"},"location":{"cityName":"Antwerpen"},"vipUrl":"/v/computers/m2470060087","attributes":[{"key":"delivery","value":"Verzenden"}]}]}""";
    List<Listing> beAds = TweedehandsSource.ParseJson(sample2dh);
    Check("TweedehandsSource parses 2dehands.be JSON", beAds.Count == 1 && beAds[0].Id == "2dh-m2470060087" && beAds[0].Price == 65 && beAds[0].Source == "2dehands.be" && beAds[0].CanShip && beAds[0].Link.StartsWith("https://www.2dehands.be/"));
    Check("TweedehandsSource allows GPUs/CPUs/RAM and rejects monitors/PCs",
        TweedehandsSource.IsEligibleRule(new SearchRule { Query = "rtx 4070", MaxPrice = 300 }) &&
        TweedehandsSource.IsEligibleRule(new SearchRule { Query = "5800x3d", MaxPrice = 180 }) &&
        TweedehandsSource.IsEligibleRule(new SearchRule { Query = "ddr5 32gb", MaxPrice = 65 }) &&
        !TweedehandsSource.IsEligibleRule(new SearchRule { Query = "defect monitor", MaxPrice = 100 }) &&
        !TweedehandsSource.IsEligibleRule(new SearchRule { Query = "game pc defect", MaxPrice = 150 }) &&
        TweedehandsSource.StrictMaxPrice(100) == 85);

    string sampleCb = """<script type="application/ld+json">{"@type":"ItemList","itemListElement":[{"productID":895125,"name":"Philips 346E2CUAE/00","image":["https://image.coolblue.nl/500x500/products/1653893"],"offers":{"price":219}}]}</script><a href="https://www.coolblue.nl/product-tweedekans/895125/3100497">Philips 346E2CUAE/00</a><div>Onbeschadigd</div><div>Nieuwprijs 219 ,- 198 ,-</div>""";
    List<Listing> cbAds = CoolblueSource.ParseHtml(sampleCb);
    Check("CoolblueSource parses Tweedekans price, retail discount, and condition", cbAds.Count == 1 && cbAds[0].Id == "cb-3100497" && cbAds[0].Price == 198 && cbAds[0].OldPrice == 219 && cbAds[0].Condition == "Onbeschadigd" && cbAds[0].Source == "Coolblue Tweedekans");
    Check("CoolblueSource rejects normal discounts and allows real price errors",
        !CoolblueSource.IsPriceErrorDeal(new Listing { Price = 198, OldPrice = 219 }, 250) &&
        CoolblueSource.IsPriceErrorDeal(new Listing { Price = 79, OldPrice = 219 }, 150));

    string sampleVinted = """<div data-testid="product-item-id-10982054445"><img src="https://images1.vinted.net/t/1.webp" alt="Vendo nintendo switch, Merk: Nintendo, Staat: Heel goed, 100.00 €, 105.70 €" data-testid="product-item-id-10982054445--image--img"/><a href="/items/10982054445-vendo-nintendo-switch?referrer=catalog" data-testid="product-item-id-10982054445--overlay-link"></a></div>""";
    List<Listing> vAds = VintedSource.ParseHtml(sampleVinted);
    Check("VintedSource parses SSR card title, price, condition, and link", vAds.Count == 1 && vAds[0].Id == "vinted-10982054445" && vAds[0].Title == "Vendo nintendo switch" && vAds[0].Price == 100 && vAds[0].Condition == "Heel goed" && vAds[0].Source == "Vinted");

    Check("ImageHasher AreSimilar matches close dHash (<= 6 bits)", ImageHasher.AreSimilar("d:000000000000000f", "d:0000000000000000", 6));
    Check("ImageHasher AreSimilar rejects distant dHash", !ImageHasher.AreSimilar("d:ffffffffffffffff", "d:0000000000000000", 6));
    byte[] fakeJpeg1 = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x01, 0x02, 0xFF, 0xDA, 0x00, 0x04, 0x00, 0x00, 0x11, 0x22, 0x33, 0x44, 0xFF, 0xD9 };
    byte[] fakeJpeg2 = new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x04, 0x99, 0x88, 0xFF, 0xDA, 0x00, 0x04, 0x00, 0x00, 0x11, 0x22, 0x33, 0x44, 0xFF, 0xD9 };
    Check("ImageHasher strips EXIF metadata on fallback JPEG scan hash", ImageHasher.ComputeHash(fakeJpeg1) == ImageHasher.ComputeHash(fakeJpeg2));

    Console.WriteLine("\n=== 5. Listing.FromJson & ListingHistory (Price drops + Photo Repost + Concurrency) ===");
    string oddJson = """
    {
      "itemId": "m2189999999",
      "title": "RTX 3070 <test> & \"quote\"",
      "description": "Werkt goed",
      "categoryId": 322,
      "priceInfo": { "priceCents": 9999999900, "priceType": "FIXED" },
      "location": { "distanceMeters": 4500, "cityName": null },
      "sellerInformation": { "sellerName": "Jan", "isVerified": true },
      "vipUrl": "/v/computers/m2189999999",
      "attributes": [ { "key": "delivery", "value": "Ophalen of Verzenden" }, { "key": "condition", "value": null } ],
      "imageUrls": [ "//img1_82.jpg", "//img2_82.jpg" ]
    }
    """;
    using (JsonDocument doc = JsonDocument.Parse(oddJson))
    {
        Listing ad = Listing.FromJson(doc.RootElement);
        Check("Handles >int.MaxValue priceCents without overflow", Math.Abs(ad.Price - 99999999.0) < 0.01);
        Check("Handles null cityName & attribute safely", ad.City == "Onbekend" && ad.Condition == "" && ad.PhotoUrls.Count == 2);
    }

    ListingHistory hist = new ListingHistory(simDir);
    Listing dropAd = new Listing { Id = "m1001", Title = "Odyssey", SellerName = "Jan", CategoryId = 322, Price = 80 };
    hist.Remember(dropAd, "searchA");
    dropAd.Price = 55;
    Check("SearchA sees drop from 80", hist.Remember(dropAd, "searchA") == 80);
    hist.MarkPriceDropChecked("m1001", "searchA");
    Check("SearchA won't re-trigger; SearchB still catches drop", hist.Remember(dropAd, "searchA") == 0 && hist.Remember(dropAd, "searchB") == 80);
    hist.MarkPriceAlerted("m1001");
    Check("SearchC won't duplicate drop", hist.Remember(dropAd, "searchC") == 0);

    // Photo-hash repost detection with a DIFFERENT title
    hist.SetPhotoHash("m1001", "d:0f0f0f0f0f0f0f0f");
    hist.GetById("m1001")!.FirstSeen = DateTime.Now.AddDays(-3);
    Listing renamedRepost = new Listing { Id = "m2002", Title = "Completely New Title", SellerName = "OtherAccount", CategoryId = 322, PhotoHash = "d:0f0f0f0f0f0f0f0e" };
    HistoryEntry? photoRepost = hist.FindRepost(renamedRepost, out bool byPhoto);
    Check("FindRepost detects reposted photo even when title & seller changed", photoRepost?.Id == "m1001" && byPhoto);

    Console.WriteLine("\n=== 6. URL Parser, Scheduler, Limiter, Telegram Mock & Live WebDashboard + SSE ===");
    Check("MarktplaatsUrlParser extracts search & shared listing URLs",
        MarktplaatsUrlParser.TryParse("https://www.marktplaats.nl/l/computers-en-software/q/rtx+3070/#PriceCentsTo:14000", out var pUrl, out _) &&
        pUrl!.Query == "rtx 3070" && pUrl.MaxPrice == 140 &&
        MarktplaatsUrlParser.TryExtractListingUrl("Monitor https://link.marktplaats.nl/m2189123456", out _, out var lId) && lId == "m2189123456");

    HttpClient mockClient = new HttpClient(new MockHandler(_ => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true,"result":{"message_id":77}}""", Encoding.UTF8, "application/json")
        })));

    UserSession owner = new UserSession("100", true, "Owner", simDir, "FAKE", "3511AB", 15, true, mockClient, mockClient);
    Check("Owner session loads cleanly", owner.LoadData() == "");

    UserManager um = new UserManager(owner, "FAKE", mockClient, mockClient, _ => { });
    string inv = um.CreateInviteCode();
    Check("Multi-user invite + join works", um.TryRedeemInvite(inv, "200", "Alice", out var alice, out _) && alice != null);

    Environment.SetEnvironmentVariable("WEB_PORT", "15058");
    Environment.SetEnvironmentVariable("WEB_PASSWORD", "");
    RequestLimiter lim = new RequestLimiter();
    lim.LogRequest();
    lim.LogRequest();
    lim.Blocked(403);
    Check("RequestLimiter tracks req/min, 403 blocks today, and 3-min cooldown",
        lim.RequestsPerMinute >= 2.0 && lim.Blocks403Today == 1 && lim.PausedUntil > DateTime.Now.AddMinutes(2));
    using WebDashboard web = new WebDashboard(
        owner, hints, lim, DateTime.Now.AddMinutes(-2),
        (_, rule) => Task.FromResult($"Checked {rule.Query}"),
        (_, q) => Task.FromResult(PriceLookup.Compute(q, new double[] { 100, 120, 140 }, Array.Empty<double>())));
    web.Start();

    using HttpClient webClient = new HttpClient { BaseAddress = new Uri("http://localhost:15058") };
    Check("GET / serves embedded index.html", (await webClient.GetStringAsync("/")).Contains("/api/events"));
    Check("GET /api/state returns live state", (await webClient.GetStringAsync("/api/state")).Contains("fastCategoryMode"));

    var pcResp = await webClient.PostAsync("/api/price-check", new StringContent("""{"query":"rtx 3070"}""", Encoding.UTF8, "application/json"));
    Check("POST /api/price-check returns computed median", pcResp.IsSuccessStatusCode && (await pcResp.Content.ReadAsStringAsync()).Contains("120"));

    // SSE /api/events live push verification (client is registered before ": connected" is written)
    using CancellationTokenSource sseCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    using HttpResponseMessage sseResp = await webClient.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, sseCts.Token);
    using StreamReader sseReader = new StreamReader(await sseResp.Content.ReadAsStreamAsync(sseCts.Token));
    string? firstLine = await sseReader.ReadLineAsync(sseCts.Token);
    web.BroadcastUpdate();
    bool gotUpdate = false;
    for (int i = 0; i < 6 && !gotUpdate; i++)
    {
        string? line = await sseReader.ReadLineAsync(sseCts.Token);
        if (line == null) break;
        if (line == "event: update") gotUpdate = true;
    }
    Check("GET /api/events streams live SSE update event",
        sseResp.Content.Headers.ContentType?.MediaType == "text/event-stream" &&
        firstLine == ": connected" &&
        gotUpdate);

    byte[] zip = await webClient.GetByteArrayAsync("/api/backup");
    Check("GET /api/backup produces valid .zip", zip.Length > 200 && zip[0] == (byte)'P' && zip[1] == (byte)'K');

    Console.WriteLine($"\n=== RESULT: {passed} passed, {failed} failed ===");
    Environment.Exit(failed == 0 ? 0 : 1);
}
finally
{
    try { Directory.Delete(simDir, recursive: true); } catch { }
}

static string FindRepoRoot()
{
    DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
    for (int i = 0; i < 8 && dir != null; i++)
    {
        if (File.Exists(Path.Combine(dir.FullName, "MarktplaatsNotifier.csproj")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    return Environment.CurrentDirectory;
}

class MockHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> fn;
    public MockHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> fn) => this.fn = fn;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => fn(request);
}

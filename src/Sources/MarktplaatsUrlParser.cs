using System.Globalization;
using System.Text.RegularExpressions;

namespace MarktplaatsNotifier;

public class ParsedMarktplaatsUrl
{
    public string Query { get; set; } = "";
    public int? MaxPrice { get; set; }
    public string Category { get; set; } = "";
    public int SubCategory { get; set; }
}

public static class MarktplaatsUrlParser
{
    private static readonly Regex AnyUrlRegex = new Regex(@"https?://[^\s]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ItemIdRegex = new Regex(@"\b(m\d{7,12})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, string> CategorySlugs =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["audio-tv-en-foto"] = "31",
            ["computers-en-software"] = "322",
            ["spelcomputers-en-games"] = "356",
            ["fietsen-en-brommers"] = "445",
            ["huis-en-inrichting"] = "504",
            ["witgoed-en-apparatuur"] = "537",
            ["telecommunicatie"] = "820",
            ["auto-onderdelen"] = "2600",
            ["auto-s"] = "91",
            ["doe-het-zelf-en-verbouw"] = "239",
            ["muziek-en-instrumenten"] = "784",
            ["sport-en-fitness"] = "856",
            ["tuin-en-terras"] = "1847",
            ["kinderen-en-baby-s"] = "565",
            ["kleding-dames"] = "621",
            ["kleding-heren"] = "1776",
            ["verzamelen"] = "1098",
            ["boeken"] = "201",
            ["antiek-en-kunst"] = "1",
            ["motoren"] = "678"
        };

    // Detects a Marktplaats listing URL even inside shared app text, e.g.:
    // "Samsung Odyssey - €120 https://link.marktplaats.nl/m2189123456"
    // or "https://www.marktplaats.nl/v/computers-en-software/monitoren/m2189123456-samsung"
    public static bool TryExtractListingUrl(string text, out string listingUrl, out string itemId)
    {
        listingUrl = "";
        itemId = "";

        foreach (Match urlMatch in AnyUrlRegex.Matches(text))
        {
            string candidate = urlMatch.Value.TrimEnd('.', ',', ')', ']', '!', '?');
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
            {
                continue;
            }

            if (!(uri.Host.Equals("marktplaats.nl", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.EndsWith(".marktplaats.nl", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            bool isShortLink = uri.Host.Equals("link.marktplaats.nl", StringComparison.OrdinalIgnoreCase);
            bool isVipPath = uri.AbsolutePath.StartsWith("/v/", StringComparison.OrdinalIgnoreCase) ||
                             uri.AbsolutePath.StartsWith("/a/", StringComparison.OrdinalIgnoreCase);
            if (!isShortLink && !isVipPath)
            {
                continue;
            }

            Match idMatch = ItemIdRegex.Match(uri.AbsolutePath);
            if (idMatch.Success)
            {
                listingUrl = candidate;
                itemId = idMatch.Groups[1].Value.ToLowerInvariant();
                return true;
            }
        }

        return false;
    }

    public static bool IsMarktplaatsSearchUrl(string text)
    {
        string trimmed = text.Trim();
        if (TryExtractListingUrl(trimmed, out _, out _))
        {
            return false;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) &&
               (uri.Host.Equals("marktplaats.nl", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".marktplaats.nl", StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryParse(string rawUrl, out ParsedMarktplaatsUrl? parsed, out string error)
    {
        parsed = null;
        error = "";

        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out Uri? uri) ||
            !(uri.Host.Equals("marktplaats.nl", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".marktplaats.nl", StringComparison.OrdinalIgnoreCase)))
        {
            error = "That doesn't look like a Marktplaats.nl link.";
            return false;
        }

        ParsedMarktplaatsUrl result = new ParsedMarktplaatsUrl();

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            string seg = segments[i];
            if (seg.Equals("q", StringComparison.OrdinalIgnoreCase) && i + 1 < segments.Length)
            {
                result.Query = DecodeSearchText(segments[i + 1]);
            }
            else if (seg.Equals("l", StringComparison.OrdinalIgnoreCase) && i + 1 < segments.Length)
            {
                string catSlug = segments[i + 1];
                if (CategorySlugs.TryGetValue(catSlug, out string? catId))
                {
                    result.Category = catId;
                }
            }
        }

        Dictionary<string, string> queryParams = ParseKeyValues(uri.Query.TrimStart('?'), '&', '=');
        ApplyParameters(result, queryParams);

        string fragment = uri.Fragment.TrimStart('#');
        if (fragment != "")
        {
            Dictionary<string, string> hashParams = fragment.Contains('|') || fragment.Contains(':')
                ? ParseKeyValues(fragment, '|', ':')
                : ParseKeyValues(fragment, '&', '=');
            ApplyParameters(result, hashParams);
        }

        result.Query = result.Query.Replace(";", " ").Trim();
        if (result.Query == "")
        {
            error = "Couldn't find search words in that link. Search for something on Marktplaats first, then copy the URL.";
            return false;
        }

        parsed = result;
        return true;
    }

    private static void ApplyParameters(ParsedMarktplaatsUrl result, Dictionary<string, string> values)
    {
        if (TryGetAny(values, out string q, "query", "q") && q != "")
        {
            result.Query = DecodeSearchText(q);
        }

        if (TryGetAny(values, out string l1, "l1CategoryId", "categoryId") && int.TryParse(l1, out _))
        {
            result.Category = l1;
        }

        if (TryGetAny(values, out string l2, "l2CategoryId", "subCategoryId") &&
            int.TryParse(l2, out int sub) && sub > 0 && result.Category != "")
        {
            result.SubCategory = sub;
        }

        if (TryGetAny(values, out string centsStr, "PriceCentsTo", "priceCentsTo") &&
            long.TryParse(centsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long cents) &&
            cents >= 0)
        {
            result.MaxPrice = (int)Math.Round(cents / 100.0);
        }
        else if (TryGetAny(values, out string eurosStr, "maxPrice", "priceTo") &&
                 double.TryParse(eurosStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double euros) &&
                 euros >= 0)
        {
            result.MaxPrice = (int)Math.Round(euros);
        }
    }

    private static bool TryGetAny(Dictionary<string, string> map, out string value, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (map.TryGetValue(key, out string? found) && !string.IsNullOrWhiteSpace(found))
            {
                value = found.Trim();
                return true;
            }
        }

        value = "";
        return false;
    }

    private static Dictionary<string, string> ParseKeyValues(string text, char pairSep, char kvSep)
    {
        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return map;
        }

        foreach (string pair in text.Split(pairSep, StringSplitOptions.RemoveEmptyEntries))
        {
            int idx = pair.IndexOf(kvSep);
            if (idx <= 0)
            {
                continue;
            }

            string key = Uri.UnescapeDataString(pair.Substring(0, idx)).Trim();
            string val = Uri.UnescapeDataString(pair.Substring(idx + 1)).Trim();
            if (key != "")
            {
                map[key] = val;
            }
        }

        return map;
    }

    private static string DecodeSearchText(string raw)
    {
        return Uri.UnescapeDataString(raw.Replace('+', ' ')).Trim();
    }
}

using System.Text;

namespace MarktplaatsNotifier;

public class SearchRule
{
    public int Id { get; set; }
    public string Query { get; set; } = "";
    public int MaxPrice { get; set; }
    public string Category { get; set; } = "";
    public int SubCategory { get; set; }
    public string[] SkipWords { get; set; } = Array.Empty<string>();
    public bool Urgent { get; set; }
}

public class SearchList
{
    private readonly string filePath;
    private readonly object rulesLock = new object();
    private int nextRuleId = 1;

    public Dictionary<string, SearchRule> Rules { get; } =
        new Dictionary<string, SearchRule>(StringComparer.OrdinalIgnoreCase);

    public DateTime LastFileWriteUtc { get; private set; } = DateTime.MinValue;

    public SearchList(string? dataDir = null)
    {
        string dir = dataDir ?? AppFiles.DataDirectory;
        filePath = Path.Combine(dir, "searches.txt");
    }

    public List<string> GetKeysSnapshot()
    {
        lock (rulesLock)
        {
            return Rules.Keys.ToList();
        }
    }

    public List<SearchRule> GetRulesSnapshot()
    {
        lock (rulesLock)
        {
            return Rules.Values.ToList();
        }
    }

    public SearchRule? FindById(int id)
    {
        lock (rulesLock)
        {
            foreach (SearchRule rule in Rules.Values)
            {
                if (rule.Id == id)
                {
                    return rule;
                }
            }

            return null;
        }
    }

    public string Add(string line)
    {
        if (!TryParseLine(line, "", out SearchRule? rule, out string error))
        {
            return "❌ " + error;
        }

        return AddRule(rule!);
    }

    public string AddRule(SearchRule rule)
    {
        lock (rulesLock)
        {
            if (Rules.ContainsKey(rule.Query))
            {
                return $"⚠️ \"{rule.Query}\" is already in your list.";
            }

            rule.Id = nextRuleId++;
            Rules.Add(rule.Query, rule);

            string newLine = FormatLine(rule);
            if (File.Exists(filePath))
            {
                string fileText = File.ReadAllText(filePath);
                if (fileText != "" && !fileText.EndsWith("\n"))
                {
                    File.AppendAllText(filePath, "\n");
                }
            }

            File.AppendAllText(filePath, newLine + "\n");
            UpdateFileTimestamp();

            string extra = rule.Urgent ? " 🚨 urgent" : "";
            return $"✅ Added: {rule.Query} (max €{rule.MaxPrice}){extra}";
        }
    }

    public bool UpdateRule(SearchRule rule)
    {
        lock (rulesLock)
        {
            if (!Rules.ContainsKey(rule.Query))
            {
                return false;
            }

            Rules[rule.Query] = rule;

            if (File.Exists(filePath))
            {
                List<string> updatedLines = new List<string>();
                bool replaced = false;
                foreach (string fileLine in File.ReadAllLines(filePath))
                {
                    bool isComment = fileLine.Trim().StartsWith("#");
                    string lineSearch = fileLine.Split(';')[0].Trim();
                    if (!isComment && string.Equals(lineSearch, rule.Query, StringComparison.OrdinalIgnoreCase))
                    {
                        updatedLines.Add(FormatLine(rule));
                        replaced = true;
                    }
                    else
                    {
                        updatedLines.Add(fileLine);
                    }
                }

                if (!replaced)
                {
                    updatedLines.Add(FormatLine(rule));
                }

                File.WriteAllLines(filePath, updatedLines);
                UpdateFileTimestamp();
            }

            return true;
        }
    }

    public string Remove(string searchText, out string removedKey)
    {
        lock (rulesLock)
        {
            removedKey = "";
            if (!Rules.TryGetValue(searchText, out SearchRule? existing))
            {
                return $"❌ \"{searchText}\" is not in your list. Send /list to see your searches.";
            }

            removedKey = existing.Query;
            Rules.Remove(existing.Query);

            if (File.Exists(filePath))
            {
                List<string> keptLines = new List<string>();
                foreach (string fileLine in File.ReadAllLines(filePath))
                {
                    bool isComment = fileLine.Trim().StartsWith("#");
                    string lineSearch = fileLine.Split(';')[0].Trim();
                    if (!isComment && string.Equals(lineSearch, existing.Query, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    keptLines.Add(fileLine);
                }

                File.WriteAllLines(filePath, keptLines);
                UpdateFileTimestamp();
            }

            return $"🗑️ Removed: {existing.Query}";
        }
    }

    public string ListText()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append($"📋 Your searches ({Rules.Count}):\n");
        foreach (SearchRule rule in Rules.Values)
        {
            string urgentTag = rule.Urgent ? " 🚨" : "";
            sb.Append($"\n• {rule.Query} (max €{rule.MaxPrice}){urgentTag}");
        }

        return sb.ToString();
    }

    public string Load()
    {
        if (!File.Exists(filePath))
        {
            File.WriteAllText(filePath,
                "# One search per line: search words;max price;category;skip words;urgent\n# Example: defect monitor;60;322;barst,strepen\n");
        }

        string error = ParseFile(out Dictionary<string, SearchRule> parsed);
        if (error != "")
        {
            return error;
        }

        Rules.Clear();
        foreach (SearchRule rule in parsed.Values)
        {
            rule.Id = nextRuleId++;
            Rules.Add(rule.Query, rule);
        }

        UpdateFileTimestamp();
        return "";
    }

    public bool ReloadIfChanged(out string error, out List<string> removedKeys)
    {
        error = "";
        removedKeys = new List<string>();

        if (!File.Exists(filePath))
        {
            return false;
        }

        DateTime writeUtc = File.GetLastWriteTimeUtc(filePath);
        if (writeUtc <= LastFileWriteUtc)
        {
            return false;
        }

        LastFileWriteUtc = writeUtc;

        string parseError = ParseFile(out Dictionary<string, SearchRule> parsed);
        if (parseError != "")
        {
            error = parseError;
            return true;
        }

        foreach (string existingKey in Rules.Keys)
        {
            if (!parsed.ContainsKey(existingKey))
            {
                removedKeys.Add(existingKey);
            }
        }

        Dictionary<string, int> existingIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (SearchRule existing in Rules.Values)
        {
            existingIds[existing.Query] = existing.Id;
        }

        Rules.Clear();
        foreach (SearchRule rule in parsed.Values)
        {
            rule.Id = existingIds.TryGetValue(rule.Query, out int prevId) ? prevId : nextRuleId++;
            Rules.Add(rule.Query, rule);
        }

        return true;
    }

    private string ParseFile(out Dictionary<string, SearchRule> parsed)
    {
        parsed = new Dictionary<string, SearchRule>(StringComparer.OrdinalIgnoreCase);
        int lineNumber = 0;

        foreach (string line in File.ReadAllLines(filePath))
        {
            lineNumber++;
            string trimmed = line.Trim();
            if (trimmed == "" || trimmed.StartsWith("#"))
            {
                continue;
            }

            if (!TryParseLine(line, $"Line {lineNumber} in searches.txt: ", out SearchRule? rule, out string error))
            {
                return error;
            }

            if (parsed.ContainsKey(rule!.Query))
            {
                return $"Line {lineNumber} in searches.txt: \"{rule.Query}\" is in the file twice. Remove one of them.";
            }

            parsed.Add(rule.Query, rule);
        }

        return "";
    }

    private void UpdateFileTimestamp()
    {
        if (File.Exists(filePath))
        {
            LastFileWriteUtc = File.GetLastWriteTimeUtc(filePath);
        }
    }

    public static bool TryParseCategory(string raw, out string category, out int subCategory, out string error)
    {
        category = raw.Trim();
        subCategory = 0;
        error = "";

        if (category == "")
        {
            return true;
        }

        if (category.Contains('/'))
        {
            string[] pieces = category.Split('/');
            if (pieces.Length != 2)
            {
                error = $"\"{raw}\" is not a valid category. Use a number like 322 or 356/2954.";
                return false;
            }

            category = pieces[0].Trim();
            string subPart = pieces[1].Trim();
            if (category == "" || !int.TryParse(category, out _))
            {
                error = $"Include the main category before '/', like 356/2954 (got \"{raw}\").";
                return false;
            }

            if (!int.TryParse(subPart, out subCategory) || subCategory <= 0)
            {
                error = $"\"{subPart}\" is not a valid subcategory. Use numbers, like 356/2954.";
                return false;
            }

            return true;
        }

        if (!int.TryParse(category, out _))
        {
            error = $"\"{category}\" is not a valid category. Use a number like 322, or leave it out.";
            return false;
        }

        return true;
    }

    private static bool TryParseLine(string line, string prefix, out SearchRule? rule, out string error)
    {
        rule = null;
        error = "";

        string[] parts = line.Split(';');
        if (parts.Length < 2 || parts[0].Trim() == "")
        {
            error = prefix == ""
                ? "Use: /add search words;max price;category;skip words;urgent\nExample: /add rtx 3070;140;322"
                : $"{prefix}wrong format \"{line}\"\nUse: search words;max price (for example: defect monitor;60)";
            return false;
        }

        string searchText = parts[0].Trim();

        if (!int.TryParse(parts[1].Trim(), out int limit) || limit < 0)
        {
            error = $"{prefix}\"{parts[1].Trim()}\" is not a valid price. Use whole euros (0 or higher), like 140.";
            return false;
        }

        string rawCategory = parts.Length > 2 ? parts[2].Trim() : "";
        if (!TryParseCategory(rawCategory, out string category, out int subCategory, out string catError))
        {
            error = prefix + catError;
            return false;
        }

        string[] skipWords = Array.Empty<string>();
        if (parts.Length > 3)
        {
            skipWords = parts[3]
                .ToLowerInvariant()
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        string urgent = parts.Length > 4 ? parts[4].Trim().ToLowerInvariant() : "";
        if (urgent != "" && urgent != "urgent")
        {
            error = $"{prefix}\"{urgent}\" is not valid. Use urgent, or leave it out.";
            return false;
        }

        rule = new SearchRule
        {
            Query = searchText,
            MaxPrice = limit,
            Category = category,
            SubCategory = subCategory,
            SkipWords = skipWords,
            Urgent = urgent == "urgent"
        };
        return true;
    }

    private static string FormatLine(SearchRule rule)
    {
        string categoryPart = rule.Category;
        if (rule.Category != "" && rule.SubCategory != 0)
        {
            categoryPart += $"/{rule.SubCategory}";
        }

        string skipPart = string.Join(",", rule.SkipWords);
        if (rule.Urgent)
        {
            return $"{rule.Query};{rule.MaxPrice};{categoryPart};{skipPart};urgent";
        }

        if (skipPart != "")
        {
            return $"{rule.Query};{rule.MaxPrice};{categoryPart};{skipPart}";
        }

        if (categoryPart != "")
        {
            return $"{rule.Query};{rule.MaxPrice};{categoryPart}";
        }

        return $"{rule.Query};{rule.MaxPrice}";
    }
}

public class SearchList
{
    public Dictionary<string, int> Prices { get; } = new Dictionary<string, int>();
    public Dictionary<string, string> Categories { get; } = new Dictionary<string, string>();

    public Dictionary<string, int> SubCategories { get; } = new Dictionary<string, int>();

    public Dictionary<string, string[]> SkipWords { get; } = new Dictionary<string, string[]>();
    public HashSet<string> NewSearches { get; } = new HashSet<string>();

    public HashSet<string> UrgentSearches { get; } = new HashSet<string>();
    public HashSet<string> DefectSearches { get; } = new HashSet<string>();


    public string Add(string line)
    {
        string[] parts = line.Split(';');
        if (parts.Length < 2 || parts[0].Trim() == "")
        {
            return "❌ Use: /add search words;max price;category (category is optional)\nExample: /add rtx 3070;140;322";
        }

        string searchText = parts[0].Trim();

        if (!int.TryParse(parts[1].Trim(), out int limit))
        {
            return $"❌ \"{parts[1].Trim()}\" is not a valid price. Use whole euros, like 140.";
        }

        string category = "";
        if (parts.Length > 2)
        {
            category = parts[2].Trim();
        }

        int subCategory = 0;
        if (category.Contains('/'))
        {
            string[] pieces = category.Split('/');
            category = pieces[0].Trim();
            if (!int.TryParse(pieces[1].Trim(), out subCategory))
            {
                return $"❌ \"{pieces[1].Trim()}\" is not a valid subcategory. Use numbers, like 356/2954.";
            }
        }

        if (category != "" && !int.TryParse(category, out _))
        {
            return $"❌ \"{category}\" is not a valid category. Use a number like 322, or leave it out.";
        }

        if (Prices.ContainsKey(searchText))
        {
            return $"⚠️ \"{searchText}\" is already in your list.";
        }

        string[] skipWords = new string[0];
        if (parts.Length > 3)
        {
            skipWords = parts[3].ToLower()
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        string[] flags = new string[0];
        if (parts.Length > 4)
        {
            flags = parts[4].ToLower()
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        Prices.Add(searchText, limit);
        Categories.Add(searchText, category);
        SubCategories.Add(searchText, subCategory);
        SkipWords.Add(searchText, skipWords);
        NewSearches.Add(searchText);
        if (flags.Contains("urgent"))
        {
            UrgentSearches.Add(searchText);
        }

        if (flags.Contains("defect"))
        {
            DefectSearches.Add(searchText);
        }

        string newLine = BuildLine(searchText);

        string fileText = File.ReadAllText("searches.txt");
        if (fileText != "" && !fileText.EndsWith("\n"))
        {
            File.AppendAllText("searches.txt", "\n");
        }

        File.AppendAllText("searches.txt", newLine + "\n");

        string extra = "";
        if (UrgentSearches.Contains(searchText))
        {
            extra += " 🚨";
        }

        if (DefectSearches.Contains(searchText))
        {
            extra += " 🛠️";
        }

        return $"✅ Added: {searchText} (max €{limit}){extra}";
    }

    public string Remove(string searchText)
    {
        if (!Prices.ContainsKey(searchText))
        {
            return $"❌ \"{searchText}\" is not in your list. Send /list to see your searches.";
        }

        Prices.Remove(searchText);
        Categories.Remove(searchText);
        SubCategories.Remove(searchText);
        SkipWords.Remove(searchText);
        UrgentSearches.Remove(searchText);
        DefectSearches.Remove(searchText);

        List<string> keptLines = new List<string>();
        foreach (string fileLine in File.ReadAllLines("searches.txt"))
        {
            bool isComment = fileLine.Trim().StartsWith("#");
            string lineSearch = fileLine.Split(';')[0].Trim();
            if (!isComment && lineSearch == searchText)
            {
                continue;
            }

            keptLines.Add(fileLine);
        }

        File.WriteAllLines("searches.txt", keptLines);

        return $"🗑️ Removed: {searchText}";
    }

    public string BuildLine(string searchText)
    {
        string line = $"{searchText};{Prices[searchText]};{Categories[searchText]}";
        if (SubCategories[searchText] != 0)
        {
            line += $"/{SubCategories[searchText]}";
        }

        List<string> flags = new List<string>();
        if (UrgentSearches.Contains(searchText))
        {
            flags.Add("urgent");
        }

        if (DefectSearches.Contains(searchText))
        {
            flags.Add("defect");
        }

        line += $";{string.Join(",", SkipWords[searchText])};{string.Join(",", flags)}";
        return line.TrimEnd(';');
    }

    private void SaveLine(string searchText)
    {
        List<string> lines = new List<string>();
        foreach (string fileLine in File.ReadAllLines("searches.txt"))
        {
            bool isComment = fileLine.Trim().StartsWith("#");
            string lineSearch = fileLine.Split(';')[0].Trim();
            if (!isComment && lineSearch == searchText)
            {
                lines.Add(BuildLine(searchText));
            }
            else
            {
                lines.Add(fileLine);
            }
        }

        File.WriteAllLines("searches.txt", lines);
    }

    public string SetPrice(string searchText, int price)
    {
        if (!Prices.ContainsKey(searchText))
        {
            return $"❌ \"{searchText}\" is not in your list.";
        }

        Prices[searchText] = price;
        SaveLine(searchText);
        return $"💶 {searchText}: max price is now €{price}";
    }

    public string ToggleUrgent(string searchText)
    {
        if (!Prices.ContainsKey(searchText))
        {
            return $"❌ \"{searchText}\" is not in your list.";
        }

        if (!UrgentSearches.Remove(searchText))
        {
            UrgentSearches.Add(searchText);
        }

        SaveLine(searchText);
        return UrgentSearches.Contains(searchText) ? $"🚨 {searchText}: urgent ON" : $"🔕 {searchText}: urgent OFF";
    }

    public string ToggleDefect(string searchText)
    {
        if (!Prices.ContainsKey(searchText))
        {
            return $"❌ \"{searchText}\" is not in your list.";
        }

        if (!DefectSearches.Remove(searchText))
        {
            DefectSearches.Add(searchText);
        }

        SaveLine(searchText);
        return DefectSearches.Contains(searchText)
            ? $"🛠️ {searchText}: only broken ON"
            : $"✅ {searchText}: only broken OFF";
    }

    public string DetailsText(string searchText)
    {
        string text = $"🔎 {searchText}\n💶 max €{Prices[searchText]}";
        if (Categories[searchText] != "")
        {
            text += $"\n📂 category {Categories[searchText]}";
            if (SubCategories[searchText] != 0)
            {
                text += $"/{SubCategories[searchText]}";
            }
        }

        if (SkipWords[searchText].Length > 0)
        {
            text += $"\n🚫 skip: {string.Join(", ", SkipWords[searchText])}";
        }

        text += UrgentSearches.Contains(searchText) ? "\n🚨 urgent: ON" : "\n🔕 urgent: off";
        text += DefectSearches.Contains(searchText) ? "\n🛠️ only broken: ON" : "\n✅ only broken: off";
        return text;
    }

    public string Reload()
    {
        Prices.Clear();
        Categories.Clear();
        SubCategories.Clear();
        SkipWords.Clear();
        UrgentSearches.Clear();
        DefectSearches.Clear();
        return Load();
    }

    private static string GroupName(string line)
    {
        string trimmed = line.Trim();
        if (!trimmed.StartsWith("# ---"))
        {
            return "";
        }

        return trimmed.Trim('#', ' ', '-');
    }

    public List<(string Name, int Active, int Off)> Groups()
    {
        List<(string Name, int Active, int Off)> groups = new List<(string Name, int Active, int Off)>();
        foreach (string line in File.ReadAllLines("searches.txt"))
        {
            string name = GroupName(line);
            if (name != "")
            {
                groups.Add((name, 0, 0));
                continue;
            }

            if (groups.Count == 0 || line.Trim() == "")
            {
                continue;
            }

            var last = groups[^1];
            if (line.StartsWith("#off "))
            {
                groups[^1] = (last.Name, last.Active, last.Off + 1);
            }
            else if (!line.Trim().StartsWith("#"))
            {
                groups[^1] = (last.Name, last.Active + 1, last.Off);
            }
        }

        return groups.Where(group => group.Active + group.Off > 0).ToList();
    }

    public string ToggleGroup(string groupName)
    {
        string[] lines = File.ReadAllLines("searches.txt");
        bool inGroup = false;
        bool turnOff = Groups().Any(group => group.Name == groupName && group.Active > 0);

        for (int i = 0; i < lines.Length; i++)
        {
            string name = GroupName(lines[i]);
            if (name != "")
            {
                inGroup = name == groupName;
                continue;
            }

            if (!inGroup || lines[i].Trim() == "")
            {
                continue;
            }

            if (turnOff && !lines[i].Trim().StartsWith("#"))
            {
                lines[i] = "#off " + lines[i];
            }
            else if (!turnOff && lines[i].StartsWith("#off "))
            {
                lines[i] = lines[i].Substring(5);
            }
        }

        File.WriteAllLines("searches.txt", lines);
        string error = Reload();
        if (error != "")
        {
            return "❌ " + error;
        }

        return turnOff ? $"⏸️ {groupName}: OFF" : $"▶️ {groupName}: ON";
    }

    public string ListText()
    {
        string text = $"📋 Your searches ({Prices.Count}):\n";
        foreach (KeyValuePair<string, int> pair in Prices)
        {
            text += $"\n• {pair.Key} (max €{pair.Value})";
        }

        return text;
    }

    public string Load()
    {
        if (!File.Exists("searches.txt"))
        {
            if (File.Exists("searches.example.txt"))
            {
                File.Copy("searches.example.txt", "searches.txt");
            }
            else
            {
                File.WriteAllText("searches.txt",
                    "# One search per line: search words;max price;category\n# Example: defect monitor;60;322\n");
            }
        }

        int lineNumber = 0;
        foreach (string line in File.ReadAllLines("searches.txt"))
        {
            lineNumber++;
            if (line.Trim() == "" || line.Trim().StartsWith("#"))
            {
                continue;
            }

            string[] parts = line.Split(';');
            if (parts.Length < 2 || parts[0].Trim() == "")
            {
                return
                    $"Line {lineNumber} in searches.txt is wrong: \"{line}\"\nUse: search words;max price (for example: defect monitor;60)";
            }

            string searchText = parts[0].Trim();

            if (!int.TryParse(parts[1].Trim(), out int limit))
            {
                return
                    $"Line {lineNumber} in searches.txt: \"{parts[1].Trim()}\" is not a valid price. Use whole euros, like 60.";
            }

            string category = "";
            if (parts.Length > 2)
            {
                category = parts[2].Trim();
            }

            int subCategory = 0;
            if (category.Contains('/'))
            {
                string[] pieces = category.Split('/');
                category = pieces[0].Trim();
                if (!int.TryParse(pieces[1].Trim(), out subCategory))
                {
                    return
                        $"Line {lineNumber} in searches.txt: \"{pieces[1].Trim()}\" is not a valid subcategory. Use numbers, like 356/2954.";
                }
            }

            if (category != "" && !int.TryParse(category, out _))
            {
                return
                    $"Line {lineNumber} in searches.txt: \"{category}\" is not a valid category. Use a number like 322, or leave it out.";
            }

            string[] skipWords = new string[0];
            if (parts.Length > 3)
            {
                skipWords = parts[3].ToLower().Split(',',
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            }

            if (Prices.ContainsKey(searchText))
            {
                return
                    $"Line {lineNumber} in searches.txt: \"{searchText}\" is in the file twice. Remove one of them.";
            }

            string[] flags = new string[0];
            if (parts.Length > 4)
            {
                flags = parts[4].ToLower()
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            }

            foreach (string flag in flags)
            {
                if (flag != "urgent" && flag != "defect")
                {
                    return
                        $"Line {lineNumber} in searches.txt: \"{flag}\" is not valid. Use urgent, defect or urgent,defect, or leave it out.";
                }
            }

            Prices.Add(searchText, limit);
            Categories.Add(searchText, category);
            SubCategories.Add(searchText, subCategory);
            SkipWords.Add(searchText, skipWords);
            if (flags.Contains("urgent"))
            {
                UrgentSearches.Add(searchText);
            }

            if (flags.Contains("defect"))
            {
                DefectSearches.Add(searchText);
            }
        }

        return "";
    }
}
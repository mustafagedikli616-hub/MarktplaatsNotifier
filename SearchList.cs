
public class SearchList
{
    public Dictionary<string, int> Prices { get; } = new Dictionary<string, int>();         
    public Dictionary<string, string> Categories { get; } = new Dictionary<string, string>(); 
    public HashSet<string> NewSearches { get; } = new HashSet<string>();      
    
    
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

        if (category != "" && !int.TryParse(category, out _))
        {
            return $"❌ \"{category}\" is not a valid category. Use a number like 322, or leave it out.";
        }

        if (Prices.ContainsKey(searchText))
        {
            return $"⚠️ \"{searchText}\" is already in your list.";
        }

        Prices.Add(searchText, limit);
        Categories.Add(searchText, category);
        NewSearches.Add(searchText);

        string newLine = $"{searchText};{limit}";
        if (category != "")
        {
            newLine += $";{category}";
        }

        string fileText = File.ReadAllText("searches.txt");
        if (fileText != "" && !fileText.EndsWith("\n"))
        {
            File.AppendAllText("searches.txt", "\n");
        }

        File.AppendAllText("searches.txt", newLine + "\n");

        return $"✅ Added: {searchText} (max €{limit})";
    }
    
    public string Remove(string searchText)
    {
        if (!Prices.ContainsKey(searchText))
        {
            return $"❌ \"{searchText}\" is not in your list. Send /list to see your searches.";
        }

        Prices.Remove(searchText);
        Categories.Remove(searchText);

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
            File.WriteAllText("searches.txt",
                "# One search per line: search words;max price;category\n# Example: defect monitor;60;322\n");
        }
        int lineNumber = 0;
        foreach(string line in File.ReadAllLines("searches.txt"))
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

            if (category != "" && !int.TryParse(category, out _))
            {
                return 
                    $"Line {lineNumber} in searches.txt: \"{category}\" is not a valid category. Use a number like 322, or leave it out.";
            }

            if (Prices.ContainsKey(searchText))
            {
                return
                    $"Line {lineNumber} in searches.txt: \"{searchText}\" is in the file twice. Remove one of them.";
            }

            Prices.Add(searchText, limit);
            Categories.Add(searchText, category);
            
        }

        return "";
    }
}
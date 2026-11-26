using System.Text.RegularExpressions;

namespace MarktplaatsNotifier;

// Reads hints.txt and finds a repair tip for a listing.
// A line in hints.txt looks like: words;hint;question to ask the seller
public class RepairHint
{
    public string[] Words { get; set; } = Array.Empty<string>();
    public string Text { get; set; } = "";
    public string Question { get; set; } = "";
}

public class RepairHints
{
    private static readonly Regex NegationBeforeWord = new Regex(
        @"\b(?:geen|zonder)\s+(?:(?:enkele|enige|zichtbare)\s+)?$",
        RegexOptions.Compiled);

    private readonly List<RepairHint> rules = new List<RepairHint>();
    private readonly object syncLock = new object();
    private DateTime lastFileWriteUtc = DateTime.MinValue;

    public int Count
    {
        get { lock (syncLock) { return rules.Count; } }
    }

    // Used the first time, when hints.txt doesn't exist yet
    private static readonly string[] DefaultLines =
    {
        "# Repair hints: words;hint;question to ask the seller",
        "# Words are separated by commas. Use + when ALL parts must be there, like: ps5+geen beeld",
        "# Rules are checked from top to bottom. Only the first one that fits is shown.",
        "scherm is gebroken,scherm gebroken,gebroken scherm,barst,glas kapot;💡 Broken screen: for monitors and TVs a new panel usually costs more than it's worth, for phones check the price of a new screen first;Is er achter de barst nog beeld te zien?",
        "ps5+geen beeld,playstation+geen beeld,xbox+geen beeld;💡 Console without picture: often the HDMI port or the HDMI chip;Kun je een foto sturen van de HDMI-poort?",
        "hdmi poort,hdmi-poort,hdmi kapot,hdmi defect;💡 Broken HDMI port: needs hot air to replace, check the photos for bent pins;Kun je een foto sturen van de HDMI-poort?",
        "geen beeld+geluid,zwart scherm+geluid,donker scherm+geluid;💡 Sound but no picture: usually the backlight or the power board;Als je met een zaklamp op het scherm schijnt, zie je dan iets?",
        "geen beeld,zwart scherm,blijft zwart;💡 No picture: try another cable, then test the backlight with a flashlight;Hoor je geluid, of brandt er een lampje?",
        "stickdrift,stick drift;💡 Stick drift: swap the stick module (through-hole soldering), cheap part;Werken alle knoppen en het opladen nog?",
        "oververhit,te heet,wordt heet;💡 Overheating: often dried thermal paste or a dusty fan;Hoe lang blijft hij aan voordat hij uitvalt?",
        "laadt niet,laad niet,oplaadpoort,oplaad poort;💡 Not charging: often the charging port or the battery, cheap parts;Heb je een andere kabel en lader geprobeerd?",
        "gaat niet aan,gaat niet meer aan,start niet,doet niets,geen stroom;💡 Dead: check the charger first, then the battery or the power board;Hoe lang heeft hij ongebruikt gelegen, en brandt er een lampje als je hem aansluit?",
        "bootloop,blijft opnieuw opstarten,herstart steeds,blijft hangen;💡 Keeps restarting: can be software (reset) or the main board;Heb je een fabrieksreset geprobeerd?",
        "knippert,flikkert,flikkering;💡 Flickering: try another cable first, then the backlight or the power board;Gebeurt het ook met een andere kabel?",
        "accu kapot,accu slecht,accu defect,batterij kapot,batterij slecht,batterij defect;💡 Bad battery: usually an easy, cheap swap;Werkt hij wel als hij aan de lader zit?",
        "geen signaal,no signal;💡 \"No signal\": often the cable or the source, sometimes the main board;Heb je hem met een andere kabel en een ander apparaat geprobeerd?",
        "ventilator+lawaai,maakt lawaai,veel herrie;💡 Loud fan: usually dust, clean it and use new thermal paste;Is hij ooit schoongemaakt?"
    };

    public void Load()
    {
        string filePath = AppFiles.PathFor("hints.txt");
        if (!File.Exists(filePath))
        {
            File.WriteAllLines(filePath, DefaultLines);
        }

        List<RepairHint> loaded = new List<RepairHint>();
        foreach (string line in File.ReadAllLines(filePath))
        {
            string trimmed = line.Trim();
            if (trimmed == "" || trimmed.StartsWith("#"))
            {
                continue;
            }

            string[] parts = line.Split(';');
            if (parts.Length < 2)
            {
                continue;
            }

            RepairHint rule = new RepairHint
            {
                Words = parts[0].ToLowerInvariant().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                Text = parts[1].Trim(),
                Question = parts.Length > 2 ? parts[2].Trim() : ""
            };

            if (rule.Words.Length > 0)
            {
                loaded.Add(rule);
            }
        }

        lock (syncLock)
        {
            rules.Clear();
            rules.AddRange(loaded);
            lastFileWriteUtc = File.GetLastWriteTimeUtc(filePath);
        }
    }

    public bool ReloadIfChanged()
    {
        string filePath = AppFiles.PathFor("hints.txt");
        if (!File.Exists(filePath))
        {
            return false;
        }

        DateTime writeUtc = File.GetLastWriteTimeUtc(filePath);
        if (writeUtc <= lastFileWriteUtc)
        {
            return false;
        }

        Load();
        return true;
    }

    // Gives back the first rule that fits the text, or null if none fits
    public RepairHint? Find(string text)
    {
        string lowerText = text.ToLowerInvariant();
        List<RepairHint> snapshot;
        lock (syncLock)
        {
            snapshot = rules.ToList();
        }

        foreach (RepairHint rule in snapshot)
        {
            foreach (string option in rule.Words)
            {
                // "geen beeld+geluid" means: "geen beeld" AND "geluid" must both be there
                bool allFound = true;
                foreach (string part in option.Split('+'))
                {
                    if (!HasWord(lowerText, part.Trim()))
                    {
                        allFound = false;
                        break;
                    }
                }

                if (allFound)
                {
                    return rule;
                }
            }
        }

        return null;
    }

    // True if the word is in the text, but not right after a whole-word "geen" or "zonder".
    private static bool HasWord(string text, string word)
    {
        if (word == "")
        {
            return false;
        }

        int index = text.IndexOf(word, StringComparison.Ordinal);
        while (index >= 0)
        {
            string before = text.Substring(0, index);
            if (!NegationBeforeWord.IsMatch(before))
            {
                return true;
            }

            index = text.IndexOf(word, index + 1, StringComparison.Ordinal);
        }

        return false;
    }
}

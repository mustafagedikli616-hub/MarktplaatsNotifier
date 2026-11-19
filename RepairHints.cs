namespace MarktplaatsNotifier;

// Reads hints.txt and finds a repair tip for a listing.
// A line in hints.txt looks like: words;hint;question to ask the seller
public class RepairHint
{
    public string[] Words { get; set; } = new string[0];
    public string Text { get; set; } = "";
    public string Question { get; set; } = "";
}

public class RepairHints
{
    private List<RepairHint> rules = new List<RepairHint>();

    // Used the first time, when hints.txt doesn't exist yet
    private static string[] defaultLines =
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
        if (!File.Exists("hints.txt"))
        {
            File.WriteAllLines("hints.txt", defaultLines);
        }

        foreach (string line in File.ReadAllLines("hints.txt"))
        {
            // skip empty lines and comments
            if (line.Trim() == "" || line.StartsWith("#"))
            {
                continue;
            }

            string[] parts = line.Split(';');
            if (parts.Length < 2)
            {
                continue;
            }

            RepairHint rule = new RepairHint();
            rule.Words = parts[0].ToLower().Split(',');
            rule.Text = parts[1].Trim();
            if (parts.Length > 2)
            {
                rule.Question = parts[2].Trim();
            }

            rules.Add(rule);
        }
    }

    // Gives back the first rule that fits the text, or null if none fits
    public RepairHint? Find(string text)
    {
        string lowerText = text.ToLower();

        foreach (RepairHint rule in rules)
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

    // True if the word is in the text, but not right after "geen " or "zonder "
    // So "geen stickdrift" doesn't count as stick drift.
    private static bool HasWord(string text, string word)
    {
        if (word == "")
        {
            return false;
        }

        int index = text.IndexOf(word);
        while (index >= 0)
        {
            string before = text.Substring(0, index);
            if (!before.EndsWith("geen ") && !before.EndsWith("zonder "))
            {
                return true;
            }

            index = text.IndexOf(word, index + 1);
        }

        return false;
    }
}
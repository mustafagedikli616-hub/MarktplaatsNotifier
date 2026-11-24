namespace MarktplaatsNotifier;

public class SearchScheduler
{
    private const double StartSeconds = 180;      
    private const double MinSeconds = 60;       
    private const double MaxSeconds = 900;      
    private const double UrgentMaxSeconds = 120;  
    private const double NightMinSeconds = 600;   
    private const double EveningMaxSeconds = 300;

    private Dictionary<string, DateTime> nextCheck = new Dictionary<string, DateTime>();
    private Dictionary<string, double> waitSeconds = new Dictionary<string, double>();
    private Dictionary<string, double> hotScore = new Dictionary<string, double>();

    public void SpreadStart(IEnumerable<string> searches, int secondsApart)
    {
        int position = 0;
        foreach (string search in searches)
        {
            nextCheck[search] = DateTime.Now.AddSeconds(position * secondsApart);
            position++;
        }
    }
    public string NextDue(IEnumerable<string> searches, HashSet<string> urgentSearches)
    {
        List<string> due = new List<string>();
        foreach (string search in searches)
        {
            if (nextCheck.GetValueOrDefault(search, DateTime.MinValue) <= DateTime.Now)
            {
                due.Add(search);
            }
        }

        if (due.Count == 0)
        {
            return "";
        }

        return due
            .OrderBy(search => urgentSearches.Contains(search) ? 0 : 1)
            .ThenByDescending(search => hotScore.GetValueOrDefault(search, 0))
            .ThenBy(search => nextCheck.GetValueOrDefault(search, DateTime.MinValue))
            .First();
    }

    public List<string> HotSearches(int count)
    {
        return hotScore
            .Where(pair => pair.Value >= 0.5)
            .OrderByDescending(pair => pair.Value)
            .Take(count)
            .Select(pair => pair.Key)
            .ToList();
    }

   
    public void Done(string search, bool foundNew, bool urgent, bool night,bool evening)
    {
        double hot = hotScore.GetValueOrDefault(search, 0) * 0.9;
        if (foundNew)
        {
            hot += 1;
        }
        hotScore[search] = hot;
        double wait = waitSeconds.GetValueOrDefault(search, StartSeconds);

        if (foundNew)
        {
            wait = Math.Max(MinSeconds, wait / 2);    
        }
        else
        {
            wait = Math.Min(MaxSeconds, wait * 1.5); 
        }

        if (urgent)
        {
            wait = Math.Min(wait, UrgentMaxSeconds);
        }

        waitSeconds[search] = wait;

        double thisTime = wait;
        if (night && !urgent)
        {
            thisTime = Math.Max(wait, NightMinSeconds);
        }
        else if (evening)
        {
            thisTime = Math.Min(wait, EveningMaxSeconds);
        }

        nextCheck[search] = DateTime.Now.AddSeconds(thisTime);
    }

    
    public int SecondsUntil(string search)
    {
        DateTime due = nextCheck.GetValueOrDefault(search, DateTime.MinValue);
        return Math.Max(0, (int)(due - DateTime.Now).TotalSeconds);
    }
}

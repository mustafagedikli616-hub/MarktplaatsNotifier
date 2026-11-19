namespace MarktplaatsNotifier;

public class SearchScheduler
{
    private const double StartSeconds = 180;      
    private const double MinSeconds = 60;       
    private const double MaxSeconds = 900;      
    private const double UrgentMaxSeconds = 120;  
    private const double NightMinSeconds = 600;   

    private Dictionary<string, DateTime> nextCheck = new Dictionary<string, DateTime>();
    private Dictionary<string, double> waitSeconds = new Dictionary<string, double>();

   
    public string NextDue(IEnumerable<string> searches)
    {
        string best = "";
        DateTime bestTime = DateTime.MaxValue;

        foreach (string search in searches)
        {
            
            DateTime due = nextCheck.GetValueOrDefault(search, DateTime.MinValue);
            if (due < bestTime)
            {
                best = search;
                bestTime = due;
            }
        }

        if (best != "" && bestTime <= DateTime.Now)
        {
            return best;
        }

        return "";
    }

   
    public void Done(string search, bool foundNew, bool urgent, bool night)
    {
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

        nextCheck[search] = DateTime.Now.AddSeconds(thisTime);
    }

    
    public int SecondsUntil(string search)
    {
        DateTime due = nextCheck.GetValueOrDefault(search, DateTime.MinValue);
        return Math.Max(0, (int)(due - DateTime.Now).TotalSeconds);
    }
}

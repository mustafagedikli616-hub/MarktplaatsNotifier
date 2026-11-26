namespace MarktplaatsNotifier;

public class SearchScheduler
{
    private const double StartSeconds = 180;
    private const double MinSeconds = 60;
    private const double MaxSeconds = 900;
    private const double UrgentMaxSeconds = 120;
    private const double NightMinSeconds = 600;

    private readonly object syncLock = new object();
    private readonly Dictionary<string, DateTime> nextCheck =
        new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> waitSeconds =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    public string NextDue(IEnumerable<string> searches)
    {
        lock (syncLock)
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
    }

    public void Done(string search, bool foundNew, bool urgent, bool night, bool quiet = false)
    {
        lock (syncLock)
        {
            double wait;
            if (quiet && !waitSeconds.ContainsKey(search))
            {
                wait = StartSeconds;
            }
            else
            {
                wait = waitSeconds.GetValueOrDefault(search, StartSeconds);
                if (foundNew)
                {
                    wait = Math.Max(MinSeconds, wait / 2);
                }
                else
                {
                    wait = Math.Min(MaxSeconds, wait * 1.5);
                }
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
    }

    public void Remove(string search)
    {
        lock (syncLock)
        {
            nextCheck.Remove(search);
            waitSeconds.Remove(search);
        }
    }
}

namespace MarktplaatsNotifier;

public static class AppFiles
{
    public static string DataDirectory { get; } = FindDataDirectory();

    public static string PathFor(string fileName) => Path.Combine(DataDirectory, fileName);

    private static string FindDataDirectory()
    {
        string? envDir = Environment.GetEnvironmentVariable("MARKTPLAATS_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            Directory.CreateDirectory(envDir);
            return envDir;
        }

        if (HasAppFiles(AppContext.BaseDirectory))
        {
            return AppContext.BaseDirectory;
        }

        if (HasAppFiles(Environment.CurrentDirectory))
        {
            return Environment.CurrentDirectory;
        }

        // When launched from bin/Debug/net10.0 (e.g. Rider's Run button),
        // walk up to the project folder if a .csproj or searches.txt lives there.
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 4 && dir?.Parent != null; i++)
        {
            dir = dir.Parent;
            if (HasAppFiles(dir.FullName) || File.Exists(Path.Combine(dir.FullName, "MarktplaatsNotifier.csproj")))
            {
                return dir.FullName;
            }
        }

        return AppContext.BaseDirectory;
    }

    private static bool HasAppFiles(string directory)
    {
        return File.Exists(Path.Combine(directory, "secrets.txt")) ||
               File.Exists(Path.Combine(directory, "searches.txt"));
    }
}

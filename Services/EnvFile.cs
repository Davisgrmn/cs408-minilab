namespace AssignmentTracker.Services;

public static class EnvFile
{
    // Supports KEY=value and quoted values without overwriting existing environment variables.
    public static void Load(string path)
    {
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;
            var split = text.IndexOf('=');
            if (split <= 0) continue;
            var key = text[..split].Trim();
            var value = text[(split + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') ||
                (value[0] == '\'' && value[^1] == '\''))) value = value[1..^1];
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}

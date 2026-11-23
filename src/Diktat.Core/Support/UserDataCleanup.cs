namespace Diktat.Core.Support;

/// "Alle Daten löschen": removes the folders Diktat writes to, each as a whole. Like on the Mac,
/// the downloaded models go too: they lie in one of these folders.
public static class UserDataCleanup
{
    public sealed record Failure(string Path, string Error);

    public sealed record Report(IReadOnlyList<string> Removed, IReadOnlyList<Failure> Failed)
    {
        public bool Succeeded => Failed.Count == 0;
    }

    /// Diktat's folder in each of the given base folders. On Windows: %APPDATA% (settings,
    /// history, meetings, API keys), %LOCALAPPDATA% (models, logs) and %TEMP% (meeting audio,
    /// temporary audio). An empty or relative base is skipped, so a missing environment
    /// variable never points the removal at the working directory.
    public static IReadOnlyList<string> Folders(string appFolderName, params string?[] baseFolders) =>
        baseFolders
            .Where(b => !string.IsNullOrWhiteSpace(b) && Path.IsPathFullyQualified(b))
            .Select(b => Path.GetFullPath(Path.Combine(b!, appFolderName)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// Removes every path that exists, a folder with all it holds. A file someone else holds
    /// open (a virus scanner, the search index) gets a few more tries. What cannot be removed
    /// is reported, the rest still goes.
    public static Report Remove(IEnumerable<string> paths, int attempts = 3, TimeSpan? pause = null)
    {
        var removed = new List<string>();
        var failed = new List<Failure>();
        foreach (var path in paths)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    RemoveIfExists(path);
                    removed.Add(path);
                    break;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= attempts)
                    {
                        failed.Add(new Failure(path, e.Message));
                        break;
                    }
                    Thread.Sleep(pause ?? TimeSpan.FromMilliseconds(300));
                }
            }
        }
        return new Report(removed, failed);
    }

    private static void RemoveIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return;
        }
        if (!Directory.Exists(path)) return;
        // Windows refuses to delete read-only files; clear the flag first. Links are not followed:
        // what they point to is not Diktat's.
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(path, "*", options))
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(path, recursive: true);
    }
}

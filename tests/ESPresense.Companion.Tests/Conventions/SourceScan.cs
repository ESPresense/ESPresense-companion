using System.Text.RegularExpressions;

namespace ESPresense.Companion.Tests.Conventions;

/// <summary>
/// Reads the backend sources under <c>src/</c> so convention tests can fail the build when a pattern
/// spreads to a file that isn't allowed to use it.
/// </summary>
internal static class SourceScan
{
    private static readonly Lazy<string> SrcDir = new(() =>
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ESPresense-companion.sln")))
                return Path.Combine(dir.FullName, "src");
        throw new DirectoryNotFoundException("Could not find ESPresense-companion.sln above " + AppContext.BaseDirectory);
    });

    /// <summary>Every backend .cs file, as a path relative to <c>src/</c> with forward slashes.</summary>
    public static IEnumerable<(string Path, string Text)> BackendFiles()
    {
        var root = SrcDir.Value;
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.StartsWith("ui/") || rel.StartsWith("bin/") || rel.StartsWith("obj/")) continue;
            yield return (rel, File.ReadAllText(file));
        }
    }

    /// <summary>Files (relative to <c>src/</c>) whose code, with comments removed, matches <paramref name="pattern"/>.</summary>
    public static SortedSet<string> FilesMatching(Regex pattern) =>
        new(BackendFiles().Where(f => pattern.IsMatch(StripComments(f.Text))).Select(f => f.Path), StringComparer.Ordinal);

    private static readonly Regex Comments = new(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);

    // Good enough for a convention check: string literals containing "//" are rare in this codebase,
    // and stripping them as comments only ever hides a match, it can't invent one.
    private static string StripComments(string code) => Comments.Replace(code, "");
}

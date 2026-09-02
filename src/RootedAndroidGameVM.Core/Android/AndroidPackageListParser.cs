namespace RootedAndroidGameVM.Core.Android;

public static class AndroidPackageListParser
{
    public static IReadOnlyList<string> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var packages = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            const string prefix = "package:";
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var value = line[prefix.Length..].Trim();
            try
            {
                packages.Add(AndroidPackageName.Parse(value).Value);
            }
            catch (ArgumentException)
            {
                // Ignore malformed package-manager output rather than passing it to a shell command.
            }
        }

        return packages.ToArray();
    }
}

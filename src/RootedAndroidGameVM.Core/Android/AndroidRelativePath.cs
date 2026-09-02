using System.Text.RegularExpressions;

namespace RootedAndroidGameVM.Core.Android;

public sealed partial record AndroidRelativePath
{
    private AndroidRelativePath(string value) => Value = value;

    public string Value { get; }

    public static AndroidRelativePath Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Replace('\\', '/');
        if (!RelativePathPattern().IsMatch(normalized) ||
            normalized.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Invalid Android relative data path.", nameof(value));
        }

        return new(normalized);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex RelativePathPattern();
}

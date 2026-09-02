using System.Text.RegularExpressions;

namespace RootedAndroidGameVM.Core.Android;

public sealed partial record AndroidPackageName
{
    private AndroidPackageName(string value) => Value = value;

    public string Value { get; }

    public static AndroidPackageName Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!PackagePattern().IsMatch(value))
        {
            throw new ArgumentException("Invalid Android package name.", nameof(value));
        }

        return new(value);
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex PackagePattern();
}

using System.Text.RegularExpressions;

namespace RootedAndroidGameVM.Core.Setup;

public sealed partial record SetupE2eOptions(
    string ProductRoot,
    string AvdName,
    int Port,
    string Serial)
{
    public static SetupE2eOptions? TryParse(
        IReadOnlyList<string> arguments,
        Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(readEnvironment);
        if (!arguments.Contains("--e2e", StringComparer.Ordinal)) return null;
        if (readEnvironment("RGVM_E2E_ACCEPT_SDK_LICENSE") != "1")
        {
            throw new InvalidOperationException(
                "E2E mode requires RGVM_E2E_ACCEPT_SDK_LICENSE=1.");
        }

        var productRoot = RequiredValue(arguments, "--product-root");
        var avdName = RequiredValue(arguments, "--avd-name");
        if (!AvdNamePattern().IsMatch(avdName))
        {
            throw new ArgumentException("E2E AVD name contains unsupported characters.");
        }
        if (!int.TryParse(RequiredValue(arguments, "--port"), out var port) ||
            port < 5554 ||
            port > 5682 ||
            port % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(arguments), "E2E emulator port must be even and between 5554 and 5682.");
        }

        var fullRoot = Path.GetFullPath(productRoot);
        if (string.Equals(fullRoot, Path.GetPathRoot(fullRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("E2E product root cannot be a drive root.");
        }
        return new(fullRoot, avdName, port, $"emulator-{port}");
    }

    private static string RequiredValue(IReadOnlyList<string> arguments, string name)
    {
        var index = arguments.IndexOf(name);
        if (index < 0 || index + 1 >= arguments.Count ||
            string.IsNullOrWhiteSpace(arguments[index + 1]))
        {
            throw new ArgumentException($"Missing required E2E argument '{name}'.");
        }
        return arguments[index + 1];
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex AvdNamePattern();
}

file static class ReadOnlyListExtensions
{
    public static int IndexOf(this IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], value, StringComparison.Ordinal)) return index;
        }
        return -1;
    }
}

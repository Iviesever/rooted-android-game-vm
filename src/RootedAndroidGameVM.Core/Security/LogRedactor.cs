namespace RootedAndroidGameVM.Core.Security;

public static class LogRedactor
{
    public static string RedactLocalPaths(string input) =>
        Redact(
            input,
            [
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.UserName
            ]);

    public static string Redact(string input, IEnumerable<string> sensitiveValues)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(sensitiveValues);

        var output = input;
        foreach (var value in sensitiveValues
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length))
        {
            output = output.Replace(value, "[REDACTED]", StringComparison.OrdinalIgnoreCase);
        }

        return output;
    }
}

namespace RootedAndroidGameVM.Core.Android;

public static class AvdConfigEditor
{
    public static async Task UpsertAsync(
        string path,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("AVD config.ini is missing.", path);
        var remaining = new Dictionary<string, string>(values, StringComparer.Ordinal);
        var output = new List<string>();
        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                output.Add(line);
                continue;
            }

            var key = line[..separator];
            if (!values.ContainsKey(key))
            {
                output.Add(line);
                continue;
            }

            if (remaining.Remove(key, out var replacement))
            {
                output.Add($"{key}={replacement}");
            }
        }

        output.AddRange(remaining.Select(pair => $"{pair.Key}={pair.Value}"));
        await File.WriteAllLinesAsync(path, output, cancellationToken);
    }
}

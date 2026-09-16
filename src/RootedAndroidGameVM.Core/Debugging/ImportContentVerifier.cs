using System.Text.Json;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record ImportVerification(bool Verified, string Target, int VerifiedFiles, int ExpectedFiles, string? Reason,
    string[] MissingFiles, string[] DifferentFiles, string[] MetadataRewrites, string[] ExtraFiles, bool TargetExists = false);

public sealed record ImportHashEnumeration(Dictionary<string, string> Files, string[] ConflictingPaths, int DuplicateRows, bool TargetExists);

public static class ImportContentVerifier
{
    public static ImportHashEnumeration ParseHashes(string raw, string target)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicts = new HashSet<string>(StringComparer.Ordinal); var duplicates = 0; var exists = false;
        foreach (var line in raw.Split('\n').Select(line => line.TrimEnd('\r')))
        {
            if (line == "RGVM_DIRECTORY_PRESENT") { exists = true; continue; }
            if (line.Length == 0) continue;
            if (line.Length <= 66 || !System.Text.RegularExpressions.Regex.IsMatch(line[..64], "^[a-fA-F0-9]{64}$") ||
                !line[66..].StartsWith(target + "/", StringComparison.Ordinal)) throw new InvalidDataException("解包文件散列输出格式无效。");
            var name = line[(67 + target.Length)..]; var hash = line[..64];
            if (!files.TryAdd(name, hash)) { duplicates++; if (files[name] != hash) conflicts.Add(name); }
        }
        return new(files, conflicts.Order().ToArray(), duplicates, exists);
    }

    public static ImportVerification Compare(string target, IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual, bool knownMetadataRewrite = false)
    {
        var missing = expected.Keys.Where(name => !actual.ContainsKey(name)).Order().ToArray();
        var different = expected.Where(pair => actual.TryGetValue(pair.Key, out var hash) &&
            !pair.Value.Equals(hash, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).Order().ToArray();
        var metadata = knownMetadataRewrite && different.Contains("info.json") ? new[] { "info.json" } : [];
        different = different.Except(metadata).ToArray();
        var extra = actual.Keys.Except(expected.Keys).Order().ToArray();
        var verified = expected.Count > 0 && missing.Length == 0 && different.Length == 0 && extra.Length == 0;
        return new(verified, target, expected.Count(pair => actual.TryGetValue(pair.Key, out var hash) &&
            pair.Value.Equals(hash, StringComparison.OrdinalIgnoreCase)), expected.Count,
            verified ? metadata.Length > 0 ? "known_metadata_rewrite" : null :
            different.Length > 0 || extra.Length > 0 ? "content_mismatch" : "unpacking_incomplete",
            missing, different, metadata, extra);
    }

    // Malody regenerates the catalogue info.json; never ignore the whole file by name.
    // Existing fields (including signatures) must remain semantically identical, except the
    // observed Malody 6.6.12 catalogue version rewrite 393216 -> 394764. Only these
    // client catalogue additions are accepted, with bounded types. Layout/script/resources
    // and unexpected files still require exact SHA-256 equality.
    public static bool IsKnownMetadataRewrite(string expected, string actual)
    {
        if (expected.Length > 65536 || actual.Length > 65536) return false;
        try
        {
            using var left = JsonDocument.Parse(expected); using var right = JsonDocument.Parse(actual);
            if (left.RootElement.ValueKind != JsonValueKind.Object || right.RootElement.ValueKind != JsonValueKind.Object) return false;
            var source = Properties(left.RootElement); var target = Properties(right.RootElement);
            if (source is null || target is null || source.Count == 0) return false;
            foreach (var (name, value) in source)
            {
                if (!target.TryGetValue(name, out var next)) return false;
                if (JsonElement.DeepEquals(value, next)) continue;
                if (name == "version" && value.ValueKind == JsonValueKind.Number && next.ValueKind == JsonValueKind.Number &&
                    value.TryGetInt64(out var before) && next.TryGetInt64(out var after) && before == 393216 && after == 394764) continue;
                return false;
            }
            foreach (var (name, value) in target.Where(pair => !source.ContainsKey(pair.Key)))
            {
                var known = name switch
                {
                    "title" or "creator" or "desc" or "cover" => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 4096,
                    "version" or "mode" or "key" or "feature" or "skinid" or "updatedTime" => value.TryGetInt64(out var number) && number >= 0,
                    "special" => value.ValueKind == JsonValueKind.Null,
                    _ => false
                };
                if (!known) return false;
            }
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return false; }
    }

    private static Dictionary<string, JsonElement>? Properties(JsonElement value)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!result.TryAdd(property.Name, property.Value)) return null;
        return result;
    }
}

using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RootedAndroidGameVM.Core.Android;

public readonly record struct AndroidUiPoint(int X, int Y);

public sealed partial class AndroidUiSnapshot
{
    private readonly XDocument _document;

    private AndroidUiSnapshot(XDocument document) => _document = document;

    public static AndroidUiSnapshot Parse(string rawXml)
    {
        if (string.IsNullOrWhiteSpace(rawXml))
        {
            throw new InvalidDataException("UIAutomator returned an empty hierarchy.");
        }
        var start = rawXml.IndexOf('<');
        var end = rawXml.LastIndexOf("</hierarchy>", StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            throw new InvalidDataException("UIAutomator output did not contain a hierarchy document.");
        }

        var xml = rawXml[start..(end + "</hierarchy>".Length)];
        try
        {
            return new AndroidUiSnapshot(XDocument.Parse(xml));
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("UIAutomator returned a transient malformed hierarchy.", exception);
        }
    }

    public bool Contains(string label) =>
        FindNode(label, requireEnabled: false) is not null;

    public AndroidUiPoint FindCenter(string label)
    {
        var node = FindNode(label, requireEnabled: true)
            ?? throw new InvalidOperationException($"Android UI element '{label}' was not found or enabled.");
        return GetCenter(node, label);
    }

    public bool TryFindCenter(string label, out AndroidUiPoint point) =>
        TryGetCenter(FindNode(label, requireEnabled: true), out point);

    public AndroidUiPoint FindCenterByResourceId(string resourceId)
    {
        var node = FindByResourceId(resourceId);
        return GetCenter(node, resourceId);
    }

    public bool TryFindCenterByResourceId(string resourceId, out AndroidUiPoint point) =>
        TryGetCenter(FindNodeByResourceId(resourceId), out point);

    public bool IsCheckedByResourceId(string resourceId) =>
        FindByResourceId(resourceId).DescendantsAndSelf().Any(node =>
            string.Equals(node.Attribute("checked")?.Value, "true", StringComparison.Ordinal));

    public string DescribeVisibleLabels() =>
        string.Join(
            " | ",
            _document.Descendants("node")
                .SelectMany(node => new[]
                {
                    node.Attribute("text")?.Value,
                    node.Attribute("content-desc")?.Value
                })
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .Take(20));

    private static AndroidUiPoint GetCenter(XElement node, string label)
    {
        var bounds = node.Attribute("bounds")?.Value ?? string.Empty;
        if (!TryGetCenter(node, out var point))
        {
            throw new InvalidDataException($"Android UI element '{label}' has invalid bounds '{bounds}'.");
        }
        return point;
    }

    private static bool TryGetCenter(XElement? node, out AndroidUiPoint point)
    {
        point = default;
        var match = BoundsPattern().Match(node?.Attribute("bounds")?.Value ?? string.Empty);
        if (!match.Success ||
            !int.TryParse(match.Groups["left"].Value, out var left) ||
            !int.TryParse(match.Groups["top"].Value, out var top) ||
            !int.TryParse(match.Groups["right"].Value, out var right) ||
            !int.TryParse(match.Groups["bottom"].Value, out var bottom) ||
            right <= left ||
            bottom <= top)
        {
            return false;
        }

        point = new AndroidUiPoint(left + ((right - left) / 2), top + ((bottom - top) / 2));
        return true;
    }

    private XElement? FindNode(string label, bool requireEnabled) =>
        _document.Descendants("node").FirstOrDefault(node =>
            (!requireEnabled || node.Attribute("enabled")?.Value != "false") &&
            (string.Equals(node.Attribute("text")?.Value, label, StringComparison.Ordinal) ||
             string.Equals(node.Attribute("content-desc")?.Value, label, StringComparison.Ordinal)));

    private XElement? FindNodeByResourceId(string resourceId) =>
        _document.Descendants("node").FirstOrDefault(candidate =>
            candidate.Attribute("enabled")?.Value != "false" &&
            string.Equals(candidate.Attribute("resource-id")?.Value, resourceId, StringComparison.Ordinal));

    private XElement FindByResourceId(string resourceId) =>
        FindNodeByResourceId(resourceId) ?? throw new InvalidOperationException(
            $"Android UI resource '{resourceId}' was not found or enabled.");

    [GeneratedRegex(@"^\[(?<left>\d+),(?<top>\d+)\]\[(?<right>\d+),(?<bottom>\d+)\]$")]
    private static partial Regex BoundsPattern();
}

using System.Xml.Linq;

namespace DotnetLift.Core.Upgrade;

/// <summary>Helpers for editing project files while keeping their original formatting.</summary>
internal static class ProjectXml
{
    public static XDocument Load(string path) => XDocument.Load(path, LoadOptions.PreserveWhitespace);

    public static string Serialize(XDocument doc)
    {
        var body = doc.Root!.ToString(SaveOptions.DisableFormatting);
        var text = doc.Declaration is null ? body : doc.Declaration + Environment.NewLine + body;
        return text.EndsWith('\n') ? text : text + Environment.NewLine;
    }

    public static IEnumerable<XElement> Elements(XElement root, string localName) =>
        root.Descendants().Where(e => e.Name.LocalName == localName);

    public static string? Child(XElement e, string localName) =>
        e.Elements().FirstOrDefault(c => c.Name.LocalName == localName)?.Value.Trim();

    /// <summary>Removes an element together with the whitespace in front of it, so no blank line is left behind.</summary>
    public static void Remove(XElement e)
    {
        if (e.PreviousNode is XText text && string.IsNullOrWhiteSpace(text.Value))
        {
            text.Remove();
        }
        e.Remove();
    }

    public static void RemoveEmptyItemGroups(XElement root)
    {
        foreach (var group in Elements(root, "ItemGroup").Where(g => !g.Elements().Any()).ToList())
        {
            Remove(group);
        }
    }

    /// <summary>The indentation used for direct children of the root element ("  " if it cannot be detected).</summary>
    public static string ChildIndent(XElement root)
    {
        var first = root.Nodes().OfType<XText>().FirstOrDefault(t => t.Value.Contains('\n'));
        if (first is null)
        {
            return "  ";
        }
        var afterNewline = first.Value[(first.Value.LastIndexOf('\n') + 1)..];
        return afterNewline.Length > 0 && string.IsNullOrWhiteSpace(afterNewline) ? afterNewline : "  ";
    }
}

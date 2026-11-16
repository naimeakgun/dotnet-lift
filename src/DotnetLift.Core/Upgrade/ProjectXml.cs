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

    /// <summary>Adds an item group after the last property/item group, separated by a blank line.</summary>
    public static void AddItemGroup(XElement root, XElement group, IEnumerable<XElement> items)
    {
        var indent = ChildIndent(root);
        foreach (var item in items)
        {
            group.Add(new XText(Environment.NewLine + indent + indent), item);
        }
        group.Add(new XText(Environment.NewLine + indent));

        var anchor = root.Elements().LastOrDefault(e => e.Name.LocalName is "ItemGroup" or "PropertyGroup");
        if (anchor is not null)
        {
            anchor.AddAfterSelf(new XText(Environment.NewLine + Environment.NewLine + indent), group);
        }
        else
        {
            root.Add(new XText(Environment.NewLine + indent), group, new XText(Environment.NewLine));
        }
    }

    /// <summary>Appends an item to an existing group, keeping the group's closing tag on its own line.</summary>
    public static void AddToItemGroup(XElement root, XElement group, XElement item)
    {
        var indent = ChildIndent(root);
        if (group.LastNode is XText trailing && string.IsNullOrWhiteSpace(trailing.Value))
        {
            trailing.AddBeforeSelf(new XText(Environment.NewLine + indent + indent), item);
        }
        else
        {
            group.Add(new XText(Environment.NewLine + indent + indent), item, new XText(Environment.NewLine + indent));
        }
    }

    public static string TargetFrameworkCondition(string tfm) => $"'$(TargetFramework)' == '{tfm}'";

    /// <summary>
    /// Adds an item to the item group for <paramref name="conditionTfm"/> (creating it if needed),
    /// or, without a target, to the first unconditional group holding the same kind of item.
    /// </summary>
    public static void AddItem(XElement root, string? conditionTfm, XElement item)
    {
        var condition = conditionTfm is null ? null : TargetFrameworkCondition(conditionTfm);
        var group = Elements(root, "ItemGroup").FirstOrDefault(g =>
            condition is null
                ? g.Attribute("Condition") is null && g.Elements().Any(e => e.Name.LocalName == item.Name.LocalName)
                : g.Attribute("Condition")?.Value == condition);

        if (group is null)
        {
            group = condition is null
                ? new XElement(root.Name.Namespace + "ItemGroup")
                : new XElement(root.Name.Namespace + "ItemGroup", new XAttribute("Condition", condition));
            AddItemGroup(root, group, [item]);
        }
        else
        {
            AddToItemGroup(root, group, item);
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

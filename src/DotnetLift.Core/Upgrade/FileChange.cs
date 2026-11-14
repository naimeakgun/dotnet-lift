namespace DotnetLift.Core.Upgrade;

/// <summary>A pending change to one file. <see cref="NewContent"/> null means the file is deleted.</summary>
public sealed record FileChange(string Path, string? NewContent)
{
    public bool IsDelete => NewContent is null;
    public bool IsCreate => !IsDelete && !File.Exists(Path);

    public string? ReadOriginal() => File.Exists(Path) ? File.ReadAllText(Path) : null;

    public void Apply()
    {
        if (NewContent is null)
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
            return;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, NewContent);
    }

    public static void ApplyAll(IEnumerable<FileChange> changes)
    {
        foreach (var change in changes)
        {
            change.Apply();
        }
    }
}

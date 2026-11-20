namespace DotnetLift.Core.Upgrade;

/// <summary>
/// A pending change to one file. <see cref="NewContent"/> null means the file is deleted,
/// unless <see cref="MovedFrom"/> is set: then the file at that path is moved to <see cref="Path"/> byte for byte.
/// </summary>
public sealed record FileChange(string Path, string? NewContent)
{
    /// <summary>The current location of a moved file; null for edits, creations and deletions.</summary>
    public string? MovedFrom { get; init; }

    /// <summary>Moves a file without reading it, so binary files (fonts, images) are safe.</summary>
    public static FileChange Move(string source, string destination) => new(destination, null) { MovedFrom = source };

    public bool IsMove => MovedFrom is not null;
    public bool IsDelete => NewContent is null && !IsMove;
    public bool IsCreate => !IsDelete && !IsMove && !File.Exists(Path);

    /// <summary>Every path this change touches, for staging: a move touches its old and its new location.</summary>
    public IEnumerable<string> Paths => MovedFrom is { } source ? new[] { source, Path } : new[] { Path };

    public string? ReadOriginal() => File.Exists(Path) ? File.ReadAllText(Path) : null;

    public void Apply()
    {
        if (MovedFrom is { } source)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.Move(source, Path);
            RemoveEmptyDirectories(System.IO.Path.GetDirectoryName(source));
            return;
        }

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

    /// <summary>A folder emptied by moves goes too (git does not keep empty folders either); stops at the first folder that still has something.</summary>
    private static void RemoveEmptyDirectories(string? directory)
    {
        while (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = System.IO.Path.GetDirectoryName(directory);
        }
    }
}

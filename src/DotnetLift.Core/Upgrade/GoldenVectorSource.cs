namespace DotnetLift.Core.Upgrade;

/// <summary>
/// The golden-vector helper `add-tests` writes into a test project: outputs are recorded on .NET Framework into the
/// project's golden/ folder and compared byte for byte on modern .NET. Written as C# 7.3 with no test-framework
/// dependency (a mismatch is an exception, which MSTest, xUnit and NUnit all report as a failure).
/// </summary>
public static class GoldenVectorSource
{
    public const string FileName = "GoldenVector.cs";
    public const string FolderName = "golden";

    /// <summary>The AssemblyMetadata key the project file sets to its own folder, so the helper finds golden/ from the test output.</summary>
    public const string MetadataKey = "GoldenVectorProjectDirectory";

    /// <summary>The environment variable that re-records vectors instead of comparing them: DOTNET_LIFT_GOLDEN=record.</summary>
    public const string RecordVariable = "DOTNET_LIFT_GOLDEN";

    /// <summary>Keeps git from changing line endings in recorded text, which would break the byte comparison.</summary>
    public const string GitAttributes = "# Golden vectors are compared byte for byte: never convert line endings.\n* -text\n";

    /// <summary>The project-file item that records the project folder in the test assembly.</summary>
    public static string ProjectItem =>
        $"""
            <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
              <_Parameter1>{MetadataKey}</_Parameter1>
              <_Parameter2>$(MSBuildProjectDirectory)</_Parameter2>
            </AssemblyAttribute>
        """;

    public static string Source(string ns)
    {
        var text = Template
            .Replace("__NAMESPACE__", ns, StringComparison.Ordinal)
            .Replace("__KEY__", MetadataKey, StringComparison.Ordinal)
            .Replace("__VARIABLE__", RecordVariable, StringComparison.Ordinal)
            .Replace("__FOLDER__", FolderName, StringComparison.Ordinal);
        return text.ReplaceLineEndings(Environment.NewLine);
    }

    private const string Template = """
        using System;
        using System.IO;
        using System.Linq;
        using System.Reflection;
        using System.Runtime.InteropServices;
        using System.Text;
        using System.Text.RegularExpressions;

        namespace __NAMESPACE__
        {
            /// <summary>
            /// Golden vectors: what the code produces on .NET Framework, kept in golden/ and compared byte for byte on
            /// modern .NET. Use them for outputs that must not change when the framework does: hashes, encoded bytes,
            /// serialized data, formatted text.
            ///
            ///   GoldenVector.Check("password-hash-turkish", PasswordHasher.Hash("İSMAİL", "şifre"));
            ///   GoldenVector.Check("snapshot-bytes", SnapshotSerializer.Save(order));
            ///
            /// On .NET Framework a missing vector is recorded (run `dotnet test -f net48` before the library moves and
            /// commit golden/ with the tests); a recorded one is compared there too, so the output must be stable.
            /// On modern .NET a missing vector fails: it was never recorded on the old framework.
            /// Set __VARIABLE__=record to re-record every vector the tests check (only when a change is intended).
            /// </summary>
            public static class GoldenVector
            {
                private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
                private static readonly Regex ValidName = new Regex("^[A-Za-z0-9][A-Za-z0-9._-]*$");
                private static readonly object Gate = new object();

                /// <summary>Text, stored as UTF-8 in golden/NAME.txt.</summary>
                public static void Check(string name, string value)
                {
                    if (value == null)
                    {
                        throw new ArgumentNullException("value");
                    }

                    byte[] bytes;
                    try
                    {
                        bytes = StrictUtf8.GetBytes(value);
                    }
                    catch (ArgumentException)
                    {
                        throw new ArgumentException(
                            "The text has an unpaired surrogate, which UTF-8 cannot store exactly. " +
                            "Check its UTF-16 bytes instead: GoldenVector.Check(name, Encoding.Unicode.GetBytes(value)).", "value");
                    }
                    Compare(name, ".txt", bytes, true);
                }

                /// <summary>Bytes, stored as they are in golden/NAME.bin.</summary>
                public static void Check(string name, byte[] value)
                {
                    if (value == null)
                    {
                        throw new ArgumentNullException("value");
                    }
                    Compare(name, ".bin", value, false);
                }

                private static void Compare(string name, string extension, byte[] actual, bool text)
                {
                    if (name == null || !ValidName.IsMatch(name))
                    {
                        throw new ArgumentException("A golden vector name uses letters, digits, '.', '_' and '-' (it becomes a file name): " + name, "name");
                    }

                    var directory = Folder();
                    var path = Path.Combine(directory, name + extension);
                    var record = string.Equals(Environment.GetEnvironmentVariable("__VARIABLE__"), "record", StringComparison.OrdinalIgnoreCase);

                    lock (Gate)
                    {
                        if (record || !File.Exists(path))
                        {
        #if NETFRAMEWORK
                            Write(directory, path, actual);
                            return;
        #else
                            if (record)
                            {
                                Write(directory, path, actual);
                                return;
                            }
                            throw new GoldenVectorException(
                                "No golden vector " + name + extension + " in " + directory + ". Vectors are recorded on .NET Framework: " +
                                "run the tests there first (dotnet test -f net48) and commit the golden folder with them.");
        #endif
                        }

                        var expected = File.ReadAllBytes(path);
                        if (!expected.SequenceEqual(actual))
                        {
                            throw new GoldenVectorException(Describe(name + extension, expected, actual, text));
                        }
                    }
                }

                private static void Write(string directory, string path, byte[] bytes)
                {
                    Directory.CreateDirectory(directory);
                    var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllBytes(temp, bytes);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    File.Move(temp, path);
                }

                /// <summary>The golden folder of the test project: from the project file's metadata, else the nearest folder with a .csproj above the output.</summary>
                private static string Folder()
                {
                    var metadata = typeof(GoldenVector).Assembly
                        .GetCustomAttributes(typeof(AssemblyMetadataAttribute), false)
                        .Cast<AssemblyMetadataAttribute>()
                        .FirstOrDefault(a => a.Key == "__KEY__");
                    if (metadata != null && !string.IsNullOrEmpty(metadata.Value) && Directory.Exists(metadata.Value))
                    {
                        return Path.Combine(metadata.Value, "__FOLDER__");
                    }

                    for (var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); dir != null; dir = dir.Parent)
                    {
                        if (dir.GetFiles("*.csproj").Length > 0)
                        {
                            return Path.Combine(dir.FullName, "__FOLDER__");
                        }
                    }
                    throw new GoldenVectorException("Cannot find the test project folder: add the __KEY__ assembly metadata to the project file.");
                }

                private static string Describe(string file, byte[] expected, byte[] actual, bool text)
                {
                    var offset = 0;
                    while (offset < expected.Length && offset < actual.Length && expected[offset] == actual[offset])
                    {
                        offset++;
                    }

                    var sb = new StringBuilder();
                    sb.Append("Golden vector ").Append(file).Append(" differs on ").Append(RuntimeInformation.FrameworkDescription).AppendLine(".");
                    sb.Append("  expected ").Append(expected.Length).Append(" bytes, got ").Append(actual.Length)
                      .Append("; first difference at byte ").Append(offset).AppendLine(".");
                    var start = Math.Max(0, offset - 8);
                    sb.Append("  expected: ").AppendLine(Hex(expected, start));
                    sb.Append("  actual:   ").AppendLine(Hex(actual, start));
                    if (text)
                    {
                        sb.Append("  expected text: ").AppendLine(Escape(expected));
                        sb.Append("  actual text:   ").AppendLine(Escape(actual));
                    }
                    sb.Append("If the new output is intended, re-record with __VARIABLE__=record; otherwise this is a behaviour change the migration introduced.");
                    return sb.ToString();
                }

                private static string Hex(byte[] bytes, int start)
                {
                    var count = Math.Min(24, Math.Max(0, bytes.Length - start));
                    var hex = BitConverter.ToString(bytes, Math.Min(start, bytes.Length), count).Replace("-", " ");
                    return (start > 0 ? "... " : "") + hex + (start + count < bytes.Length ? " ..." : "");
                }

                /// <summary>The text with control and non-ASCII characters escaped, so look-alike characters (İ, ı, �) are visible.</summary>
                private static string Escape(byte[] bytes)
                {
                    var value = Encoding.UTF8.GetString(bytes);
                    if (value.Length > 200)
                    {
                        value = value.Substring(0, 200) + "...";
                    }

                    var sb = new StringBuilder("\"");
                    foreach (var c in value)
                    {
                        if (c >= 32 && c < 127 && c != '"' && c != '\\')
                        {
                            sb.Append(c);
                        }
                        else
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                    }
                    return sb.Append('"').ToString();
                }
            }

            /// <summary>A golden vector that is missing on modern .NET, or differs from what was recorded.</summary>
            public sealed class GoldenVectorException : Exception
            {
                public GoldenVectorException(string message) : base(message)
                {
                }
            }
        }

        """;
}

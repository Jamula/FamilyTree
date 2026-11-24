using System.Security.Cryptography;
using System.Text;
using FamilyTree.Persistence;

if (args.Length != 7)
    return Usage();

var action = args[0];
var sourceOption = action == "backup" ? "--database" : "--backup";
var targetOption = action == "backup" ? "--output" : "--database";
if (action is not ("backup" or "restore") ||
    args[1] != sourceOption || args[3] != targetOption || args[5] != "--key-file")
    return Usage();

byte[]? key = null;
try
{
    var keyPath = ResolveKeyPath(args[6], args[2], args[4]);
    key = File.ReadAllBytes(keyPath);
    if (key.Length != 32)
        throw new InvalidDataException();

    if (action == "backup")
        SqliteRecovery.CreateBackup(args[2], args[4], key);
    else
        SqliteRecovery.Restore(args[2], args[4], key);

    Console.WriteLine(action == "backup" ? "Encrypted backup created." : "Database restored.");
    return 0;
}
catch (Exception)
{
    Console.Error.WriteLine("Recovery failed; verify the paths, key and snapshot.");
    return 2;
}
finally
{
    if (key is not null)
        CryptographicOperations.ZeroMemory(key);
}

static int Usage()
{
    Console.Error.WriteLine(
        "Usage: backup --database <file> --output <encrypted-file> --key-file <32-byte-file>");
    Console.Error.WriteLine(
        "   or: restore --backup <encrypted-file> --database <new-file> --key-file <32-byte-file>");
    return 2;
}

static string ResolveKeyPath(string keyPath, string sourcePath, string targetPath)
{
    if (string.IsNullOrWhiteSpace(keyPath) || !Path.IsPathFullyQualified(keyPath))
        throw new ArgumentException();
    var fullPath = Path.GetFullPath(keyPath);
    var root = Path.GetPathRoot(fullPath)!;
    var current = root;
    foreach (var segment in fullPath[root.Length..].Split(
                 [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                 StringSplitOptions.RemoveEmptyEntries))
    {
        current = JoinChildPath(current, segment);
        var entry = new FileInfo(current);
        if (entry.LinkTarget is not null)
            throw new IOException();
        if ((File.Exists(current) || Directory.Exists(current)) &&
            (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new IOException();
    }
    if (!File.Exists(fullPath) ||
        (File.GetAttributes(fullPath) & FileAttributes.Directory) != 0)
        throw new FileNotFoundException();

    foreach (var path in new[] { sourcePath, targetPath })
        if (string.Equals(fullPath.Normalize(NormalizationForm.FormC),
                Path.GetFullPath(path).Normalize(NormalizationForm.FormC),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException();
    return fullPath;
}

static string JoinChildPath(string directory, string child)
{
    if (string.IsNullOrEmpty(child) || child is "." or ".." ||
        Path.IsPathFullyQualified(child) || Path.GetFileName(child) != child ||
        child.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        throw new IOException();
    return Path.Join(directory, child);
}

using System.Text.Json;

internal static class PwaManifestAssertions
{
    public static void Run()
    {
        var root = Path.GetFullPath("wwwroot", AppContext.BaseDirectory);
        var path = AssetPath(root, "manifest.webmanifest");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var manifest = document.RootElement;

        Check(manifest.GetProperty("id").GetString() == "/", "PWA identity stays at the site root");
        Check(manifest.GetProperty("start_url").GetString() == "/", "PWA opens the public landing page");
        Check(manifest.GetProperty("scope").GetString() == "/", "PWA scope stays at the site root");
        Check(manifest.GetProperty("display").GetString() == "standalone", "PWA requests standalone display");

        var icons = manifest.GetProperty("icons").EnumerateArray().ToArray();
        Check(icons.Length == 1, "PWA manifest has one declared icon");
        Check(icons[0].GetProperty("src").GetString() == "/icons/familytree.svg",
            "PWA icon path is stable");
        Check(icons[0].GetProperty("sizes").GetString() == "any"
            && icons[0].GetProperty("type").GetString() == "image/svg+xml",
            "PWA icon declares its SVG format");
        Check(File.Exists(AssetPath(root, "icons/familytree.svg")),
            "PWA icon asset is copied with the manifest");

        foreach (var invalid in new[] { "", "/icons/familytree.svg", @"C:\icons\familytree.svg",
                     @"\\server\share\icon.svg", "../icon.svg", "icons/../../icon.svg",
                     "../wwwroot-other/icon.svg", "icons/../familytree.svg" })
        {
            var rejected = false;
            try
            {
                AssetPath(root, invalid);
            }
            catch (ArgumentException)
            {
                rejected = true;
            }

            Check(rejected, "PWA asset path must reject rooted and traversal paths");
        }

        Console.WriteLine("PASS PWA manifest contract (not browser installability)");
    }

    private static string AssetPath(string root, string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\') || relativePath.Contains(':')
            || relativePath.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Asset path must be a relative path beneath the asset root.", nameof(relativePath));
        }

        var fullPath = Path.GetFullPath(relativePath, root);
        var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootPrefix, comparison))
        {
            throw new ArgumentException("Asset path must stay beneath the asset root.", nameof(relativePath));
        }

        return fullPath;
    }

    private static void Check(bool accepted, string reason)
    {
        if (!accepted)
        {
            throw new Exception(reason);
        }
    }
}

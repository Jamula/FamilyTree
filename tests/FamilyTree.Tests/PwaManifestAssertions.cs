using System.Text.Json;

internal static class PwaManifestAssertions
{
    public static void Run()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "manifest.webmanifest");
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
        Check(File.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot", "icons", "familytree.svg")),
            "PWA icon asset is copied with the manifest");

        Console.WriteLine("PASS PWA manifest contract (not browser installability)");
    }

    private static void Check(bool accepted, string reason)
    {
        if (!accepted)
        {
            throw new Exception(reason);
        }
    }
}

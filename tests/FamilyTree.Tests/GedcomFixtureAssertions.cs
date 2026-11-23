using System.Text;

internal static class GedcomFixtureAssertions
{
    public static void Run()
    {
        var path = Path.Join(AppContext.BaseDirectory, "fixtures", "synthetic-family.ged.txt");
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        Ensure(lines.Length > 8, "fixture must contain a nontrivial example");
        Ensure(lines[0] == "0 HEAD" && lines[^1] == "0 TRLR", "GEDCOM dataset boundaries");
        Ensure(lines.Contains("1 GEDC") && lines.Contains("2 VERS 7.0"), "GEDCOM 7 version header");
        Ensure(lines.Count(line => line.EndsWith(" INDI", StringComparison.Ordinal)) == 3,
            "fixture includes three fictional individuals");
        Ensure(lines.Count(line => line.EndsWith(" FAM", StringComparison.Ordinal)) == 1,
            "fixture includes one fictional family");
        Ensure(lines.All(line => !line.Contains("\t", StringComparison.Ordinal)),
            "GEDCOM fixture uses space delimiters");
        Console.WriteLine("PASS 6 GEDCOM fixture smoke assertions (not standards conformance)");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

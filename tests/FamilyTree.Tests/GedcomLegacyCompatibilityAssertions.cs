using System.Runtime.CompilerServices;
using FamilyTree.Domain;

internal static class GedcomLegacyCompatibilityAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var fixture = File.ReadAllText(Path.Join(AppContext.BaseDirectory,
            "fixtures", "synthetic-family.ged.txt"));
        var converted = fixture.Replace("2 VERS 7.0",
            "2 VERS 5.5.1\n1 CHAR UTF-8", StringComparison.Ordinal);
        var report = GedcomLegacyCompatibility.Preview(converted);
        Check(report.Valid && report.People.Count == 3 && report.Families.Count == 1,
            "synthetic GEDCOM 5.5.1 subset preview retains people and family");
        Check(report.Warnings.Any(x => x.Contains("5.5.1", StringComparison.Ordinal)),
            "legacy file has explicit non-lossless warning");
        Check(converted.Contains("2 VERS 5.5.1", StringComparison.Ordinal),
            "compatibility preview never modifies original source");
        Check(GedcomLegacyCompatibility.Preview(fixture).Valid,
            "GEDCOM7 files pass unchanged");
        Check(GedcomLegacyCompatibility.Preview("\uFEFF" + fixture).Valid,
            "UTF8 BOM tolerated when otherwise valid");
        Check(!GedcomLegacyCompatibility.Preview(null).Valid &&
              !GedcomLegacyCompatibility.Preview("").Valid,
            "null or empty content denied");
        Check(!GedcomLegacyCompatibility.Preview(new string('X', 256_001)).Valid,
            "oversized legacy file denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "UTF-8", "ANSI", StringComparison.Ordinal)).Valid,
            "unsafe legacy ANSI decoding denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "1 CHAR UTF-8\n", "", StringComparison.Ordinal)).Valid,
            "unknown legacy encoding denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "5.5.1", "5.5.2", StringComparison.Ordinal)).Valid,
            "unknown legacy version denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "1 CHAR UTF-8", "2 VERS 5.5.1\n1 CHAR UTF-8",
            StringComparison.Ordinal)).Valid,
            "conflicting/duplicate version markers denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "0 TRLR", "0 END", StringComparison.Ordinal)).Valid,
            "missing trailer denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "@I3@ FAMC", "@I3@ FAMC", StringComparison.Ordinal)
            .Replace("1 FAMC @F1@", "1 FAMC @NONEXISTENT@", StringComparison.Ordinal)).Valid,
            "broken relationship pointers denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "1 NAME Alex /Example/", "1 NAME ",
            StringComparison.Ordinal)).Valid,
            "incomplete Person records denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "1 NAME Alex /Example/", "1 NAME Alex\tExample",
            StringComparison.Ordinal)).Valid,
            "tab control characters denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "1 NAME Alex /Example/", "1 NAME Alex\0Example",
            StringComparison.Ordinal)).Valid,
            "NUL control characters denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "1 NAME Alex /Example/", "1 NAME " + new string('X',1025),
            StringComparison.Ordinal)).Valid,
            "huge line rejected");
        Console.WriteLine("PASS GEDCOM 5.5.1 UTF8 bounded preview, round-trip and invalid-input tests");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using System.Runtime.CompilerServices;
using FamilyTree.Domain;

internal static class GedcomLegacyCompatibilityAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var fixture = File.ReadAllText(Path.Join(AppContext.BaseDirectory,
            "fixtures", "synthetic-family.ged.txt"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var converted = fixture.Replace("2 VERS 7.0",
            "2 VERS 5.5.1\n1 CHAR UTF-8", StringComparison.Ordinal);
        var report = GedcomLegacyCompatibility.Preview(converted);
        Check(report.Valid && report.People.Count == 3 && report.Families.Count == 1,
            "synthetic GEDCOM 5.5.1 subset preview retains people and family");
        Check(report.Warnings.Any(x => x.Contains("5.5.1", StringComparison.Ordinal)),
            "legacy file has explicit non-lossless warning");
        var withExporterVersion = converted.Replace(
            "1 GEDC\n2 VERS 5.5.1",
            "1 SOUR ExampleExporter\n2 VERS 1.0\n1 GEDC\n2 VERS 5.5.1",
            StringComparison.Ordinal);
        Check(GedcomLegacyCompatibility.Preview(withExporterVersion).Valid,
            "SOUR/VERS is exporter metadata, not a conflicting GEDCOM version");
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
            "1 FAMC @F1@", "1 FAMC @F1",
            StringComparison.Ordinal)).Valid,
            "missing pointer closing delimiter denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "1 FAMC @F1@", "1 FAMC @I1@",
            StringComparison.Ordinal)).Valid,
            "individual pointer cannot impersonate a family");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "1 FAMC @F1@", "01 FAMC @I1@",
            StringComparison.Ordinal)).Valid,
            "zero-padded level cannot bypass pointer-type validation");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "1 FAMC @F1@", "01 FAMC @F1",
            StringComparison.Ordinal)).Valid,
            "zero-padded level cannot bypass missing pointer terminator");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "1 FAMC @F1@", "2 FAMC @F1@",
            StringComparison.Ordinal)).Valid,
            "nested person-to-family pointer at wrong level denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "1 FAMC @F1@", "1 CHIL @I1@",
            StringComparison.Ordinal)).Valid,
            "family CHIL pointer cannot appear within an individual record");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "1 NAME Alex /Example/", "1 NAME Alex\rExample\r",
            StringComparison.Ordinal)).Valid,
            "stray CR control characters denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "1 NAME Alex /Example/", "1 NAME Alex\u0001Example",
            StringComparison.Ordinal)).Valid,
            "ASCII control character in names rejected");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "1 CHAR UTF-8", "1 CHAR",
            StringComparison.Ordinal)).Valid,
            "bare CHAR declaration denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "1 CHAR UTF-8", "1 CHAR UTF-8\n1 CHAR UTF-8",
            StringComparison.Ordinal)).Valid,
            "repeated charset declarations denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "2 VERS 5.5.1", "2 VERS 5.5.1\n2 VERS",
            StringComparison.Ordinal)).Valid,
            "bare duplicate GEDC version denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "0 TRLR", "0 HEAD\n0 TRLR",
            StringComparison.Ordinal)).Valid,
            "repeated HEAD denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "0 TRLR", "0 TRLR\n0 TRLR",
            StringComparison.Ordinal)).Valid,
            "interior trailer denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "0 TRLR", "0 HEAD bogus\n0 TRLR",
            StringComparison.Ordinal)).Valid,
            "additional HEAD with metadata denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "0 TRLR", "0 TRLR bogus\n0 TRLR",
            StringComparison.Ordinal)).Valid,
            "interior TRAILER with extra text denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "1 FAMC @F1@", "1 FAMC @F1@ ",
            StringComparison.Ordinal)).Valid,
            "trailing whitespace in pointer denied");
        Check(GedcomLegacyCompatibility.Preview(
            withExporterVersion.Replace("2 VERS 1.0", "2 VERS 5.5.1",
            StringComparison.Ordinal)).Valid,
            "source-version identical to GEDCOM version is not rewritten as format");
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
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "0 TRLR", "0 @X@ HEAD\\n0 TRLR", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)).Valid,
            "spoofed level-zero HEAD with cross-reference denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "0 TRLR", "0 @X@ TRLR\\n0 TRLR", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)).Valid,
            "spoofed level-zero TRLR with cross-reference denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "1 FAMC @F1@", "1 @X@ FAMC @I1@", StringComparison.Ordinal)).Valid,
            "level-one cross-reference cannot disguise a relationship pointer");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "1 FAMC @F1@", "1 @X@ FAMC @F1", StringComparison.Ordinal)).Valid,
            "level-one cross-reference cannot disguise an unterminated pointer");
        foreach (var source in new[] { converted, fixture })
        {
            foreach (var headerLink in new[] { "1 FAMC @MISSING", "1 CHIL @I1@",
                         "1 FAMS @F1@", "2 HUSB @I1@" })
                Check(!GedcomLegacyCompatibility.Preview(source.Replace(
                    "1 SOUR FamilyTreeSyntheticFixtures",
                    "1 SOUR FamilyTreeSyntheticFixtures\n" + headerLink,
                    StringComparison.Ordinal)).Valid,
                    "header relationship tags are rejected");

            var eventDetails = source.Replace("1 CHIL @I3@",
                "1 CHIL @I3@\n1 MARR\n2 HUSB\n3 AGE 30y\n2 WIFE\n3 AGE 29y",
                StringComparison.Ordinal);
            var eventReport = GedcomLegacyCompatibility.Preview(eventDetails);
            Check(eventReport.Valid && eventReport.Families.Single().ParentPointers.Count == 2 &&
                  eventReport.Warnings.Any(x => x.Contains("family", StringComparison.Ordinal)),
                "family event spouse ages are previewable but require review");
            foreach (var badDetail in new[] { "2 HUSB @I1@\n3 AGE 30y",
                         "2 HUSB @I1\n3 AGE 30y", "2 HUSB\n3 NOTE ignored",
                         "2 HUSB\n3 AGE " })
                Check(!GedcomLegacyCompatibility.Preview(eventDetails.Replace(
                    "2 HUSB\n3 AGE 30y", badDetail, StringComparison.Ordinal)).Valid,
                    "malformed spouse details are rejected");
            Check(!GedcomLegacyCompatibility.Preview(eventDetails.Replace(
                "1 MARR\n2 HUSB", "1 NOTE\n2 HUSB", StringComparison.Ordinal)).Valid,
                "spouse detail requires a family event parent");
            Check(!GedcomLegacyCompatibility.Preview(eventDetails.Replace(
                "2 HUSB\n3 AGE 30y", "2 FAMC @F1@", StringComparison.Ordinal)).Valid,
                "family event cannot conceal a relationship pointer");
        }
        Console.WriteLine("PASS GEDCOM 5.5.1 UTF8 bounded preview, round-trip and invalid-input tests");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

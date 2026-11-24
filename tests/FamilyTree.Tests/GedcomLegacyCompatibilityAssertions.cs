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
        var original = GedcomSevenSubset.Preview(fixture);
        Check(original.Valid && original.People.Select(x => x.DisplayName).SequenceEqual(
            new[] { "Alex Example", "Jordan Sample", "Taylor Example" }) &&
            original.Families.Single().ParentPointers.SequenceEqual(new[] { "@I1@", "@I2@" }) &&
            original.Families.Single().ChildPointers.SequenceEqual(new[] { "@I3@" }),
            "synthetic GEDCOM 7 baseline contains expected people and links");
        CheckSameGraph(original, report);
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
        foreach (var boundary in new[] { "HEAD", "TRLR" })
            Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
                "0 TRLR", $"0 @X@ {boundary} bogus\n0 TRLR", StringComparison.Ordinal)).Valid,
                "cross-referenced boundary with payload denied");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "1 FAMC @F1@", "1 @X@ FAMC @I1@", StringComparison.Ordinal)).Valid,
            "level-one cross-reference cannot disguise a relationship pointer");
        Check(!GedcomLegacyCompatibility.Preview(converted.Replace(
            "1 FAMC @F1@", "1 @X@ FAMC @F1", StringComparison.Ordinal)).Valid,
            "level-one cross-reference cannot disguise an unterminated pointer");
        foreach (var source in new[] { converted, fixture })
        {
            foreach (var record in new[] { "0 NOTE FAM\n1 CHIL @I1@",
                         "0 NOTE INDI\n1 FAMS @F1@" })
                Check(!GedcomLegacyCompatibility.Preview(source.Replace(
                    "0 TRLR", record + "\n0 TRLR", StringComparison.Ordinal)).Valid,
                    "record text cannot impersonate a genealogy record type");
            Check(GedcomLegacyCompatibility.Preview(source.Replace(
                "0 TRLR", "0 NOTE FAM\n0 TRLR", StringComparison.Ordinal)).Valid,
                "ordinary note text remains previewable");
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
            CheckSameGraph(original, eventReport);
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

            foreach (var tag in new[] { "FAMC", "FAMS", "HUSB", "WIFE", "CHIL",
                         "ASSO", "ALIA", "ANCI", "DESI" })
                foreach (var record in new[] { $"0 {tag} @I1@", $"0 @X@ {tag} @I1@" })
                    Check(!GedcomLegacyCompatibility.Preview(source.Replace(
                        "0 TRLR", record + "\n0 TRLR", StringComparison.Ordinal)).Valid,
                        "pointer tags cannot be level-zero records");

            foreach (var tag in new[] { "OBJE", "ASSO", "ALIA", "ANCI", "DESI",
                         "SUBM", "SUBN", "REPO", "SNOTE", "SOUR", "NOTE" })
                foreach (var value in new[] { "@MISSING", "@MISSING@", "@I1@ extra" })
                    Check(!GedcomLegacyCompatibility.Preview(source.Replace(
                        "1 NAME Alex /Example/", $"1 NAME Alex /Example/\n1 {tag} {value}",
                        StringComparison.Ordinal)).Valid,
                        "malformed and unresolved nonrelationship references are rejected");

            foreach (var tag in new[] { "ASSO", "ALIA", "ANCI", "DESI", "SUBM",
                         "SUBN", "REPO", "SNOTE" })
                Check(!GedcomLegacyCompatibility.Preview(source.Replace(
                    "1 NAME Alex /Example/", $"1 NAME Alex /Example/\n1 {tag} text",
                    StringComparison.Ordinal)).Valid,
                    "pointer-only tags require a pointer");

            foreach (var item in new (string Tag, string Pointer)[] { ("ASSO", "@I2@"),
                         ("ALIA", "@I2@"), ("OBJE", "@M1@"), ("SOUR", "@S1@"),
                         ("NOTE", "@N1@"), ("SUBM", "@U1@"), ("SNOTE", "@T1@"),
                         ("ANCI", "@U1@"), ("DESI", "@U1@") })
            {
                var withReferences = source.Replace(
                    "1 NAME Alex /Example/", $"1 NAME Alex /Example/\n1 {item.Tag} {item.Pointer}",
                    StringComparison.Ordinal).Replace("0 TRLR",
                    "0 @M1@ OBJE\n0 @S1@ SOUR\n0 @N1@ NOTE\n0 @U1@ SUBM\n" +
                    "0 @B1@ SUBN\n0 @R1@ REPO\n0 @T1@ SNOTE\n0 TRLR", StringComparison.Ordinal);
                Check(GedcomLegacyCompatibility.Preview(withReferences).Valid ==
                    (item.Tag != "NOTE" || source == converted),
                    "complete references use the version's supported note form");
                Check(!GedcomLegacyCompatibility.Preview(withReferences.Replace(
                    $"1 {item.Tag} {item.Pointer}", $"1 {item.Tag} @F1@",
                    StringComparison.Ordinal)).Valid,
                    "nonrelationship references must target the correct record type");
            }
            var headerPointers = source.Replace("1 GEDC",
                "1 SUBM @U1@\n1 SUBN @B1@\n1 GEDC", StringComparison.Ordinal).Replace(
                "0 TRLR", "0 @U1@ SUBM\n0 @B1@ SUBN\n0 @S1@ SOUR\n1 REPO @R1@\n0 @R1@ REPO\n0 TRLR",
                StringComparison.Ordinal);
            Check(GedcomLegacyCompatibility.Preview(headerPointers).Valid,
                "header and source repository references are previewable");
            var submission = headerPointers.Replace("0 @B1@ SUBN",
                "0 @B1@ SUBN\n1 SUBM @U1@", StringComparison.Ordinal);
            Check(GedcomLegacyCompatibility.Preview(submission).Valid == (source == converted),
                "legacy submission may reference its submitter");
            foreach (var tag in new[] { "SUBM", "SUBN", "REPO", "SOUR", "OBJE" })
            {
                foreach (var record in new[] { $"0 {tag} @MISSING@", $"0 @X@ {tag} @MISSING@" })
                    Check(!GedcomLegacyCompatibility.Preview(source.Replace(
                        "0 TRLR", record + "\n0 TRLR", StringComparison.Ordinal)).Valid,
                        "reference records cannot carry pointer payloads");
                Check(GedcomLegacyCompatibility.Preview(source.Replace(
                    "0 TRLR", $"0 {tag}\n0 TRLR", StringComparison.Ordinal)).Valid,
                    "unreferenced records without IDs remain previewable");
            }
            Check(GedcomLegacyCompatibility.Preview(source.Replace("0 TRLR",
                "0 SOUR\n1 REPO @R1@\n0 @R1@ REPO\n0 TRLR", StringComparison.Ordinal)).Valid,
                "unreferenced source can cite a repository");
            foreach (var tag in new[] { "SUBN", "REPO" })
                Check(!GedcomLegacyCompatibility.Preview(source.Replace(
                    "1 NAME Alex /Example/", $"1 NAME Alex /Example/\n1 {tag} @I1@",
                    StringComparison.Ordinal)).Valid, "pointer must appear in its permitted container");
            var eventAssociation = source.Replace("1 NAME Alex /Example/",
                "1 NAME Alex /Example/\n1 BIRT\n2 ASSO @I2@\n3 ROLE WITN", StringComparison.Ordinal);
            Check(GedcomLegacyCompatibility.Preview(eventAssociation).Valid,
                "event association remains previewable");
            Check(!GedcomLegacyCompatibility.Preview(eventAssociation.Replace(
                "1 BIRT\n2 ASSO", "1 NOTE text\n2 ASSO", StringComparison.Ordinal)).Valid,
                "event association cannot be hidden beneath a note");
            var inline = source.Replace(
                "1 NAME Alex /Example/", "1 NAME Alex /Example/\n1 NOTE ordinary text\n1 SOUR inline text\n1 OBJE\n2 FILE example.jpg",
                StringComparison.Ordinal);
            Check(GedcomLegacyCompatibility.Preview(inline).Valid == (source == converted),
                "legacy inline source and media remain previewable; GEDCOM 7 requires pointers");
            Check(!GedcomLegacyCompatibility.Preview(source.Replace(
                "1 SOUR FamilyTreeSyntheticFixtures", "1 SOUR FamilyTreeSyntheticFixtures\n1 ALIA @I1@",
                StringComparison.Ordinal)).Valid, "individual alias is not a header pointer");
        }
        Console.WriteLine("PASS GEDCOM 5.5.1 UTF8 bounded preview, round-trip and invalid-input tests");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CheckSameGraph(GedcomPreviewReport expected, GedcomPreviewReport actual)
    {
        Check(actual.Valid && expected.People.SequenceEqual(actual.People) &&
            expected.Families.Count == actual.Families.Count &&
            expected.Families.Zip(actual.Families).All(pair =>
                pair.First.Pointer == pair.Second.Pointer &&
                pair.First.ParentPointers.SequenceEqual(pair.Second.ParentPointers) &&
                pair.First.ChildPointers.SequenceEqual(pair.Second.ChildPointers)),
            "preview preserves people and exact family pointers");
    }
}

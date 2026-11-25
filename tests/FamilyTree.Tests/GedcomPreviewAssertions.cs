using System.Runtime.CompilerServices;
using FamilyTree.Domain;
internal static class GedcomPreviewAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        const string safe = "0 HEAD\n1 GEDC\n2 VERS 7.0\n0 @I1@ INDI\n1 NAME Fictional /Person/\n0 @I2@ INDI\n0 @F1@ FAM\n1 HUSB @I1@\n1 WIFE @I2@\n0 TRLR\n";
        var preview = GedcomStructurePreview.Preview(safe);
        Check(preview.Valid && preview.Individuals == 2 && preview.Families == 1,
            "synthetic preview recognized");
        var rejectedInputs = new (string? Input, string Scenario, string Error)[]
        {
            (safe.Replace("0 @I2@ INDI", "0 @I1@ INDI\n0 @I2@ INDI"),
                "duplicate pointer", "Duplicate record pointer"),
            (safe.Replace("WIFE @I2@", "WIFE @OTHER@"),
                "unresolved pointer", "Unresolved reference pointer"),
            (safe.Replace("2 VERS 7.0", "2 VERS 5.5"),
                "missing version", "Not marked GEDCOM 7"),
            (new string('X', 300000), "oversize input", "File absent or too large"),
            (null, "null input", "File absent or too large"),
            ("0 HEAD\n0 TRLR", "short input", "Invalid line count"),
            (safe.Replace("1 NAME", "1\tNAME"), "tabbed line", "Malformed or oversized line"),
            (safe.Replace("1 NAME", "1 name"), "malformed line", "Malformed GEDCOM line"),
            (safe.Replace("1 NAME", "4 NAME"), "invalid nesting", "Invalid nesting depth"),
            (safe.Replace("0 HEAD", "0 NOTE"), "missing head", "Missing HEAD"),
            (safe.Replace("0 TRLR", "0 NOTE"), "missing trailer", "Missing TRLR"),
        };
        foreach (var (input, scenario, expectedError) in rejectedInputs)
        {
            var result = GedcomStructurePreview.Preview(input);
            Check(!result.Valid && result.Errors.SequenceEqual(new[] { expectedError }),
                $"{scenario}: expected only '{expectedError}', got [{string.Join(", ", result.Errors)}]");
        }
        var error = GedcomStructurePreview.Preview(safe + "secret-data").Errors;
        Check(!error.Any(x => x.Contains("secret-data", StringComparison.Ordinal)),
            "errors cannot echo GEDCOM contents");
        Console.WriteLine("PASS bounded synthetic GEDCOM metadata preview");
    }
    private static void Check(bool value, string reason)
    { if (!value) throw new Exception(reason); }
}
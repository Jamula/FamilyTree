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
        Check(!GedcomStructurePreview.Preview(safe.Replace("@I2@ INDI", "@I1@ INDI"))
                .Valid, "duplicate pointer rejected");
        Check(!GedcomStructurePreview.Preview(safe.Replace("WIFE @I2@", "WIFE @OTHER@"))
                .Valid, "unresolved pointer rejected");
        Check(!GedcomStructurePreview.Preview("0 HEAD\n0 TRLR")
                .Valid, "missing version rejected");
        Check(!GedcomStructurePreview.Preview(new string('X', 300000)).Valid,
            "oversize input rejected");
        var rejectedInputs = new (string? Input, string Reason)[]
        {
            (null, "null input"),
            ("0 HEAD\n0 TRLR", "short input"),
            ("0 HEAD\n1\tNAME bad\n0 TRLR\n", "tabbed line"),
            ("0 HEAD\n1 NAME bad\n0 TRLR\ntrailing", "malformed line"),
            ("0 HEAD\n4 NAME too-deep\n0 TRLR\n", "invalid nesting"),
            ("1 HEAD\n0 TRLR\n", "missing head"),
            ("0 HEAD\n0 HEAD\n", "missing trailer"),
        };
        foreach (var (input, reason) in rejectedInputs)
            Check(!GedcomStructurePreview.Preview(input).Valid, reason);
        var error = GedcomStructurePreview.Preview(safe + "secret-data").Errors;
        Check(!error.Any(x => x.Contains("secret-data", StringComparison.Ordinal)),
            "errors cannot echo GEDCOM contents");
        Console.WriteLine("PASS bounded synthetic GEDCOM metadata preview");
    }
    private static void Check(bool value, string reason)
    { if (!value) throw new Exception(reason); }
}
using System.Runtime.CompilerServices;
using System.Text;
using FamilyTree.Domain;

internal static class GedcomSyntheticSliceAssertions
{
    private const string Fixture = """
        0 HEAD
        1 SOUR Fictional
        1 GEDC
        2 VERS 7.0
        0 @I1@ INDI
        1 NAME Avery /Fiction/
        1 BIRT
        2 DATE ABT 1900
        2 SOUR @S1@
        1 FAMS @F1@
        0 @I2@ INDI
        1 NAME Blair /Fiction/
        1 FAMC @F1@
        0 @F1@ FAM
        1 WIFE @I1@
        1 CHIL @I2@
        0 @S1@ SOUR
        1 TITL Imaginary register
        0 TRLR
        """;

    [ModuleInitializer]
    internal static void Run()
    {
        var candidates = new[] {
            new SyntheticGedcomCandidate("candidate-b", "avery fiction", "1901"),
            new SyntheticGedcomCandidate("candidate-a", "AVERY FICTION", "ABT 1900") };
        var result = Parse(Fixture, candidates);
        Check(result.Errors.Count == 0 && result.Proposal is not null, "fictional preview");
        var proposal = result.Proposal!;
        Check(proposal.Graph.People.Count == 2 && proposal.Graph.Families.Count == 1 &&
            proposal.Graph.Sources.Single().Title == "Imaginary register", "typed records");
        var ev = proposal.Graph.People[0].Events.Single();
        Check(ev.Kind == "BIRT" && ev.Date == "ABT 1900" && ev.Sources.Single() == "@S1@",
            "date and citation retained");
        Check(proposal.Graph.Families[0].Parents.Single() == new SyntheticGedcomParent("WIFE", "@I1@"),
            "wife-only role retained");
        Check(proposal.Matches.Count == 2 && proposal.Matches[0].OtherId == "candidate-a" &&
            proposal.Matches[0].Kind == "possible-duplicate" &&
            proposal.Matches[1].Kind == "birth-date-conflict", "deterministic candidate reconciliation");
        Check(proposal.Matches.SequenceEqual(Parse(Fixture, candidates.Reverse().ToArray()).Proposal!.Matches),
            "candidate ordering independent");
        var duplicate = Fixture.Replace("Blair /Fiction/", "Avery /Fiction/", StringComparison.Ordinal);
        var duplicateMatches = Parse(duplicate).Proposal!.Matches;
        Check(duplicateMatches.Single() == new SyntheticGedcomMatch("@I1@", "@I2@", "incoming", "possible-duplicate"),
            "incoming duplicate report");
        var unspaced = Fixture.Replace("Avery /Fiction/", "Avery/Fiction/", StringComparison.Ordinal)
            .Replace("Blair /Fiction/", "Avery Fiction", StringComparison.Ordinal);
        var unspacedMatches = Parse(unspaced, [new("candidate", "Avery Fiction", "ABT 1900")]).Proposal!.Matches;
        Check(unspacedMatches.Count == 3 &&
            unspacedMatches.Contains(new SyntheticGedcomMatch("@I1@", "@I2@", "incoming", "possible-duplicate")) &&
            unspacedMatches.Contains(new SyntheticGedcomMatch("@I1@", "candidate", "candidate", "possible-duplicate")),
            "unspaced surname delimiters preserve incoming and candidate name boundaries");
        Check(Parse(Fixture.Replace("Avery /Fiction/", "Avery Fiction", StringComparison.Ordinal),
            [new("candidate", "Avery/Fiction/", "ABT 1900")]).Proposal!.Matches.Count == 1,
            "candidate surname delimiters preserve name boundaries");
        var distinct = Fixture.Replace("Avery /Fiction/", "Ann/Abel/", StringComparison.Ordinal)
            .Replace("Blair /Fiction/", "Annabel", StringComparison.Ordinal);
        Check(Parse(distinct, [new("candidate", "Annabel", "1901")]).Proposal!.Matches
            .All(m => m.IncomingId != "@I1@"), "surname components do not merge into false matches");

        var exported = GedcomSyntheticSlice.Export(new(proposal.Graph));
        Check(exported.Text.Contains("1 WIFE @I1@\n", StringComparison.Ordinal) &&
            !exported.Text.Contains("1 HUSB @I1@", StringComparison.Ordinal),
            "wife-only export does not change role");
        var roundTrip = Parse(exported.Text).Proposal!.Graph;
        Check(roundTrip.Families[0].Parents.Single() == proposal.Graph.Families[0].Parents.Single() &&
            roundTrip.People[0].Events.Single().Date == ev.Date &&
            roundTrip.People[0].Events.Single().Sources.SequenceEqual(ev.Sources) &&
            roundTrip.Sources.Single() == proposal.Graph.Sources.Single(), "subset graph round trip");
        Check(exported.Warnings.Single().Contains("SEX", StringComparison.Ordinal) &&
            proposal.Warnings.Count > 0, "loss warnings explicit");
        var supplementary = Fixture.Replace("Avery", "Avery 𠮷", StringComparison.Ordinal)
            .Replace("Imaginary register", "Imaginary 𠮷", StringComparison.Ordinal);
        var unicodeGraph = Parse(supplementary, [new("candidate", "Avery 𠮷 Fiction", null)]).Proposal!;
        Check(unicodeGraph.Matches.Count == 1 &&
            Parse(GedcomSyntheticSlice.Export(new(unicodeGraph.Graph)).Text).Proposal!.Graph.Sources.Single().Title ==
            "Imaginary 𠮷", "supplementary Unicode preview, reconciliation and export");

        var filtered = new SyntheticGedcomGraph([proposal.Graph.People[1] with { Events = [] }], [], []);
        var safe = GedcomSyntheticSlice.Export(new(filtered)).Text;
        Check(!safe.Contains("@I1@", StringComparison.Ordinal) &&
            !safe.Contains("@F1@", StringComparison.Ordinal) &&
            !safe.Contains("@S1@", StringComparison.Ordinal) &&
            !safe.Contains("Avery", StringComparison.Ordinal) &&
            !safe.Contains("Imaginary register", StringComparison.Ordinal), "only projected data exported");
        var dateRedacted = new SyntheticGedcomGraph(
            [proposal.Graph.People[0] with { Events = [ev with { Date = null, Sources = [] }] }], [], []);
        var dateRedactedOutput = GedcomSyntheticSlice.Export(new(dateRedacted)).Text;
        Check(!dateRedactedOutput.Contains("ABT 1900", StringComparison.Ordinal) &&
            !dateRedactedOutput.Contains("2 DATE", StringComparison.Ordinal), "omitted date not exported");
        Throws<InvalidOperationException>(() => GedcomSyntheticSlice.Export(new(
            filtered with { Families = proposal.Graph.Families })), "dangling hidden links fail closed");
        Throws<InvalidOperationException>(() => GedcomSyntheticSlice.Export(new(
            filtered with { People = [proposal.Graph.People[0]] })), "dangling source fails closed");
        Throws<InvalidOperationException>(() => GedcomSyntheticSlice.Export(new(
            filtered with { People = [filtered.People[0] with { Name = "x\n0 TRLR" }] })), "line injection");
        Throws<InvalidOperationException>(() => GedcomSyntheticSlice.Export(new(
            filtered with { People = [filtered.People[0] with { Name = "x\uD800" }] })), "unpaired surrogate rejected");
        Throws<InvalidOperationException>(() => GedcomSyntheticSlice.Export(new(
            filtered with { People = [filtered.People[0], filtered.People[0]] })), "duplicate IDs export");
        Throws<InvalidOperationException>(() => GedcomSyntheticSlice.Export(new(
            filtered with { Families = [new("@F1@", [new("OTHER", "@I2@")], [])] })), "invalid role");
        Throws<InvalidOperationException>(() => GedcomSyntheticSlice.Export(new(null!)), "null graph");

        foreach (var invalid in new[] {
            Fixture.Replace("7.0", "5.5.1", StringComparison.Ordinal),
            Fixture.Replace("@S1@", "@MISSING@", StringComparison.Ordinal).Replace("0 @MISSING@ SOUR", "0 @S1@ SOUR", StringComparison.Ordinal),
            Fixture.Replace("1 WIFE @I1@", "1 WIFE @I1@\n1 HUSB @I1@", StringComparison.Ordinal),
            Fixture.Replace("1 WIFE @I1@", "1 WIFE @I1@\n1 WIFE @I2@", StringComparison.Ordinal),
            Fixture.Replace("1 WIFE @I1@", "1 WIFE @I1@\n1 CHIL @NO@", StringComparison.Ordinal),
            Fixture.Replace("1 FAMS @F1@", "1 FAMC @F1@", StringComparison.Ordinal),
            Fixture.Replace("2 DATE ABT 1900", "3 DATE ABT 1900", StringComparison.Ordinal),
            Fixture.Replace("2 DATE ABT 1900", "2 DATE ABT 1900\n2 DATE 1901", StringComparison.Ordinal),
            Fixture.Replace("0 @I2@ INDI", "0 @I1@ INDI", StringComparison.Ordinal),
            Fixture.Replace("1 NAME Avery /Fiction/", "1 NAME ///", StringComparison.Ordinal),
            Fixture.Replace("1 BIRT", "1 NOTE Private", StringComparison.Ordinal),
            Fixture.Replace("0 TRLR", "0 TRLR\n0 TRLR", StringComparison.Ordinal),
            Fixture.Replace("1 SOUR Fictional", "1 SOUR Fictional\rX", StringComparison.Ordinal),
            Fixture.Replace("1 SOUR Fictional", "1 SOUR " + new string('a', 1025), StringComparison.Ordinal),
            Fixture.Replace("@I1@", "@BAD POINTER@", StringComparison.Ordinal),
            Fixture.Replace("2 VERS 7.0", "1 VERS 7.0", StringComparison.Ordinal)
        })
            Check(Parse(invalid).Proposal is null, "malformed or unsupported input rejected");
        Check(GedcomSyntheticSlice.Preview([0xc3, 0x28]).Proposal is null, "strict UTF-8");
        Check(GedcomSyntheticSlice.Preview(null).Proposal is null &&
            GedcomSyntheticSlice.Preview(new byte[256_001]).Proposal is null, "byte bounds");
        Check(Parse(Fixture.Replace("\n", "\r\n", StringComparison.Ordinal)).Proposal is not null,
            "CRLF accepted");
        Check(Parse("\uFEFF" + Fixture).Proposal is not null, "UTF-8 BOM accepted");
        Check(Parse(Fixture, [new("same", "Fictional", null), new("same", "Other", null)]).Proposal is null,
            "duplicate candidate IDs rejected");
        static string ManyDuplicates(int count) =>
            "0 HEAD\n1 GEDC\n2 VERS 7.0\n" +
            string.Concat(Enumerable.Range(0, count).Select(i => $"0 @I{i}@ INDI\n1 NAME Same /Fiction/\n")) +
            "0 TRLR\n";
        Check(Parse(ManyDuplicates(141)).Proposal!.Matches.Count == 9_870,
            "bounded duplicate matches accepted below cap");
        Check(Parse(ManyDuplicates(142)).Proposal is null, "duplicate match cap fails closed");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Throws<OperationCanceledException>(() => GedcomSyntheticSlice.Preview([], cancellationToken: cts.Token),
            "preview cancellation");
        Throws<OperationCanceledException>(() => GedcomSyntheticSlice.Export(new(filtered), cts.Token),
            "export cancellation");
        Console.WriteLine("PASS synthetic GEDCOM slice preview, reconciliation and trusted-projection export");
    }

    private static SyntheticGedcomPreviewResult Parse(string input, IReadOnlyList<SyntheticGedcomCandidate>? candidates = null) =>
        GedcomSyntheticSlice.Preview(Encoding.UTF8.GetBytes(input), candidates);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
}

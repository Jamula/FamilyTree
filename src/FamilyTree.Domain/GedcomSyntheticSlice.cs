using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace FamilyTree.Domain;

// A synthetic-only, deliberately narrow GEDCOM 7 subset, not a validator or
// an authorization boundary. No persistence or approval is performed here.
public sealed record SyntheticGedcomEvent(string Kind, string? Date, IReadOnlyList<string> Sources);
public sealed record SyntheticGedcomPerson(string Id, string Name, IReadOnlyList<SyntheticGedcomEvent> Events);
public sealed record SyntheticGedcomParent(string Role, string PersonId);
public sealed record SyntheticGedcomFamily(string Id, IReadOnlyList<SyntheticGedcomParent> Parents, IReadOnlyList<string> Children);
public sealed record SyntheticGedcomSource(string Id, string Title);
public sealed record SyntheticGedcomGraph(IReadOnlyList<SyntheticGedcomPerson> People,
    IReadOnlyList<SyntheticGedcomFamily> Families, IReadOnlyList<SyntheticGedcomSource> Sources);
public sealed record SyntheticGedcomCandidate(string Id, string Name, string? BirthDate);
public sealed record SyntheticGedcomMatch(string IncomingId, string OtherId, string Scope, string Kind);
public sealed record SyntheticGedcomProposal(SyntheticGedcomGraph Graph, IReadOnlyList<SyntheticGedcomMatch> Matches,
    IReadOnlyList<string> Warnings);
public sealed record SyntheticGedcomPreviewResult(SyntheticGedcomProposal? Proposal, IReadOnlyList<string> Errors);
public sealed record SyntheticGedcomExport(string Text, IReadOnlyList<string> Warnings);

/// <summary>
/// The caller must supply only a trusted, already-authorized synthetic projection,
/// including independently authorized fields, sources and links. This type does
/// not perform or imply authorization; never pass a full graph and visibility flags.
/// </summary>
public sealed record TrustedSyntheticGedcomProjection(SyntheticGedcomGraph Graph);

public static partial class GedcomSyntheticSlice
{
    private const int MaxBytes = 256_000, MaxLines = 5_000, MaxLine = 1024, MaxRecords = 1000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex Line = new(
        @"^(0|[1-9][0-9]?) (?:(@[A-Za-z0-9_-]{1,40}@) )?([A-Z_][A-Z0-9_]{0,31})(?: (.+))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Pointer = new(@"^@[A-Za-z0-9_-]{1,40}@$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly string[] LossWarnings =
        ["Synthetic GEDCOM 7 subset only: SEX and individual FAMS/FAMC declarations are not exported; family role links are retained. Unsupported structures are rejected; application semantics are not preserved. Not a backup or conformance claim."];

    public static SyntheticGedcomPreviewResult Preview(byte[]? bytes,
        IReadOnlyList<SyntheticGedcomCandidate>? candidates = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes is null || bytes.Length > MaxBytes) return Error("Input absent or too large.");
        string text;
        try { text = Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Error("Invalid UTF-8."); }
        if (text.StartsWith('\uFEFF')) text = text[1..];
        try
        {
            var graph = Parse(text, cancellationToken);
            var matches = Reconcile(graph, candidates ?? [], cancellationToken);
            return new(new(graph, matches, LossWarnings.ToArray()), []);
        }
        catch (InvalidOperationException) { return Error("Invalid or unsupported GEDCOM subset."); }
    }

    private static SyntheticGedcomPreviewResult Error(string message) => new(null, [message]);
    private static void Require([DoesNotReturnIf(false)] bool condition)
    {
        if (!condition) throw new InvalidOperationException("Invalid or unsupported GEDCOM subset.");
    }

    private sealed class PersonBuilder(string id)
    {
        public readonly string Id = id;
        public string? Name;
        public readonly List<SyntheticGedcomEvent> Events = [];
        public readonly List<(string Tag, string Id)> FamilyRefs = [];
    }
    private sealed class FamilyBuilder(string id)
    {
        public readonly string Id = id;
        public readonly List<SyntheticGedcomParent> Parents = [];
        public readonly List<string> Children = [];
    }
    private sealed class EventBuilder(string kind)
    {
        public readonly string Kind = kind;
        public string? Date;
        public readonly List<string> Sources = [];
    }

    private static SyntheticGedcomGraph Parse(string text, CancellationToken ct)
    {
        Require(text.Length <= MaxBytes &&
            !text.Replace("\r\n", "", StringComparison.Ordinal).Contains('\r'));
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var count = lines.Length;
        if (count > 0 && lines[^1].Length == 0) count--;
        Require(count is >= 4 and <= MaxLines);
        var people = new List<PersonBuilder>();
        var families = new List<FamilyBuilder>();
        var sources = new List<SyntheticGedcomSource>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        PersonBuilder? person = null;
        FamilyBuilder? family = null;
        EventBuilder? evt = null;
        string? sourceId = null, sourceTitle = null, record = null, parentTag = null;
        var previousLevel = 0;
        var headerGedc = false;
        var version = false;
        var headerSource = false;
        var trailer = false;

        void CompleteEvent()
        {
            if (evt is null) return;
            Require(person is not null);
            person!.Events.Add(new(evt.Kind, evt.Date, evt.Sources.ToArray()));
            evt = null;
        }
        void CompleteRecord()
        {
            CompleteEvent();
            if (record == "SOUR")
            {
                Require(sourceId is not null && sourceTitle is not null);
                sources.Add(new(sourceId!, sourceTitle!));
            }
            person = null; family = null; sourceId = null; sourceTitle = null;
        }
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var line = lines[i];
            Require(line.Length <= MaxLine && !line.Any(char.IsControl));
            var m = Line.Match(line);
            Require(m.Success);
            var level = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var id = m.Groups[2].Value;
            var tag = m.Groups[3].Value;
            var value = m.Groups[4].Value;
            Require(level <= 2 && (i == 0 ? level == 0 : level <= previousLevel + 1));
            Require(level == 0 || id.Length == 0);
            if (level <= 1) CompleteEvent();
            if (level == 0)
            {
                CompleteRecord();
                Require(!trailer && value.Length == 0);
                if (i == 0) Require(tag == "HEAD" && id.Length == 0);
                else Require(tag != "HEAD");
                record = tag; parentTag = null;
                if (tag == "TRLR") { Require(id.Length == 0 && i == count - 1); trailer = true; }
                else if (tag is "INDI" or "FAM" or "SOUR")
                {
                    Require(id.Length > 0 && ids.Add(id) && ids.Count <= MaxRecords);
                    if (tag == "INDI") { person = new(id); people.Add(person); }
                    else if (tag == "FAM") { family = new(id); families.Add(family); }
                    else sourceId = id;
                }
                else Require(tag == "HEAD" && i == 0);
            }
            else if (level == 1)
            {
                parentTag = tag;
                if (record == "HEAD" && tag == "GEDC")
                { Require(value.Length == 0 && !headerGedc); headerGedc = true; }
                else if (record == "HEAD" && tag == "SOUR")
                { Require(!headerSource && SafeText(value)); headerSource = true; }
                else if (person is not null && tag == "NAME")
                { Require(person.Name is null && SafeName(value)); person.Name = value; }
                else if (person is not null && tag is "BIRT" or "DEAT")
                {
                    Require(value.Length == 0 && !person!.Events.Any(e => e.Kind == tag));
                    evt = new(tag);
                }
                else if (person is not null && tag is "FAMS" or "FAMC")
                { Require(IsPointer(value)); person!.FamilyRefs.Add((tag, value)); }
                else if (person is not null && tag == "SEX")
                { Require(value is "M" or "F" or "X" or "U"); }
                else if (family is not null && tag is "HUSB" or "WIFE" or "CHIL")
                {
                    Require(IsPointer(value));
                    if (tag == "CHIL") family!.Children.Add(value);
                    else family!.Parents.Add(new(tag, value));
                }
                else if (sourceId is not null && tag == "TITL")
                { Require(sourceTitle is null && SafeText(value)); sourceTitle = value; }
                else Require(false);
            }
            else if (record == "HEAD" && parentTag == "GEDC" && tag == "VERS")
            { Require(value == "7.0" && !version); version = true; }
            else if (evt is not null && tag == "DATE")
            { Require(evt.Date is null && SafeText(value)); evt.Date = value; }
            else if (evt is not null && tag == "SOUR")
            { Require(IsPointer(value)); evt.Sources.Add(value); }
            else Require(false);
            previousLevel = level;
        }
        CompleteRecord();
        Require(trailer && headerGedc && version && people.All(p => p.Name is not null));
        var peopleIds = people.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var sourceIds = sources.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var familiesById = families.ToDictionary(f => f.Id, StringComparer.Ordinal);
        foreach (var f in families)
        {
            ct.ThrowIfCancellationRequested();
            var links = f.Parents.Select(p => p.PersonId).Concat(f.Children).ToArray();
            Require(f.Parents.Count <= 2 && links.Length > 0 &&
                f.Parents.Select(p => p.Role).Distinct(StringComparer.Ordinal).Count() == f.Parents.Count &&
                links.Distinct(StringComparer.Ordinal).Count() == links.Length &&
                links.All(peopleIds.Contains));
        }
        foreach (var p in people)
        {
            ct.ThrowIfCancellationRequested();
            Require(p.Events.All(e => e.Sources.Distinct(StringComparer.Ordinal).Count() == e.Sources.Count &&
                e.Sources.All(sourceIds.Contains)));
            Require(p.FamilyRefs.Distinct().Count() == p.FamilyRefs.Count &&
                p.FamilyRefs.All(r => familiesById.TryGetValue(r.Id, out var f) &&
                    (r.Tag == "FAMS" ? f.Parents.Any(parent => parent.PersonId == p.Id) : f.Children.Contains(p.Id))));
        }
        return new(people.Select(p => new SyntheticGedcomPerson(p.Id, p.Name!, p.Events.ToArray())).ToArray(),
            families.Select(f => new SyntheticGedcomFamily(f.Id, f.Parents.ToArray(), f.Children.ToArray())).ToArray(),
            sources.ToArray());
    }

    private static bool IsPointer(string text) => Pointer.IsMatch(text);
    private static bool SafeText(string? text) => !string.IsNullOrWhiteSpace(text) &&
        text.Length <= 500 && !text.Contains('@') && !text.Any(char.IsControl) &&
        !text.Any(char.IsSurrogate);
    private static bool SafeName(string? text) => SafeText(text) &&
        !string.IsNullOrWhiteSpace(text!.Replace("/", "", StringComparison.Ordinal));

    private static IReadOnlyList<SyntheticGedcomMatch> Reconcile(SyntheticGedcomGraph graph,
        IReadOnlyList<SyntheticGedcomCandidate> candidates, CancellationToken ct)
    {
        Require(candidates.Count <= MaxRecords);
        var candidateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            Require(c is not null && SafeText(c.Id) && SafeName(c.Name) &&
                (c.BirthDate is null || SafeText(c.BirthDate)) && candidateIds.Add(c.Id));
        }
        static string Key(string name) => string.Join(' ', name.Replace("/", "", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        static string? Birth(SyntheticGedcomPerson p) => p.Events.FirstOrDefault(e => e.Kind == "BIRT")?.Date;
        static string Kind(string? a, string? b) => a is not null && b is not null &&
            !string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? "birth-date-conflict" : "possible-duplicate";
        var matches = new List<SyntheticGedcomMatch>();
        var people = graph.People.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
        var groupedPeople = people.GroupBy(p => Key(p.Name), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var groupedCandidates = candidates.GroupBy(c => Key(c.Name), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        void Add(SyntheticGedcomMatch match)
        {
            Require(matches.Count < 10_000);
            matches.Add(match);
        }
        foreach (var group in groupedPeople)
        {
            for (var i = 0; i < group.Value.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var p = group.Value[i];
                for (var j = i + 1; j < group.Value.Length; j++)
                {
                    ct.ThrowIfCancellationRequested();
                    Add(new(p.Id, group.Value[j].Id, "incoming", Kind(Birth(p), Birth(group.Value[j]))));
                }
                if (groupedCandidates.TryGetValue(group.Key, out var candidateGroup))
                    foreach (var c in candidateGroup)
                    {
                        ct.ThrowIfCancellationRequested();
                        Add(new(p.Id, c.Id, "candidate", Kind(Birth(p), c.BirthDate)));
                    }
            }
        }
        return matches.OrderBy(m => m.IncomingId, StringComparer.Ordinal)
            .ThenBy(m => m.Scope, StringComparer.Ordinal).ThenBy(m => m.OtherId, StringComparer.Ordinal).ToArray();
    }
}

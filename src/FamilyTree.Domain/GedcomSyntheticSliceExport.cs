using System.Text;

namespace FamilyTree.Domain;

public static partial class GedcomSyntheticSlice
{
    /// <summary>
    /// Serializes only the supplied projection, never querying source data. The
    /// caller is responsible for authorization; this is not an access check.
    /// Invalid or incomplete projections fail without returning partial output.
    /// </summary>
    public static SyntheticGedcomExport Export(TrustedSyntheticGedcomProjection projection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(projection);
        var graph = projection.Graph;
        Require(graph is not null && graph.People is not null && graph.Families is not null &&
            graph.Sources is not null);
        Require(graph!.People.Count <= MaxRecords && graph.Families.Count <= MaxRecords &&
            graph.Sources.Count <= MaxRecords &&
            (long)graph.People.Count + graph.Families.Count + graph.Sources.Count <= MaxRecords);
        var output = new StringBuilder("0 HEAD\n1 SOUR FamilyTreeSyntheticPreview\n1 GEDC\n2 VERS 7.0\n");
        void Append(string line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(line.Length <= MaxLine && output.Length + line.Length + 1 <= MaxBytes);
            output.Append(line).Append('\n');
        }
        foreach (var p in graph.People)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(p is not null && p.Id is not null && IsPointer(p.Id) && SafeName(p.Name) && p.Events is not null &&
                p.Events.Count <= 2);
            Append($"0 {p!.Id} INDI");
            Append($"1 NAME {p.Name}");
            foreach (var e in p.Events)
            {
                Require(e is not null && (e.Kind is "BIRT" or "DEAT") &&
                    (e.Date is null || SafeText(e.Date)) && e.Sources is not null &&
                    e.Sources.Count <= MaxRecords);
                Append($"1 {e!.Kind}");
                if (e.Date is not null) Append($"2 DATE {e.Date}");
                foreach (var source in e.Sources)
                {
                    Require(source is not null && IsPointer(source));
                    Append($"2 SOUR {source}");
                }
            }
        }
        foreach (var f in graph.Families)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(f is not null && f.Id is not null && IsPointer(f.Id) && f.Parents is not null &&
                f.Children is not null && f.Parents.Count <= 2 && f.Children.Count <= MaxRecords);
            Append($"0 {f!.Id} FAM");
            for (var i = 0; i < f.Parents.Count; i++)
            {
                var parent = f.Parents[i];
                Require(parent is not null && (parent.Role is "HUSB" or "WIFE") &&
                    parent.PersonId is not null && IsPointer(parent.PersonId));
                Append($"1 {parent.Role} {parent.PersonId}");
            }
            foreach (var child in f.Children)
            {
                Require(child is not null && IsPointer(child));
                Append($"1 CHIL {child}");
            }
        }
        foreach (var source in graph.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(source is not null && source.Id is not null && IsPointer(source.Id) && SafeText(source.Title));
            Append($"0 {source!.Id} SOUR");
            Append($"1 TITL {source.Title}");
        }
        Append("0 TRLR");
        var text = output.ToString();
        Require(Utf8.GetByteCount(text) <= MaxBytes);
        // Use the same strict parser for cross-record, nesting and total line bounds.
        _ = Parse(text, cancellationToken);
        return new(text, LossWarnings.ToArray());
    }
}

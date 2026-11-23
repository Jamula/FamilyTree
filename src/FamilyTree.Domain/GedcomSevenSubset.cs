using System.Globalization;
using System.Text;

namespace FamilyTree.Domain;

/// <summary>
/// Tiny, explicitly incomplete GEDCOM 7 interchange adapter for synthetic
/// preview/round-trip regression. It is NOT a full GEDCOM parser/validator;
/// no private HTTP route or durable import is enabled. Unknown and unhandled
/// structures require a proper library + human review before promotion.
/// </summary>
public sealed record GedcomPreviewPerson(string Pointer, string DisplayName,
    string? BirthDateText, string? DeathDateText);
public sealed record GedcomPreviewFamily(string Pointer, IReadOnlyList<string> ParentPointers,
    IReadOnlyList<string> ChildPointers);
public sealed record GedcomPreviewReport(bool Valid, IReadOnlyList<GedcomPreviewPerson> People,
    IReadOnlyList<GedcomPreviewFamily> Families, IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);

public static class GedcomSevenSubset
{
    public static GedcomPreviewReport Preview(string? text)
    {
        var preflight = GedcomStructurePreview.Preview(text);
        if (!preflight.Valid || text is null)
            return new(false, [], [], [], preflight.Errors);

        var people = new List<GedcomPreviewPerson>();
        var families = new List<GedcomPreviewFamily>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        string? recordPointer = null, recordType = null;
        string displayName = "";
        string? birth = null, death = null, eventType = null;
        var parents = new List<string>();
        var children = new List<string>();
        var declaredPeople = new HashSet<string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\n').Split('\n');

        void Complete()
        {
            if (recordType == "INDI" && recordPointer is not null)
            {
                people.Add(new(recordPointer, displayName, birth, death));
                declaredPeople.Add(recordPointer);
            }
            if (recordType == "FAM" && recordPointer is not null)
                families.Add(new(recordPointer, parents.ToArray(), children.ToArray()));
        }

        foreach (var line in lines)
        {
            var firstSpace = line.IndexOf(' ');
            if (firstSpace < 0)
                return Reject("Malformed structure");
            var level = int.Parse(line.AsSpan(0, firstSpace), CultureInfo.InvariantCulture);
            var rest = line[(firstSpace + 1)..];
            var tokens = rest.Split(' ', 3);
            if (level == 0)
            {
                Complete();
                recordPointer = null;
                recordType = tokens[0];
                if (tokens[0].StartsWith('@'))
                {
                    if (tokens.Length < 2) return Reject("Malformed record");
                    recordPointer = tokens[0];
                    recordType = tokens[1];
                }
                displayName = ""; birth = null; death = null; eventType = null;
                parents.Clear(); children.Clear();
                continue;
            }
            if (recordType == "INDI" && tokens.Length >= 1)
            {
                var tag = tokens[0];
                var value = tokens.Length >= 2 ? rest[(tag.Length + 1)..] : "";
                if (level == 1)
                {
                    eventType = tag is "BIRT" or "DEAT" ? tag : null;
                    if (tag == "NAME")
                        displayName = value.Replace("/", "", StringComparison.Ordinal).Trim();
                    else if (tag is not ("BIRT" or "DEAT" or "FAMS" or "FAMC" or "SEX"))
                        warnings.Add("Unsupported individual structures require review");
                }
                if (level == 2 && tag == "DATE")
                {
                    if (eventType == "BIRT") birth = value;
                    if (eventType == "DEAT") death = value;
                }
            }
            if (recordType == "FAM" && level == 1)
            {
                var tag = tokens[0];
                var value = rest.Length > tag.Length ? rest[(tag.Length + 1)..] : "";
                if (tag is "HUSB" or "WIFE") parents.Add(value);
                else if (tag == "CHIL") children.Add(value);
                else warnings.Add("Unsupported family structures require review");
            }
        }
        Complete();
        if (people.Count != preflight.Individuals || families.Count != preflight.Families ||
            people.Any(p => p.DisplayName.Length == 0) ||
            families.Any(f => f.ParentPointers.Any(x => !declaredPeople.Contains(x)) ||
                f.ChildPointers.Any(x => !declaredPeople.Contains(x))))
            return Reject("Incomplete or invalid supported records");

        return new(true, people, families, warnings.ToArray(), []);
    }

    /// <summary>
    /// Require an explicit grant for each person and each relationship.
    /// No hidden person, relationship or pointer is included in the result.
    /// This is a bounded GEDCOM-only preview for Parent edges; other edge
    /// semantics require the versioned sidecar before lossless export.
    /// </summary>
    public static async Task<string> ExportAuthorizedPreviewAsync(Guid viewer,
        IEnumerable<Person> people, IEnumerable<Relationship> relations,
        IFamilyAuthorization authorization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(relations);
        ArgumentNullException.ThrowIfNull(authorization);
        var visible = new List<Person>();
        foreach (var person in people)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await authorization.CanSeePersonAsync(viewer, person.Id, cancellationToken))
                visible.Add(person);
        }
        // Sorting avoids unstable IDs between repeated previews; no data
        // other than explicitly permitted names leaves the API boundary.
        visible.Sort((a,b) => a.Id.CompareTo(b.Id));
        var ids = visible.Select((person,index) => (person.Id,Pointer:$"@I{index+1}@"))
            .ToDictionary(x=>x.Id,x=>x.Pointer);
        var accepted = new List<Relationship>();
        foreach (var relation in relations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (relation.Kind == RelationKind.Parent &&
                ids.ContainsKey(relation.FromPersonId) &&
                ids.ContainsKey(relation.ToPersonId) &&
                await authorization.CanSeeRelationshipAsync(viewer, relation, cancellationToken))
                accepted.Add(relation);
        }

        var output = new StringBuilder();
        output.Append("0 HEAD\n1 SOUR FamilyTreeSyntheticPreview\n1 GEDC\n2 VERS 7.0\n");
        foreach (var person in visible)
        {
            var name = person.DisplayName;
            var canonicalName = name?.Replace("/", "", StringComparison.Ordinal).Trim();
            if (string.IsNullOrWhiteSpace(canonicalName) || canonicalName.Length > 180 ||
                canonicalName.IndexOfAny(['\r','\n','\t','@']) >= 0)
                throw new InvalidOperationException("A display name cannot be safely exported.");
            output.Append("0 ").Append(ids[person.Id]).Append(" INDI\n1 NAME ")
                .Append(canonicalName).Append('\n');
        }
        // Each distinct parent-child edge has a minimal family record.
        // This is deliberately not a faithful multi-parent/blended family
        // export, and callers must not advertise it as a lossless backup.
        var n = 0;
        foreach (var edge in accepted.OrderBy(x=>x.Id))
        {
            var fam = ++n;
            output.Append("0 @F").Append(fam).Append("@ FAM\n1 HUSB ")
                .Append(ids[edge.FromPersonId]).Append("\n1 CHIL ")
                .Append(ids[edge.ToPersonId]).Append('\n');
        }
        output.Append("0 TRLR\n");
        if (output.Length > 256_000 || output.ToString().Split('\n').Length > 5000)
            throw new InvalidOperationException("Preview is too large.");
        return output.ToString();
    }

    private static GedcomPreviewReport Reject(string error) =>
        new(false, [], [], [], [error]);
}

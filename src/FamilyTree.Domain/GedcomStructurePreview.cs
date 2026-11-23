using System.Text.RegularExpressions;

namespace FamilyTree.Domain;

/// <summary>
/// Bounded, metadata-only preflight; NOT an import/parser, a GEDCOM
/// compliance validator, or authorization to store or export records.
/// Never return original lines, names, paths or identifiers in errors.
/// </summary>
public sealed record GedcomStructureResult(bool Valid, int Individuals, int Families,
    IReadOnlyList<string> Errors);

public static class GedcomStructurePreview
{
    private static readonly Regex Line = new(
        @"^(?<level>[0-9]{1,2}) (?:(?<id>@[A-Za-z0-9_-]{1,40}@) )?(?<tag>[A-Z_][A-Z0-9_]{0,31})(?: (?<value>.*))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Pointer = new(
        @"^@[A-Za-z0-9_-]{1,40}@$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static GedcomStructureResult Preview(string? text)
    {
        if (text is null || text.Length > 256_000)
            return Reject("File absent or too large");
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        if (lines.Length is < 3 or > 5000)
            return Reject("Invalid line count");
        var errors = new List<string>();
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var references = new HashSet<string>(StringComparer.Ordinal);
        int individuals = 0, families = 0, previousLevel = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > 1024 || lines[i].Contains('\t'))
                return Reject("Malformed or oversized line");
            var match = Line.Match(lines[i]);
            if (!match.Success) return Reject("Malformed GEDCOM line");
            var level = int.Parse(match.Groups["level"].Value,
                System.Globalization.CultureInfo.InvariantCulture);
            if (level > 32 || (i > 0 && level > previousLevel + 1))
                return Reject("Invalid nesting depth");
            previousLevel = level;
            var tag = match.Groups["tag"].Value;
            var id = match.Groups["id"].Value;
            var value = match.Groups["value"].Value;
            if (i == 0 && (level != 0 || tag != "HEAD"))
                errors.Add("Missing HEAD");
            if (i == lines.Length - 1 && (level != 0 || tag != "TRLR"))
                errors.Add("Missing TRLR");
            if (level == 0 && id.Length > 0)
            {
                if (!declared.Add(id)) errors.Add("Duplicate record pointer");
                if (tag == "INDI") individuals++;
                if (tag == "FAM") families++;
            }
            if (level > 0 && Pointer.IsMatch(value)) references.Add(value);
        }
        if (!lines.Any(x => x == "2 VERS 7.0"))
            errors.Add("Not marked GEDCOM 7");
        if (references.Any(pointer => !declared.Contains(pointer)))
            errors.Add("Unresolved reference pointer");
        return new(errors.Count == 0, individuals, families, errors.ToArray());
    }

    private static GedcomStructureResult Reject(string error) =>
        new(false, 0, 0, new[] { error });
}
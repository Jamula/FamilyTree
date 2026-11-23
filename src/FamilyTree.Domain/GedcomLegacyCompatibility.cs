using System.Text.RegularExpressions;

namespace FamilyTree.Domain;

/// <summary>
/// Bounded, nonpersistent 5.5.1 UTF-8 compatibility PREVIEW only.
/// Rejects malformed links and unknown encodings rather than guessing. This
/// adapter does not authorize any live import or claim GEDCOM conformance.
/// </summary>
public static class GedcomLegacyCompatibility
{
    private static readonly Regex Pointer = new(
        @"^@[A-Za-z0-9_-]{1,40}@$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static GedcomPreviewReport Preview(string? input)
    {
        if (input is null || input.Length > 256_000 || input.Contains('\0'))
            return Reject("Invalid or oversized GEDCOM data");
        var normalized = input.TrimStart('\uFEFF')
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.TrimEnd('\n').Split('\n');
        if (lines.Length is < 3 or > 5000 || lines[0] != "0 HEAD" ||
            lines[^1] != "0 TRLR")
            return Reject("Malformed GEDCOM container");

        var version = "";
        var hasUtf8 = false;
        var encodingCount = 0;
        var inHeader = true;
        var versionCount = 0;
        var versionLine = -1;
        var headerSection = "";
        var individualIds = new HashSet<string>(StringComparer.Ordinal);
        var familyIds = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<(string Tag, string Pointer)>();

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            // CRLF was normalized above. Any residual CR is malformed and
            // cannot be allowed into individual names or record pointers.
            if (line.Length > 1024 || line.Any(char.IsControl) ||
                Regex.IsMatch(line, @"^0[0-9]+ "))
                return Reject("Malformed GEDCOM line");
            if (line.StartsWith("0 ", StringComparison.Ordinal))
            {
                var record = line.Split(' ', 3);
                if (record.Length > 1 &&
                    (record[1] == "HEAD" ||
                     (record[1] == "TRLR" && i != lines.Length - 1)))
                    return Reject("Repeated GEDCOM boundary record");
                inHeader = false;
                if (record.Length == 3 && record[1].StartsWith('@'))
                {
                    if (record[2] == "INDI") individualIds.Add(record[1]);
                    if (record[2] == "FAM") familyIds.Add(record[1]);
                }
            }
            if (!inHeader)
            {
                if (line.StartsWith("1 ", StringComparison.Ordinal))
                {
                    var record = line.Split(' ', 3);
                    if (record.Length > 1 &&
                        record[1] is ("FAMC" or "FAMS" or "HUSB" or "WIFE" or "CHIL"))
                    {
                        if (record.Length != 3 || !Pointer.IsMatch(record[2]))
                            return Reject("Malformed relationship pointer");
                        links.Add((record[1], record[2]));
                    }
                }
                continue;
            }
            if (line.StartsWith("1 ", StringComparison.Ordinal))
                headerSection = line.Split(' ', 3)[1];
            // SOUR/VERS describes the exporter. Only GEDC/VERS declares
            // the standard version, so versions are counted by parent tag.
            if (headerSection == "GEDC" &&
                (line == "2 VERS" || line.StartsWith("2 VERS ", StringComparison.Ordinal)))
            {
                versionCount++;
                if (line == "2 VERS")
                    return Reject("Empty GEDCOM format version");
                versionLine = i;
                version = line["2 VERS ".Length..];
                if (string.IsNullOrWhiteSpace(version))
                    return Reject("Empty GEDCOM format version");
            }
            if (line == "1 CHAR" || line.StartsWith("1 CHAR ", StringComparison.Ordinal))
            {
                if (++encodingCount != 1 || line != "1 CHAR UTF-8")
                    return Reject("Unsupported or duplicate GEDCOM character encoding");
                hasUtf8 = true;
            }
        }
        if (versionCount != 1)
            return Reject("Missing or conflicting GEDCOM version");

        // Validate both syntactic completeness and target record type before
        // running the subset parser. The older parser only checked complete
        // pointers, allowing some dangling malformed references through.
        if (links.Any(link => link.Tag is "FAMC" or "FAMS"
                ? !familyIds.Contains(link.Pointer)
                : !individualIds.Contains(link.Pointer)))
            return Reject("Unresolved relationship pointer");

        if (version == "7.0")
            return GedcomSevenSubset.Preview(normalized);
        if (version != "5.5.1")
            return Reject("Unsupported GEDCOM version");
        if (!hasUtf8)
            return Reject("Legacy GEDCOM encoding must explicitly be UTF-8");

        // Rewrite only GEDC/VERS in a private copy, never SOURCE/VERS or the
        // caller's original text. This is an inspection, not an import.
        lines[versionLine] = "2 VERS 7.0";
        var preview = GedcomSevenSubset.Preview(string.Join('\n', lines));
        if (!preview.Valid)
            return preview;
        return preview with
        {
            Warnings = preview.Warnings.Append(
                "5.5.1 UTF-8 converted for inspection only; full import requires review")
                .ToArray()
        };
    }

    private static GedcomPreviewReport Reject(string text)
        => new(false, [], [], [], [text]);
}

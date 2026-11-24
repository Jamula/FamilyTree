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
        var records = new Dictionary<string, string>(StringComparer.Ordinal);
        var references = new List<(string Pointer, string Kind)>();
        var currentRecordKind = "";
        var currentSection = "";

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            // CRLF was normalized above. Any residual CR is malformed and
            // cannot be allowed into individual names or record pointers.
            if (line.Length > 1024 || line.Any(char.IsControl) ||
                Regex.IsMatch(line, @"^0[0-9]+ "))
                return Reject("Malformed GEDCOM line");
            // A cross-reference ID belongs only on a level-zero record. Otherwise
            // a spoofed tag (e.g. "1 @X@ FAMC @I1@") could evade pointer checks.
            var tokens = line.Split(' ', 3);
            if (tokens.Length > 1 && tokens[0] != "0" &&
                tokens[1].StartsWith('@'))
                return Reject("Cross-reference ID outside a level-zero record");
            if (line.StartsWith("0 ", StringComparison.Ordinal))
            {
                var record = tokens;
                if (record.Length > 1 &&
                    (record[1] == "HEAD" ||
                     (record[1] == "TRLR" && i != lines.Length - 1) ||
                     (record.Length == 3 && record[1].StartsWith('@') &&
                      (record[2].Split(' ', 2)[0] is "HEAD" or "TRLR"))))
                    return Reject("Repeated GEDCOM boundary record");
                inHeader = false;
                if (record.Length > 1 && record[1] is ("INDI" or "FAM"))
                    return Reject("Missing genealogy record pointer");
                var recordTag = record.Length == 3 && record[1].StartsWith('@')
                    ? record[2].Split(' ', 2)[0] : record[1];
                if (recordTag is "REPO" or "SUBN" or "SUBM" or "SOUR" or "OBJE" &&
                    (record.Length != 3 || !Pointer.IsMatch(record[1]) || record[2] != recordTag))
                    return Reject("Malformed reference record");
                if (recordTag is "FAMC" or "FAMS" or "HUSB" or "WIFE" or "CHIL" or
                    "ASSO" or "ALIA" or "ANCI" or "DESI")
                    return Reject("Pointer tag outside a record");
                currentRecordKind = record.Length == 3 && Pointer.IsMatch(record[1])
                    ? record[2] : "";
                currentSection = "";
                if (record.Length == 3 && record[1].StartsWith('@'))
                {
                    if (Pointer.IsMatch(record[1]))
                        records.TryAdd(record[1], recordTag);
                    if (record[2] == "INDI") individualIds.Add(record[1]);
                    if (record[2] == "FAM") familyIds.Add(record[1]);
                }
            }
            if (tokens.Length > 1 && tokens[0] == "1")
                currentSection = tokens[1];
            if (tokens.Length > 1 &&
                tokens[1] is ("FAMC" or "FAMS" or "HUSB" or "WIFE" or "CHIL"))
            {
                var spouseDetail = !inHeader && currentRecordKind == "FAM" &&
                    tokens[0] == "2" && tokens[1] is ("HUSB" or "WIFE") &&
                    currentSection is ("ANUL" or "CENS" or "DIV" or "DIVF" or "ENGA" or
                        "MARB" or "MARC" or "MARL" or "MARR" or "MARS" or "EVEN" or
                        "NCHI" or "RESI" or "FACT");
                if (spouseDetail)
                {
                    if (tokens.Length != 2 || i + 1 >= lines.Length ||
                        !lines[i + 1].StartsWith("3 AGE ", StringComparison.Ordinal) ||
                        string.IsNullOrWhiteSpace(lines[i + 1]["3 AGE ".Length..]))
                        return Reject("Malformed family event spouse detail");
                }
                else
                {
                    if (inHeader || tokens[0] != "1" || tokens.Length != 3 ||
                        !Pointer.IsMatch(tokens[2]))
                        return Reject("Malformed relationship pointer");
                    var familyPointer = tokens[1] is "FAMC" or "FAMS";
                    if ((familyPointer && currentRecordKind != "INDI") ||
                        (!familyPointer && currentRecordKind != "FAM"))
                        return Reject("Relationship pointer in incorrect record");
                    links.Add((tokens[1], tokens[2]));
                }
            }
            if (tokens.Length > 1 && tokens[0] != "0")
            {
                var tag = tokens[1];
                var kind = tag switch
                {
                    "ASSO" or "ALIA" => "INDI",
                    "ANCI" or "DESI" or "SUBM" => "SUBM",
                    "SUBN" => "SUBN",
                    "REPO" => "REPO",
                    "SNOTE" => "SNOTE",
                    "OBJE" => "OBJE",
                    "SOUR" => "SOUR",
                    "NOTE" => "NOTE",
                    _ => ""
                };
                var required = tag is "ASSO" or "ALIA" or "ANCI" or "DESI" or
                    "SUBM" or "SUBN" or "REPO" or "SNOTE" ||
                    version == "7.0" && !inHeader && tag is ("OBJE" or "SOUR");
                var value = tokens.Length == 3 ? tokens[2] : "";
                var pointerValue = value.StartsWith('@') &&
                    !(tag is "NOTE" or "SOUR" && value.StartsWith("@@", StringComparison.Ordinal));
                if (kind.Length > 0 && (required || pointerValue))
                {
                    if (!Pointer.IsMatch(value))
                        return Reject("Malformed reference pointer");
                    if (version == "7.0" && tag == "NOTE")
                        return Reject("Shared notes require SNOTE in GEDCOM 7");
                    var associationEvent = tokens[0] == "2" &&
                        currentSection is ("ANUL" or "CENS" or "DIV" or "DIVF" or "ENGA" or
                            "MARB" or "MARC" or "MARL" or "MARR" or "MARS" or "EVEN" or
                            "NCHI" or "RESI" or "FACT" or "CAST" or "DSCR" or "EDUC" or
                            "IDNO" or "NATI" or "NMR" or "OCCU" or "PROP" or "RELI" or
                            "SSN" or "TITL" or "ADOP" or "BAPM" or "BARM" or "BASM" or
                            "BIRT" or "BLES" or "BURI" or "CHR" or "CHRA" or "CONF" or
                            "CREM" or "DEAT" or "EMIG" or "FCOM" or "GRAD" or "IMMI" or
                            "NATU" or "ORDN" or "PROB" or "RETI" or "WILL");
                    if (((tag is "ALIA" or "ANCI" or "DESI") &&
                         (tokens[0] != "1" || currentRecordKind != "INDI")) ||
                        (tag == "ASSO" && (inHeader || currentRecordKind is not ("INDI" or "FAM") ||
                         (tokens[0] != "1" && !associationEvent))) ||
                        (tag == "SUBM" && (tokens[0] != "1" ||
                         (!inHeader && currentRecordKind is not ("INDI" or "FAM") &&
                          !(version == "5.5.1" && currentRecordKind == "SUBN")))) ||
                        (tag == "SUBN" && (!inHeader || tokens[0] != "1")) ||
                        (tag == "REPO" && (currentRecordKind != "SOUR" || tokens[0] != "1")) ||
                        (tag == "OBJE" && inHeader))
                        return Reject("Reference pointer in incorrect record");
                    references.Add((value, kind));
                }
            }
            if (!inHeader)
                continue;
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
        if (references.Any(reference => !records.TryGetValue(reference.Pointer, out var kind) ||
                kind != reference.Kind))
            return Reject("Unresolved or incorrect reference pointer");

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

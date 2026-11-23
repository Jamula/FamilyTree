namespace FamilyTree.Domain;

/// <summary>
/// Bounded non-persistent 5.5.1 UTF-8 compatibility PREVIEW only.
/// This is not a full GEDCOM parser, data migrator, or permission to import.
/// It refuses unknown encodings and unsupported versions without guessing.
/// </summary>
public static class GedcomLegacyCompatibility
{
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
        var inHeader = true;
        var versionCount = 0;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length > 1024 || line.Contains('\t') ||
                (line.Contains('\r') && !line.EndsWith("\r", StringComparison.Ordinal)))
                return Reject("Malformed GEDCOM line");
            if (line.StartsWith("0 ", StringComparison.Ordinal))
                inHeader = false;
            if (!inHeader) continue;
            if (line.StartsWith("2 VERS ", StringComparison.Ordinal))
            {
                versionCount++;
                version = line["2 VERS ".Length..];
            }
            if (line == "1 CHAR UTF-8") hasUtf8 = true;
            else if (line.StartsWith("1 CHAR ", StringComparison.Ordinal))
                return Reject("Unsupported GEDCOM character encoding");
        }
        if (versionCount != 1)
            return Reject("Missing or conflicting GEDCOM version");
        if (version == "7.0")
            return GedcomSevenSubset.Preview(normalized);
        if (version != "5.5.1")
            return Reject("Unsupported GEDCOM version");
        if (!hasUtf8)
            return Reject("Legacy GEDCOM encoding must explicitly be UTF-8");

        // The original 5.5.1 file is never overwritten. All records are
        // passed through strict GEDCOM7 structural preflight and review.
        var upgraded = normalized.Replace("\n2 VERS 5.5.1\n",
            "\n2 VERS 7.0\n", StringComparison.Ordinal);
        var preview = GedcomSevenSubset.Preview(upgraded);
        if (!preview.Valid)
            return preview;
        return preview with { Warnings =
            preview.Warnings.Append(
                "5.5.1 UTF-8 converted for inspection only; full import requires review")
            .ToArray() };
    }

    private static GedcomPreviewReport Reject(string text)
        => new(false, [], [], [], [text]);
}

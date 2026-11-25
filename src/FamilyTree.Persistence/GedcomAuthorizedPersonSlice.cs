using FamilyTree.Domain;

namespace FamilyTree.Persistence;

/// <summary>
/// Local synthetic subset adapter, not an authentication boundary or an HTTP
/// endpoint. The viewer must be supplied by a verified server session. Each
/// operation reads one current SQLite authorization snapshot; no caller grants
/// or unfiltered records are accepted. This is not a full-tree export.
/// </summary>
public sealed class GedcomAuthorizedPersonSlice
{
    private const string IncompleteWarning =
        "Incomplete single-person, name-only projection and reconciliation; dates, events, sources, links and media are omitted. Not a backup or approval.";
    private readonly PrivateTreeReadService _read;

    public GedcomAuthorizedPersonSlice(SqliteTreeReadSource source)
    {
        _read = new PrivateTreeReadService(source ?? throw new ArgumentNullException(nameof(source)));
    }

    public async Task<SyntheticGedcomExport?> ExportAsync(Guid verifiedViewer, Guid family,
        Guid branch, Guid person, CancellationToken cancellationToken = default)
    {
        var node = await ReadAsync(verifiedViewer, family, branch, person, cancellationToken);
        if (node is null) return null;
        var result = ExportName(node, cancellationToken);
        return result with { Warnings = result.Warnings.Append(IncompleteWarning).ToArray() };
    }

    public async Task<SyntheticGedcomPreviewResult?> PreviewAsync(Guid verifiedViewer, Guid family,
        Guid branch, Guid person, byte[]? input, CancellationToken cancellationToken = default)
    {
        var node = await ReadAsync(verifiedViewer, family, branch, person, cancellationToken);
        if (node is null) return null;
        // Validate the persisted name independently of the untrusted input.
        // Otherwise an unsupported candidate would look like an invalid import.
        _ = ExportName(node, cancellationToken);
        var result = GedcomSyntheticSlice.Preview(input,
            [new(node.PersonId.ToString("D"), node.DisplayName, null)], cancellationToken);
        if (result.Proposal is null) return result;
        return result with { Proposal = result.Proposal with
        {
            Warnings = result.Proposal.Warnings.Append(IncompleteWarning).ToArray()
        } };
    }

    private static SyntheticGedcomExport ExportName(AuthorizedTreeNode node, CancellationToken cancellationToken)
    {
        var graph = new SyntheticGedcomGraph([new("@I1@", node.DisplayName, [])], [], []);
        try { return GedcomSyntheticSlice.Export(new(graph), cancellationToken); }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException("Authorized name is unsupported by the GEDCOM subset.", exception);
        }
    }

    private async Task<AuthorizedTreeNode?> ReadAsync(Guid viewer, Guid family, Guid branch,
        Guid person, CancellationToken cancellationToken)
    {
        var projection = await _read.GetAsync(viewer, family, branch, person,
            new TreeOptions(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return projection?.Nodes.SingleOrDefault(node => node.PersonId == person);
    }
}

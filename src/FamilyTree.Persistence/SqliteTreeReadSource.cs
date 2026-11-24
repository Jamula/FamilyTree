using FamilyTree.Domain;

namespace FamilyTree.Persistence;

/// <summary>
/// Local-only read source. Viewer IDs must come from a verified server session,
/// not request data. This source is not registered with private HTTP routes.
/// Relationships and other fields remain unavailable until independently persisted.
/// </summary>
public sealed class SqliteTreeReadSource : ITrustedTreeReadSource, IDisposable
{
    private readonly SqliteFamilyStore _store;

    public SqliteTreeReadSource(string absolutePath, byte[] encryptionKey)
    {
        _store = new SqliteFamilyStore(absolutePath, encryptionKey);
        try { _ = new SqliteGraphGrantStore(absolutePath); }
        catch { _store.Dispose(); throw; }
    }

    public Task<TrustedTreeReadSnapshot?> LoadAsync(Guid viewer, Guid family,
        Guid branch, CancellationToken cancellationToken) =>
        _store.LoadTreeSnapshotAsync(viewer, family, branch, cancellationToken);

    internal Task<AuthorizedPersonSummary?> GetPersonAsync(Guid viewer, Guid family,
        Guid branch, Guid person, CancellationToken cancellationToken) =>
        _store.GetAuthorizedPersonAsync(viewer, family, branch, person, cancellationToken);

    internal Task<IReadOnlyList<AuthorizedPersonSummary>> SearchPeopleAsync(Guid viewer,
        Guid family, Guid branch, string text, CancellationToken cancellationToken) =>
        _store.SearchAuthorizedPeopleAsync(viewer, family, branch, text, cancellationToken);

    public void Dispose() => _store.Dispose();
}

public sealed partial class SqliteFamilyStore
{
    internal Task<TrustedTreeReadSnapshot?> LoadTreeSnapshotAsync(Guid viewer,
        Guid family, Guid branch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (viewer == Guid.Empty || family == Guid.Empty || branch == Guid.Empty)
            return Task.FromResult<TrustedTreeReadSnapshot?>(null);

        using var con = Open();
        using var tx = con.BeginTransaction(deferred: true);
        var parameters = new[] { ("$f", (object)family.ToString("D")),
            ("$p", (object)viewer.ToString("D")), ("$b", (object)branch.ToString("D")) };
        if (Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM memberships
            WHERE family_id=$f AND principal_id=$p AND state='Active';
            """, parameters) != 1)
            return Task.FromResult<TrustedTreeReadSnapshot?>(null);

        var people = new List<Person>();
        using (var q = Cmd(con, tx, """
            SELECT person.id, person.encrypted_name, person.living
            FROM person_visibility AS visibility
            JOIN person_display_name_visibility AS field
              ON field.family_id=visibility.family_id AND field.branch_id=visibility.branch_id
              AND field.principal_id=visibility.principal_id AND field.person_id=visibility.person_id
            JOIN people AS person ON person.family_id=visibility.family_id
              AND person.id=visibility.person_id
            WHERE visibility.family_id=$f AND visibility.principal_id=$p
              AND visibility.branch_id=$b ORDER BY person.id LIMIT 10001;
            """, parameters))
        using (var reader = q.ExecuteReader())
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = Guid.Parse(reader.GetString(0));
                people.Add(new Person(id, Decrypt(family, id, reader.GetFieldValue<byte[]>(1)),
                    reader.GetInt64(2) != 0));
            }
        cancellationToken.ThrowIfCancellationRequested();
        var visible = people.Select(person => person.Id).ToHashSet();
        var branchGrant = new BranchVisibilityGrant(viewer, family, branch,
            MembershipState.Active, visible, new HashSet<Guid>());
        var fieldGrants = people.Select(person => new FamilyFieldGrant(viewer, family,
            person.Id, MembershipState.Active, new HashSet<ProfileField> { ProfileField.DisplayName }));
        var snapshot = new TrustedTreeReadSnapshot(viewer, family, branch, true,
            people, [], [branchGrant], new FieldPrivacyPolicy(family, fieldGrants));
        tx.Commit();
        return Task.FromResult<TrustedTreeReadSnapshot?>(snapshot);
    }
}

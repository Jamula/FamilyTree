using FamilyTree.Domain;

namespace FamilyTree.Persistence;

/// <summary>
/// Local read adapter for verified server principals. This is not an HTTP
/// authentication boundary and is not registered with private routes.
/// </summary>
public sealed class SqlitePersonReadService(SqliteTreeReadSource source)
{
    private readonly SqliteTreeReadSource _source =
        source ?? throw new ArgumentNullException(nameof(source));

    public Task<AuthorizedPersonSummary?> GetAsync(Guid viewer, Guid family,
        Guid branch, Guid person, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _source.GetPersonAsync(viewer, family, branch, person, cancellationToken);
    }

    public Task<IReadOnlyList<AuthorizedPersonSummary>> SearchAsync(Guid viewer,
        Guid family, Guid branch, string? text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 80 || text.Trim().Length < 2)
            return Task.FromResult<IReadOnlyList<AuthorizedPersonSummary>>(Array.Empty<AuthorizedPersonSummary>());
        return _source.SearchPeopleAsync(viewer, family, branch, text.Trim(), cancellationToken);
    }
}

public sealed partial class SqliteFamilyStore
{
    private const int PersonSearchBatchSize = 128;
    private const string AuthorizedPeopleSql = """
        SELECT visibility.person_id, person.encrypted_name
        FROM person_visibility AS visibility
        JOIN memberships AS membership ON membership.family_id=visibility.family_id
          AND membership.principal_id=visibility.principal_id AND membership.state='Active'
        JOIN person_display_name_visibility AS field
          ON field.family_id=visibility.family_id AND field.branch_id=visibility.branch_id
          AND field.principal_id=visibility.principal_id AND field.person_id=visibility.person_id
        JOIN people AS person ON person.family_id=visibility.family_id AND person.id=visibility.person_id
        WHERE visibility.family_id=$f AND visibility.branch_id=$b AND visibility.principal_id=$p
        """;

    internal Task<AuthorizedPersonSummary?> GetAuthorizedPersonAsync(Guid viewer, Guid family,
        Guid branch, Guid person, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (viewer == Guid.Empty || family == Guid.Empty || branch == Guid.Empty || person == Guid.Empty)
            return Task.FromResult<AuthorizedPersonSummary?>(null);
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: true);
        using var q = Cmd(con, tx, AuthorizedPeopleSql + " AND visibility.person_id=$id;",
            ("$f", family.ToString("D")), ("$b", branch.ToString("D")),
            ("$p", viewer.ToString("D")), ("$id", person.ToString("D")));
        using var reader = q.ExecuteReader();
        AuthorizedPersonSummary? result = null;
        if (reader.Read())
            result = new(person, Decrypt(family, person, reader.GetFieldValue<byte[]>(1)));
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }

    internal Task<IReadOnlyList<AuthorizedPersonSummary>> SearchAuthorizedPeopleAsync(Guid viewer,
        Guid family, Guid branch, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<AuthorizedPersonSummary>();
        if (viewer == Guid.Empty || family == Guid.Empty || branch == Guid.Empty)
            return Task.FromResult<IReadOnlyList<AuthorizedPersonSummary>>(results);
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: true);
        var after = "";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var q = Cmd(con, tx, AuthorizedPeopleSql + """
                 AND visibility.person_id>$after ORDER BY visibility.person_id LIMIT $limit;
                """, ("$f", family.ToString("D")), ("$b", branch.ToString("D")),
                ("$p", viewer.ToString("D")), ("$after", after), ("$limit", PersonSearchBatchSize));
            using var reader = q.ExecuteReader();
            var count = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                count++;
                after = reader.GetString(0);
                var id = Guid.Parse(after);
                var name = Decrypt(family, id, reader.GetFieldValue<byte[]>(1));
                if (name.Contains(text, StringComparison.OrdinalIgnoreCase))
                    results.Add(new(id, name));
                if (results.Count == 25)
                    return Task.FromResult<IReadOnlyList<AuthorizedPersonSummary>>(results);
            }
            if (count < PersonSearchBatchSize)
                break;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<AuthorizedPersonSummary>>(results);
    }
}

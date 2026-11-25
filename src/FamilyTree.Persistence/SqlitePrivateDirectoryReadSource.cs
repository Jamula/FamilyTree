namespace FamilyTree.Persistence;

public sealed partial class SqliteFamilyStore
{
    public sealed record MemberSummary(Guid PrincipalId, string State, bool Administrator);

    public IReadOnlyList<Guid> ListActiveFamilyIds(Guid principalId)
    {
        RequireIds(principalId);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var familyIds = new List<Guid>();
        using (var command = Cmd(connection, transaction, """
            SELECT family.id
            FROM memberships AS membership
            JOIN families AS family ON family.id=membership.family_id
            WHERE membership.principal_id=$principal AND membership.state='Active'
            ORDER BY family.id LIMIT 100;
            """, ("$principal", principalId.ToString("D"))))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                familyIds.Add(Guid.Parse(reader.GetString(0)));
        transaction.Commit();
        return familyIds;
    }

    public IReadOnlyList<Guid> ListGrantedBranchIds(Guid familyId, Guid principalId)
    {
        RequireIds(familyId, principalId);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        RequireMember(connection, transaction, familyId, principalId, administrator: false);
        var branchIds = new List<Guid>();
        using (var command = Cmd(connection, transaction, """
            SELECT DISTINCT visibility.branch_id
            FROM person_visibility AS visibility
            JOIN person_display_name_visibility AS field
              ON field.family_id=visibility.family_id
              AND field.branch_id=visibility.branch_id
              AND field.principal_id=visibility.principal_id
              AND field.person_id=visibility.person_id
            JOIN people AS person
              ON person.family_id=visibility.family_id
              AND person.id=visibility.person_id
            WHERE visibility.family_id=$family
              AND visibility.principal_id=$principal
            ORDER BY visibility.branch_id LIMIT 100;
            """, ("$family", familyId.ToString("D")),
                ("$principal", principalId.ToString("D"))))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                branchIds.Add(Guid.Parse(reader.GetString(0)));
        transaction.Commit();
        return branchIds;
    }

    public IReadOnlyList<MemberSummary> ListMembersForAdministrator(
        Guid familyId, Guid administratorId)
    {
        RequireIds(familyId, administratorId);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        RequireMember(connection, transaction, familyId, administratorId, administrator: true);
        var members = new List<MemberSummary>();
        using (var command = Cmd(connection, transaction, """
            SELECT principal_id,state,administrator FROM memberships
            WHERE family_id=$family ORDER BY principal_id LIMIT 100;
            """, ("$family", familyId.ToString("D"))))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                members.Add(new MemberSummary(Guid.Parse(reader.GetString(0)),
                    reader.GetString(1), reader.GetInt64(2) != 0));
        transaction.Commit();
        return members;
    }
}

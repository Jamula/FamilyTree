using FamilyTree.Domain;
using Microsoft.Data.Sqlite;

namespace FamilyTree.Persistence;

/// <summary>
/// Local synthetic-only transactional relationship review. All caller IDs
/// must come from a verified, server-trusted identity and persisted membership.
/// No endpoint enables these methods for private records yet.
/// </summary>
public sealed partial class SqliteFamilyStore
{
    public sealed record PendingRelationshipReview(
        Guid ProposalId, Relationship Relationship, Guid SubmittedBy);

    public Guid ProposeRelationship(Guid familyId, Guid authorId, Relationship edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        RequireIds(familyId, authorId, edge.Id, edge.FromPersonId, edge.ToPersonId);
        if (edge.FromPersonId == edge.ToPersonId || !Enum.IsDefined(edge.Kind) ||
            edge.Label is { Length: > 200 } ||
            (edge.Label is not null && edge.Label.Any(char.IsControl)) ||
            (edge.Kind == RelationKind.Other && string.IsNullOrWhiteSpace(edge.Label)))
            throw new ArgumentException("Invalid genealogy relationship.");
        using var con = Open();
        using var tx = con.BeginTransaction();
        RequireMember(con, tx, familyId, authorId, administrator: false);
        var endpoints = Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM people WHERE family_id=$f AND
                id IN ($from,$to);
            """, ("$f", familyId.ToString("D")),
            ("$from", edge.FromPersonId.ToString("D")),
            ("$to", edge.ToPersonId.ToString("D")));
        if (endpoints != 2)
            throw new InvalidOperationException("Approved endpoints unavailable.");
        if (Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM relationships WHERE family_id=$f AND id=$id;
            """, ("$f", familyId.ToString("D")),
            ("$id", edge.Id.ToString("D"))) != 0 ||
            Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM relationship_proposals
            WHERE family_id=$f AND relation_id=$id AND state='Pending';
            """, ("$f", familyId.ToString("D")),
            ("$id", edge.Id.ToString("D"))) != 0)
            throw new InvalidOperationException("Duplicate relationship ID.");

        var proposalId = Guid.NewGuid();
        Exec(con, tx, """
            INSERT INTO relationship_proposals
            (id,family_id,relation_id,from_id,to_id,kind,encrypted_label,
             submitter,state)
            VALUES($proposal,$f,$id,$from,$to,$kind,$label,$submitter,'Pending');
            """,
            ("$proposal", proposalId.ToString("D")),
            ("$f", familyId.ToString("D")), ("$id", edge.Id.ToString("D")),
            ("$from", edge.FromPersonId.ToString("D")),
            ("$to", edge.ToPersonId.ToString("D")),
            ("$kind", edge.Kind.ToString()),
            ("$label", Encrypt(familyId, edge.Id, edge.Label ?? "")),
            ("$submitter", authorId.ToString("D")));
        tx.Commit();
        return proposalId;
    }

    public IReadOnlyList<PendingRelationshipReview> ListPendingRelationships(
        Guid familyId, Guid moderatorId)
    {
        RequireIds(familyId, moderatorId);
        using var con = Open();
        using var tx = con.BeginTransaction();
        RequireMember(con, tx, familyId, moderatorId, administrator: true);
        var results = new List<PendingRelationshipReview>();
        using (var cmd = Cmd(con, tx, """
            SELECT id,relation_id,from_id,to_id,kind,encrypted_label,submitter
            FROM relationship_proposals WHERE family_id=$f AND state='Pending'
            ORDER BY id LIMIT 100;
            """, ("$f", familyId.ToString("D"))))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
            {
                var edge = MaterializeRelationship(familyId,
                    reader.GetString(1),reader.GetString(2),reader.GetString(3),
                    reader.GetString(4),reader.GetFieldValue<byte[]>(5));
                results.Add(new(Guid.Parse(reader.GetString(0)),edge,
                    Guid.Parse(reader.GetString(6))));
            }
        tx.Commit();
        return results;
    }

    public void ReviewRelationship(Guid familyId, Guid moderatorId,
        Guid proposalId, bool approve)
    {
        RequireIds(familyId, moderatorId, proposalId);
        using var con = Open();
        using var tx = con.BeginTransaction();
        RequireMember(con, tx, familyId, moderatorId, administrator: true);
        Relationship edge;
        byte[] encrypted;
        using (var cmd = Cmd(con, tx, """
            SELECT relation_id,from_id,to_id,kind,encrypted_label,submitter
            FROM relationship_proposals WHERE family_id=$f AND id=$proposal
              AND state='Pending';
            """, ("$f", familyId.ToString("D")),
            ("$proposal", proposalId.ToString("D"))))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read())
                throw new InvalidOperationException("Pending relationship unavailable.");
            if (reader.GetString(5) == moderatorId.ToString("D"))
                throw new UnauthorizedAccessException("Independent moderator required.");
            encrypted = reader.GetFieldValue<byte[]>(4);
            edge = MaterializeRelationship(familyId,
                reader.GetString(0),reader.GetString(1),reader.GetString(2),
                reader.GetString(3),encrypted);
        }
        if (approve)
        {
            var endpoints = Scalar<long>(con, tx, """
                SELECT COUNT(*) FROM people WHERE family_id=$f
                AND id IN ($from,$to);
                """, ("$f", familyId.ToString("D")),
                ("$from", edge.FromPersonId.ToString("D")),
                ("$to", edge.ToPersonId.ToString("D")));
            if (endpoints != 2)
                throw new InvalidOperationException("Relationship endpoints removed.");
            Exec(con, tx, """
                INSERT INTO relationships
                (family_id,id,from_id,to_id,kind,encrypted_label)
                VALUES($f,$id,$from,$to,$kind,$label);
                """, ("$f", familyId.ToString("D")),
                ("$id", edge.Id.ToString("D")),
                ("$from", edge.FromPersonId.ToString("D")),
                ("$to", edge.ToPersonId.ToString("D")),
                ("$kind", edge.Kind.ToString()),
                ("$label", encrypted));
        }
        if (Exec(con, tx, """
            UPDATE relationship_proposals SET state=$state,reviewer=$reviewer
            WHERE family_id=$f AND id=$id AND state='Pending';
            """, ("$state", approve ? "Approved" : "Rejected"),
            ("$reviewer", moderatorId.ToString("D")),
            ("$f", familyId.ToString("D")),
            ("$id", proposalId.ToString("D"))) != 1)
            throw new InvalidOperationException("Pending relationship unavailable.");
        Audit(con, tx, familyId, proposalId, moderatorId,
            approve ? "relationship-approved" : "relationship-rejected");
        tx.Commit();
    }

    public IReadOnlyList<Relationship> ReadRelationships(Guid familyId, Guid viewerId)
    {
        RequireIds(familyId, viewerId);
        using var con = Open();
        using var tx = con.BeginTransaction();
        RequireMember(con, tx, familyId, viewerId, administrator: false);
        var result = new List<Relationship>();
        using (var cmd = Cmd(con, tx, """
            SELECT id,from_id,to_id,kind,encrypted_label
            FROM relationships WHERE family_id=$f ORDER BY id;
            """, ("$f", familyId.ToString("D"))))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                result.Add(MaterializeRelationship(familyId,
                    reader.GetString(0),reader.GetString(1),reader.GetString(2),
                    reader.GetString(3),reader.GetFieldValue<byte[]>(4)));
        tx.Commit();
        return result;
    }

    /// <summary>
    /// Independent, audited moderator reversal. No hard deletion of the audit
    /// record and no API path that can bypass membership/roles.
    /// </summary>
    public void UndoRelationship(Guid familyId, Guid moderatorId, Guid proposalId)
    {
        RequireIds(familyId, moderatorId, proposalId);
        using var con = Open();
        using var tx = con.BeginTransaction();
        RequireMember(con, tx, familyId, moderatorId, administrator: true);
        string relationId;
        using (var cmd = Cmd(con, tx, """
            SELECT relation_id, submitter FROM relationship_proposals
            WHERE family_id=$f AND id=$id AND state='Approved';
            """, ("$f", familyId.ToString("D")),
            ("$id", proposalId.ToString("D"))))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read())
                throw new InvalidOperationException("Approved relationship unavailable.");
            if (reader.GetString(1) == moderatorId.ToString("D"))
                throw new UnauthorizedAccessException("Independent moderator required.");
            relationId = reader.GetString(0);
        }
        if (Exec(con, tx, """
            DELETE FROM relationships WHERE family_id=$f AND id=$id;
            """, ("$f", familyId.ToString("D")), ("$id", relationId)) != 1)
            throw new InvalidOperationException("Approved link unavailable.");
        Exec(con, tx, """
            UPDATE relationship_proposals SET state='Undone' WHERE family_id=$f
            AND id=$id AND state='Approved';
            """, ("$f", familyId.ToString("D")),
            ("$id", proposalId.ToString("D")));
        Audit(con, tx, familyId, proposalId, moderatorId,
            "relationship-undone");
        tx.Commit();
    }

    private Relationship MaterializeRelationship(Guid familyId, string idText,
        string fromText, string toText, string kind, byte[] envelope)
    {
        if (!Guid.TryParse(idText, out var id) ||
            !Guid.TryParse(fromText, out var from) ||
            !Guid.TryParse(toText, out var to) ||
            id == Guid.Empty || from == Guid.Empty || to == Guid.Empty ||
            from == to ||
            !Enum.TryParse<RelationKind>(kind, ignoreCase: false, out var relationKind) ||
            !Enum.IsDefined(relationKind))
            throw new InvalidDataException("Relationship record invalid.");
        var label = Decrypt(familyId, id, envelope);
        if (label.Length > 200 || label.Any(char.IsControl) ||
            (relationKind == RelationKind.Other && string.IsNullOrWhiteSpace(label)))
            throw new InvalidDataException("Relationship record invalid.");
        return new(id, from, to, relationKind, label.Length == 0 ? null : label);
    }
}

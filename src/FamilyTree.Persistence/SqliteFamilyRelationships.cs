using System.Text;
using FamilyTree.Domain;
using Microsoft.Data.Sqlite;

namespace FamilyTree.Persistence;

/// <summary>
/// Local synthetic-only transactional relationship review. All caller IDs
/// must come from a verified, server-trusted identity and persisted membership.
/// No endpoint enables these methods for private records yet.
/// Legacy relationship APIs are family-wide and unsafe for private scoped data.
/// Granting any scoped relationship permission permanently disables those APIs
/// for that family; revoking the grant does not restore legacy access.
/// </summary>
public sealed partial class SqliteFamilyStore
{
    public sealed record PendingRelationshipReview(
        Guid ProposalId, Relationship? Relationship, Guid SubmittedBy,
        bool ContentUnreadable, string Cursor);

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
        using var tx = con.BeginTransaction(deferred: false);
        RequireMember(con, tx, familyId, authorId, administrator: false);
        DenyLegacyRelationshipsForScopedFamily(con, tx, familyId);
        EnsureScopedPersonSchema(con, tx);
        if (Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM scoped_person_proposals WHERE family_id=$f
            AND person_id IN ($from,$to);
            """, ("$f", familyId.ToString("D")),
            ("$from", edge.FromPersonId.ToString("D")),
            ("$to", edge.ToPersonId.ToString("D"))) != 0)
            throw new InvalidOperationException("Approved endpoints unavailable.");
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
             submitter,identity_envelope,state)
            VALUES($proposal,$f,$id,$from,$to,$kind,$label,$submitter,
                   $identity,'Pending');
            """,
            ("$proposal", proposalId.ToString("D")),
            ("$f", familyId.ToString("D")), ("$id", edge.Id.ToString("D")),
            ("$from", edge.FromPersonId.ToString("D")),
            ("$to", edge.ToPersonId.ToString("D")),
            ("$kind", edge.Kind.ToString()),
            ("$label", Encrypt(familyId, edge.Id,
                edge.Label is null ? "\u0000" : "\u0001" + edge.Label,
                RelationshipContext(proposalId, authorId, edge.Id,
                    edge.FromPersonId, edge.ToPersonId, edge.Kind))),
            ("$submitter", authorId.ToString("D")),
            ("$identity", EncryptRelationshipProposalIdentity(familyId,
                proposalId, authorId, edge.Id, edge.FromPersonId,
                edge.ToPersonId, edge.Kind)));
        tx.Commit();
        return proposalId;
    }

    public IReadOnlyList<PendingRelationshipReview> ListPendingRelationships(
        Guid familyId, Guid moderatorId, string? afterCursor = null)
    {
        RequireIds(familyId, moderatorId);
        var afterId = afterCursor is null
            ? null : DecodeRelationshipProposalCursor(afterCursor);
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: false);
        RequireMember(con, tx, familyId, moderatorId, administrator: true);
        DenyLegacyRelationshipsForScopedFamily(con, tx, familyId);
        var results = new List<PendingRelationshipReview>();
        using (var cmd = Cmd(con, tx, """
            SELECT id,relation_id,from_id,to_id,kind,encrypted_label,submitter,
                   identity_envelope
            FROM relationship_proposals
            WHERE family_id=$f AND state='Pending' AND submitter<>$moderator
                AND ($after IS NULL OR id>$after)
            ORDER BY id LIMIT 100;
            """, ("$f", familyId.ToString("D")),
            ("$moderator", moderatorId.ToString("D")),
            ("$after", (object?)afterId ?? DBNull.Value)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
            {
                Relationship? edge = null;
                var proposalId = Guid.TryParse(reader.GetString(0), out var parsedId)
                    ? parsedId : Guid.Empty;
                var submitter = Guid.TryParse(reader.GetString(6), out var parsedSubmitter)
                    ? parsedSubmitter : Guid.Empty;
                var unreadable = proposalId == Guid.Empty || submitter == Guid.Empty;
                if (!unreadable)
                {
                    try
                    {
                        VerifyRelationshipProposalIdentity(familyId, proposalId,
                            reader.GetString(1), reader.GetString(2), reader.GetString(3),
                            reader.GetString(4), submitter,
                            reader.GetFieldValue<byte[]>(7));
                        edge = MaterializeRelationship(familyId,
                            proposalId, submitter, reader.GetString(1), reader.GetString(2),
                            reader.GetString(3), reader.GetString(4),
                            reader.GetFieldValue<byte[]>(5));
                    }
                    catch (InvalidDataException)
                    {
                        // Keep a content-free proposal ID visible for an
                        // independent moderator to reject corrupted submissions.
                        unreadable = true;
                    }
                }
                results.Add(new(proposalId, edge, submitter, unreadable,
                    EncodeRelationshipProposalCursor(reader.GetString(0))));
            }
        tx.Commit();
        return results;
    }

    public void ReviewRelationship(Guid familyId, Guid moderatorId,
        Guid proposalId, bool approve)
    {
        RequireIds(familyId, moderatorId, proposalId);
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: false);
        RequireMember(con, tx, familyId, moderatorId, administrator: true);
        DenyLegacyRelationshipsForScopedFamily(con, tx, familyId);
        string relationId, fromId, toId, kind;
        Guid submitter;
        byte[] encrypted, identityEnvelope;
        using (var cmd = Cmd(con, tx, """
            SELECT relation_id,from_id,to_id,kind,encrypted_label,submitter,
                   identity_envelope
            FROM relationship_proposals WHERE family_id=$f AND id=$proposal
              AND state='Pending';
            """, ("$f", familyId.ToString("D")),
            ("$proposal", proposalId.ToString("D"))))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read())
                throw new InvalidOperationException("Pending relationship unavailable.");
            relationId = reader.GetString(0);
            fromId = reader.GetString(1);
            toId = reader.GetString(2);
            kind = reader.GetString(3);
            encrypted = reader.GetFieldValue<byte[]>(4);
            submitter = Guid.Parse(reader.GetString(5));
            identityEnvelope = reader.GetFieldValue<byte[]>(6);
        }
        VerifyRelationshipProposalIdentity(familyId, proposalId, relationId,
            fromId, toId, kind, submitter, identityEnvelope);
        if (submitter == moderatorId)
            throw new UnauthorizedAccessException("Independent moderator required.");
        if (approve)
        {
            // Authentication of encrypted payload is essential for approval;
            // rejection must stay possible even for broken ciphertext.
            var edge = MaterializeRelationship(
                familyId, proposalId, submitter, relationId, fromId, toId,
                kind, encrypted);
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
                (family_id,id,from_id,to_id,kind,encrypted_label,proposal_id,submitter)
                VALUES($f,$id,$from,$to,$kind,$label,$proposal,$submitter);
                """, ("$f", familyId.ToString("D")),
                ("$id", edge.Id.ToString("D")),
                ("$from", edge.FromPersonId.ToString("D")),
                ("$to", edge.ToPersonId.ToString("D")),
                ("$kind", edge.Kind.ToString()),
                ("$label", encrypted), ("$proposal", proposalId.ToString("D")),
                ("$submitter", submitter.ToString("D")));
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

    /// <summary>
    /// Authenticated administrator's durable undo inbox. No relationship names
    /// are logged. Does not include a moderator's own proposals to prevent
    /// self-approval or self-undo through discovery.
    /// </summary>
    public sealed record ApprovedRelationshipUndo(
        Guid ProposalId, Relationship? Relationship, Guid SubmittedBy,
        bool ContentUnreadable, string Cursor);

    public IReadOnlyList<ApprovedRelationshipUndo> ListApprovedRelationshipsForUndo(
        Guid familyId, Guid moderatorId, string? afterCursor = null)
    {
        RequireIds(familyId, moderatorId);
        var afterId = afterCursor is null
            ? null : DecodeRelationshipProposalCursor(afterCursor);
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: false);
        RequireMember(con, tx, familyId, moderatorId, administrator: true);
        DenyLegacyRelationshipsForScopedFamily(con, tx, familyId);
        var result = new List<ApprovedRelationshipUndo>();
        using (var cmd = Cmd(con, tx, """
            SELECT id,relation_id,from_id,to_id,kind,encrypted_label,submitter,
                   identity_envelope
            FROM relationship_proposals WHERE family_id=$f AND state='Approved'
                AND submitter<>$moderator AND ($after IS NULL OR id>$after)
            ORDER BY id LIMIT 100;
            """, ("$f", familyId.ToString("D")),
            ("$moderator", moderatorId.ToString("D")),
            ("$after", (object?)afterId ?? DBNull.Value)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
            {
                Relationship? edge = null;
                var proposalId = Guid.TryParse(reader.GetString(0), out var parsedId)
                    ? parsedId : Guid.Empty;
                var submitter = Guid.TryParse(reader.GetString(6), out var parsedSubmitter)
                    ? parsedSubmitter : Guid.Empty;
                var unreadable = proposalId == Guid.Empty || submitter == Guid.Empty;
                if (!unreadable)
                {
                    try
                    {
                        VerifyRelationshipProposalIdentity(familyId, proposalId,
                            reader.GetString(1), reader.GetString(2), reader.GetString(3),
                            reader.GetString(4), submitter,
                            reader.GetFieldValue<byte[]>(7));
                        edge = MaterializeRelationship(familyId,
                            proposalId, submitter, reader.GetString(1), reader.GetString(2),
                            reader.GetString(3), reader.GetString(4),
                            reader.GetFieldValue<byte[]>(5));
                    }
                    catch (InvalidDataException)
                    {
                        unreadable = true;
                    }
                }
                result.Add(new ApprovedRelationshipUndo(
                    proposalId, edge, submitter, unreadable,
                    EncodeRelationshipProposalCursor(reader.GetString(0))));
            }
        tx.Commit();
        return result;
    }

    private const string RelationshipProposalCursorPrefix = "rp:";

    private static string EncodeRelationshipProposalCursor(string orderingKey) =>
        RelationshipProposalCursorPrefix +
        Convert.ToBase64String(Encoding.UTF8.GetBytes(orderingKey));

    private static string DecodeRelationshipProposalCursor(string cursor)
    {
        if (!cursor.StartsWith(RelationshipProposalCursorPrefix,
                StringComparison.Ordinal))
            throw new ArgumentException("Invalid relationship proposal cursor.",
                nameof(cursor));
        try
        {
            return new UTF8Encoding(false, true).GetString(
                Convert.FromBase64String(
                    cursor[RelationshipProposalCursorPrefix.Length..]));
        }
        catch (Exception exception) when (
            exception is FormatException or DecoderFallbackException)
        {
            throw new ArgumentException("Invalid relationship proposal cursor.",
                nameof(cursor), exception);
        }
    }

    public IReadOnlyList<Relationship> ReadRelationships(Guid familyId, Guid viewerId)
    {
        RequireIds(familyId, viewerId);
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: false);
        RequireMember(con, tx, familyId, viewerId, administrator: false);
        DenyLegacyRelationshipsForScopedFamily(con, tx, familyId);
        var result = new List<Relationship>();
        using (var cmd = Cmd(con, tx, """
            SELECT id,from_id,to_id,kind,encrypted_label,proposal_id,submitter
            FROM relationships WHERE family_id=$f ORDER BY id;
            """, ("$f", familyId.ToString("D"))))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                result.Add(MaterializeRelationship(familyId,
                    Guid.Parse(reader.GetString(5)), Guid.Parse(reader.GetString(6)),
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
        using var tx = con.BeginTransaction(deferred: false);
        RequireMember(con, tx, familyId, moderatorId, administrator: true);
        DenyLegacyRelationshipsForScopedFamily(con, tx, familyId);
        string relationId, from, to, kind;
        Guid submitter;
        byte[] authenticatedEnvelope, identityEnvelope;
        using (var cmd = Cmd(con, tx, """
            SELECT relation_id,from_id,to_id,kind,encrypted_label,submitter,
                   identity_envelope
            FROM relationship_proposals WHERE family_id=$f AND id=$id
                AND state='Approved';
            """, ("$f", familyId.ToString("D")),
            ("$id", proposalId.ToString("D"))))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read())
                throw new InvalidOperationException("Approved relationship unavailable.");
            relationId = reader.GetString(0);
            from = reader.GetString(1);
            to = reader.GetString(2);
            kind = reader.GetString(3);
            authenticatedEnvelope = reader.GetFieldValue<byte[]>(4);
            submitter = Guid.Parse(reader.GetString(5));
            identityEnvelope = reader.GetFieldValue<byte[]>(6);
        }
        VerifyRelationshipProposalIdentity(familyId, proposalId, relationId,
            from, to, kind, submitter, identityEnvelope);
        if (submitter == moderatorId)
            throw new UnauthorizedAccessException("Independent moderator required.");
        // The proposal ID alone never authorizes deletion. Authenticate the
        // encrypted proposal against its immutable identity and full metadata.
        // Then validate the actual published row in this same transaction.
        var proposed = MaterializeRelationship(
            familyId, proposalId, submitter, relationId, from, to, kind,
            authenticatedEnvelope);
        Relationship published;
        byte[] publishedEnvelope;
        Guid publishedProposalId, publishedSubmitter;
        using (var cmd = Cmd(con, tx, """
            SELECT from_id,to_id,kind,encrypted_label,proposal_id,submitter
            FROM relationships
            WHERE family_id=$f AND id=$id;
            """, ("$f", familyId.ToString("D")),
            ("$id", relationId)))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read())
                throw new InvalidOperationException("Approved link unavailable.");
            publishedEnvelope = reader.GetFieldValue<byte[]>(3);
            publishedProposalId = Guid.Parse(reader.GetString(4));
            publishedSubmitter = Guid.Parse(reader.GetString(5));
            published = MaterializeRelationship(familyId,
                publishedProposalId, publishedSubmitter, relationId,
                reader.GetString(0), reader.GetString(1),
                reader.GetString(2), publishedEnvelope);
        }
        if (publishedProposalId != proposalId || publishedSubmitter != submitter ||
            published != proposed ||
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                authenticatedEnvelope, publishedEnvelope))
            throw new InvalidDataException("Approved relationship metadata was changed.");

        if (Exec(con, tx, """
            DELETE FROM relationships WHERE family_id=$f AND id=$id;
            """, ("$f", familyId.ToString("D")), ("$id", relationId)) != 1)
            throw new InvalidOperationException("Approved link unavailable.");
        if (Exec(con, tx, """
            UPDATE relationship_proposals SET state='Undone' WHERE family_id=$f
            AND id=$id AND state='Approved';
            """, ("$f", familyId.ToString("D")),
            ("$id", proposalId.ToString("D"))) != 1)
            throw new InvalidOperationException("Approved proposal unavailable.");
        Audit(con, tx, familyId, proposalId, moderatorId,
            "relationship-undone");
        tx.Commit();
    }

    private static byte[] RelationshipContext(Guid proposalId, Guid submitter,
        Guid relationId, Guid from, Guid to, RelationKind kind)
        => [.."familytree.relationship-proposal-label.v1"u8.ToArray(),
            ..proposalId.ToByteArray(), ..submitter.ToByteArray(),
            ..relationId.ToByteArray(),
            ..from.ToByteArray(), ..to.ToByteArray(),
            ..System.Text.Encoding.ASCII.GetBytes(
                ((int)kind).ToString(System.Globalization.CultureInfo.InvariantCulture))];

    private static void DenyLegacyRelationshipsForScopedFamily(
        SqliteConnection con, SqliteTransaction tx, Guid familyId)
    {
        if (Scalar<long>(con, tx,
            "SELECT COUNT(*) FROM scoped_relationship_families WHERE family_id=$f;",
            ("$f", familyId.ToString("D"))) != 0)
            throw new UnauthorizedAccessException("Access denied.");
    }

    private byte[] EncryptRelationshipProposalIdentity(Guid familyId,
        Guid proposalId, Guid submitter, Guid relationId, Guid from, Guid to,
        RelationKind kind)
        => Encrypt(familyId, proposalId, "relationship-proposal.v1",
            RelationshipIdentityContext(submitter, relationId, from, to, kind));

    private void VerifyRelationshipProposalIdentity(Guid familyId,
        Guid proposalId, string relationIdText, string fromText, string toText,
        string kindText, Guid submitter, byte[] identityEnvelope)
    {
        if (proposalId == Guid.Empty || submitter == Guid.Empty ||
            !Guid.TryParse(relationIdText, out var relationId) ||
            !Guid.TryParse(fromText, out var from) ||
            !Guid.TryParse(toText, out var to) ||
            relationId == Guid.Empty || from == Guid.Empty || to == Guid.Empty ||
            from == to ||
            !Enum.TryParse<RelationKind>(kindText, ignoreCase: false, out var kind) ||
            !Enum.IsDefined(kind) ||
            Decrypt(familyId, proposalId, identityEnvelope,
                RelationshipIdentityContext(submitter, relationId, from, to, kind))
                != "relationship-proposal.v1")
            throw new InvalidDataException("Relationship proposal identity invalid.");
    }

    private static byte[] RelationshipIdentityContext(Guid submitter,
        Guid relationId, Guid from, Guid to, RelationKind kind)
        => [.."familytree.relationship-proposal-identity.v1"u8.ToArray(),
            ..submitter.ToByteArray(), ..relationId.ToByteArray(),
            ..from.ToByteArray(), ..to.ToByteArray(),
            ..System.Text.Encoding.ASCII.GetBytes(
                ((int)kind).ToString(System.Globalization.CultureInfo.InvariantCulture))];

    private Relationship MaterializeRelationship(Guid familyId, Guid proposalId,
        Guid submitter, string idText, string fromText, string toText,
        string kind, byte[] envelope)
    {
        if (!Guid.TryParse(idText, out var id) ||
            !Guid.TryParse(fromText, out var from) ||
            !Guid.TryParse(toText, out var to) ||
            id == Guid.Empty || from == Guid.Empty || to == Guid.Empty ||
            from == to ||
            !Enum.TryParse<RelationKind>(kind, ignoreCase: false, out var relationKind) ||
            !Enum.IsDefined(relationKind))
            throw new InvalidDataException("Relationship record invalid.");
        if (proposalId == Guid.Empty || submitter == Guid.Empty)
            throw new InvalidDataException("Relationship record invalid.");
        var encoded = Decrypt(familyId, id, envelope,
            RelationshipContext(proposalId, submitter, id,
                from, to, relationKind));
        // Versioned encrypted payload distinguishes null from explicitly empty.
        // Old pre-MVP synthetic records without a prefix remain readable:
        // previously ambiguous empty values preserve their legacy null mapping.
        string? label = encoded switch
        {
            "\u0000" => null,
            _ when encoded.StartsWith("\u0001", StringComparison.Ordinal) => encoded[1..],
            _ => encoded.Length == 0 ? null : encoded
        };
        if (label is { Length: > 200 } ||
            (label is not null && label.Any(char.IsControl)) ||
            (relationKind == RelationKind.Other && string.IsNullOrWhiteSpace(label)))
            throw new InvalidDataException("Relationship record invalid.");
        return new(id, from, to, relationKind, label);
    }
}

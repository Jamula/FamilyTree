using System.Security.Cryptography;
using System.Text;
using FamilyTree.Domain;
using Microsoft.Data.Sqlite;

namespace FamilyTree.Persistence;

public sealed partial class SqliteFamilyStore
{
    private static void EnsureRelationshipVisibilitySchema(SqliteConnection con, SqliteTransaction tx)
    {
        Exec(con, tx, """
            CREATE TABLE IF NOT EXISTS scoped_relationship_visibility (
              family_id TEXT NOT NULL, branch_id TEXT NOT NULL, principal_id TEXT NOT NULL,
              relation_id TEXT NOT NULL, envelope BLOB NOT NULL,
              PRIMARY KEY(family_id,branch_id,principal_id,relation_id),
              FOREIGN KEY(family_id,principal_id) REFERENCES memberships(family_id,principal_id),
              FOREIGN KEY(family_id,relation_id) REFERENCES scoped_relationship_publications(family_id,relation_id));
            CREATE TABLE IF NOT EXISTS scoped_relationship_visibility_audit (
              seq INTEGER PRIMARY KEY AUTOINCREMENT, family_id TEXT NOT NULL, branch_id TEXT NOT NULL,
              principal_id TEXT NOT NULL, relation_id TEXT NOT NULL, actor_id TEXT,
              action TEXT NOT NULL, created_utc TEXT NOT NULL);
            CREATE TRIGGER IF NOT EXISTS relationship_visibility_membership_change
            AFTER UPDATE OF state ON memberships WHEN OLD.state<>NEW.state BEGIN
              INSERT INTO scoped_relationship_visibility_audit
                (family_id,branch_id,principal_id,relation_id,actor_id,action,created_utc)
              SELECT family_id,branch_id,principal_id,relation_id,NULL,'membership-changed',
                strftime('%Y-%m-%dT%H:%M:%fZ','now') FROM scoped_relationship_visibility
              WHERE family_id=OLD.family_id AND principal_id=OLD.principal_id;
              DELETE FROM scoped_relationship_visibility
              WHERE family_id=OLD.family_id AND principal_id=OLD.principal_id;
            END;
            CREATE TRIGGER IF NOT EXISTS relationship_visibility_publication_delete
            BEFORE DELETE ON scoped_relationship_publications BEGIN
              INSERT INTO scoped_relationship_visibility_audit
                (family_id,branch_id,principal_id,relation_id,actor_id,action,created_utc)
              SELECT family_id,branch_id,principal_id,relation_id,NULL,'publication-removed',
                strftime('%Y-%m-%dT%H:%M:%fZ','now') FROM scoped_relationship_visibility
              WHERE family_id=OLD.family_id AND relation_id=OLD.relation_id;
              DELETE FROM scoped_relationship_visibility
              WHERE family_id=OLD.family_id AND relation_id=OLD.relation_id;
            END;
            """);
    }

    /// <summary>
    /// Grants one approved scoped publication. Both administrator and recipient
    /// must currently have both endpoint and display-name grants in this branch.
    /// The administrator must also hold visibility for this relationship or an
    /// branch Undo permission for an independent author's publication. Revoking
    /// read visibility alone does not remove Undo access or delegation.
    /// The caller must supply a principal established by a trusted server.
    /// </summary>
    public void GrantScopedRelationshipVisibility(Guid family, Guid branch, Guid administrator,
        Guid recipient, Guid relationship)
    {
        RequireIds(family, branch, administrator, recipient, relationship);
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: false);
        RequireMember(con, tx, family, administrator, true);
        RequireMember(con, tx, family, recipient, false);
        var publication = ReadVisiblePublication(con, tx, family, branch, administrator, relationship, false);
        var recipientCanSee = false;
        try
        {
            recipientCanSee = publication is not null && HasVisibleEndpoints(con, tx, family, branch, recipient,
                publication.Link.FromPersonId, publication.Link.ToPersonId);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or
            FormatException or InvalidCastException or OverflowException or ArgumentException) { }
        if (!recipientCanSee)
            throw new UnauthorizedAccessException("Access denied.");
        var envelope = Encrypt(family, relationship, "relationship-visibility-v1",
            VisibilityContext(branch, recipient));
        var changed = Exec(con, tx, """
            INSERT INTO scoped_relationship_visibility(family_id,branch_id,principal_id,relation_id,envelope)
            VALUES($f,$b,$p,$r,$e)
            ON CONFLICT(family_id,branch_id,principal_id,relation_id) DO UPDATE SET envelope=excluded.envelope;
            """, ("$f", family.ToString("D")), ("$b", branch.ToString("D")),
            ("$p", recipient.ToString("D")), ("$r", relationship.ToString("D")), ("$e", envelope));
        if (changed != 0) AuditVisibility(con, tx, family, branch, administrator, recipient, relationship, "granted");
        tx.Commit();
    }

    /// <summary>Idempotently revokes without disclosing whether a relationship exists.</summary>
    public void RevokeScopedRelationshipVisibility(Guid family, Guid branch, Guid administrator,
        Guid recipient, Guid relationship)
    {
        RequireIds(family, branch, administrator, recipient, relationship);
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: false);
        RequireMember(con, tx, family, administrator, true);
        RequireMember(con, tx, family, recipient, false);
        if (Exec(con, tx, """
            DELETE FROM scoped_relationship_visibility
            WHERE family_id=$f AND branch_id=$b AND principal_id=$p AND relation_id=$r;
            """, ("$f", family.ToString("D")), ("$b", branch.ToString("D")),
            ("$p", recipient.ToString("D")), ("$r", relationship.ToString("D"))) != 0)
            AuditVisibility(con, tx, family, branch, administrator, recipient, relationship, "revoked");
        tx.Commit();
    }

    public sealed record ScopedRelationshipVisibilityPage(
        IReadOnlyList<RelationshipProvenance> Entries, Guid? NextCursor);

    /// <summary>
    /// Returns only explicitly granted, authenticated publications in a single
    /// transaction snapshot. Unknown and hidden IDs both return null.
    /// </summary>
    public RelationshipProvenance? GetVisibleScopedRelationship(Guid family, Guid branch,
        Guid viewer, Guid relationship)
    {
        RequireIds(family, branch, viewer, relationship);
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: true);
        if (!IsActiveVisibilityMember(con, tx, family, viewer)) return null;
        return ReadVisiblePublication(con, tx, family, branch, viewer, relationship, true);
    }

    /// <summary>
    /// Pages by relationship ID, returning at most 100 authenticated entries.
    /// The continuation is based only on returned entries, not hidden rows.
    /// </summary>
    public ScopedRelationshipVisibilityPage ListVisibleScopedRelationships(Guid family, Guid branch,
        Guid viewer, Guid? afterRelationshipId = null)
    {
        RequireIds(family, branch, viewer);
        if (afterRelationshipId == Guid.Empty) throw new ArgumentException("Invalid cursor.");
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: true);
        var result = new List<RelationshipProvenance>();
        if (!IsActiveVisibilityMember(con, tx, family, viewer)) return new(result, null);
        var after = afterRelationshipId?.ToString("D") ?? "";
        while (true)
        {
            var ids = new List<string>();
            using (var cmd = Cmd(con, tx, """
                SELECT relation_id FROM scoped_relationship_visibility
                WHERE family_id=$f AND branch_id=$b AND principal_id=$p AND relation_id>$after
                  AND typeof(relation_id)='text'
                ORDER BY relation_id LIMIT 128;
                """, ("$f", family.ToString("D")), ("$b", branch.ToString("D")),
                ("$p", viewer.ToString("D")), ("$after", after)))
            using (var reader = cmd.ExecuteReader())
                while (reader.Read()) ids.Add(reader.GetString(0));
            foreach (var value in ids)
            {
                after = value;
                if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty ||
                    value != id.ToString("D")) continue;
                var item = ReadVisiblePublication(con, tx, family, branch, viewer, id, true);
                if (item is null) continue;
                if (result.Count == 100) return new(result, result[^1].Link.Id);
                result.Add(item);
            }
            if (ids.Count < 128) return new(result, null);
        }
    }

    private RelationshipProvenance? ReadVisiblePublication(SqliteConnection con, SqliteTransaction tx,
        Guid family, Guid branch, Guid viewer, Guid relationship, bool requireGrant)
    {
        try
        {
            string proposalText;
            byte[] published;
            using (var cmd = Cmd(con, tx, """
                SELECT proposal_id,payload FROM scoped_relationship_publications
                WHERE family_id=$f AND branch_id=$b AND relation_id=$r;
                """, ("$f", family.ToString("D")), ("$b", branch.ToString("D")),
                ("$r", relationship.ToString("D"))))
            using (var reader = cmd.ExecuteReader())
            {
                if (!reader.Read()) return null;
                proposalText = reader.GetString(0);
                published = reader.GetFieldValue<byte[]>(1);
            }
            if (!Guid.TryParseExact(proposalText, "D", out var proposal) || proposal == Guid.Empty ||
                proposalText != proposal.ToString("D")) return null;
            var loaded = LoadScopedLink(con, tx, family, branch, proposal, "Approved");
            var edge = loaded.Proposal.Provenance.Link;
            if (edge.Id != relationship || !CryptographicOperations.FixedTimeEquals(published, loaded.Payload) ||
                !HasVisibleEndpoints(con, tx, family, branch, viewer, edge.FromPersonId, edge.ToPersonId))
                return null;
            using var grantCmd = Cmd(con, tx, """
                    SELECT envelope FROM scoped_relationship_visibility
                    WHERE family_id=$f AND branch_id=$b AND principal_id=$p AND relation_id=$r;
                    """, ("$f", family.ToString("D")), ("$b", branch.ToString("D")),
                    ("$p", viewer.ToString("D")), ("$r", relationship.ToString("D")));
            var grant = grantCmd.ExecuteScalar();
            var visible = grant is byte[] envelope &&
                Decrypt(family, relationship, envelope, VisibilityContext(branch, viewer)) == "relationship-visibility-v1";
            if (!visible && (requireGrant || loaded.Proposal.SubmittedBy == viewer ||
                Scalar<long>(con, tx, """
                    SELECT COUNT(*) FROM scoped_relationship_grants
                    WHERE family_id=$f AND branch_id=$b AND principal_id=$p
                      AND permission='Undo';
                    """, ("$f", family.ToString("D")), ("$b", branch.ToString("D")),
                    ("$p", viewer.ToString("D"))) == 0)) return null;
            return loaded.Proposal.Provenance;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or
            FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    private bool HasVisibleEndpoints(SqliteConnection con, SqliteTransaction tx, Guid family,
        Guid branch, Guid principal, Guid from, Guid to)
    {
        if (from == to) return false;
        if (Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM sqlite_master WHERE type='table'
            AND name IN ('person_visibility','person_display_name_visibility');
            """) != 2) return false;
        using var cmd = Cmd(con, tx, """
            SELECT person.id,person.encrypted_name,person.living FROM people person
            JOIN person_visibility v ON v.family_id=person.family_id AND v.person_id=person.id
            JOIN person_display_name_visibility d ON d.family_id=v.family_id AND d.branch_id=v.branch_id
              AND d.principal_id=v.principal_id AND d.person_id=v.person_id
            WHERE person.family_id=$f AND v.branch_id=$b AND v.principal_id=$p AND person.id IN ($from,$to);
            """, ("$f", family.ToString("D")), ("$b", branch.ToString("D")), ("$p", principal.ToString("D")),
            ("$from", from.ToString("D")), ("$to", to.ToString("D")));
        using var reader = cmd.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            var id = Guid.ParseExact(reader.GetString(0), "D");
            _ = Decrypt(family, id, reader.GetFieldValue<byte[]>(1));
            if (reader.GetInt64(2) is not (0 or 1)) return false;
            count++;
        }
        return count == 2;
    }

    private static bool IsActiveVisibilityMember(SqliteConnection con, SqliteTransaction tx, Guid family, Guid principal) =>
        Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM memberships WHERE family_id=$f AND principal_id=$p AND state='Active';
            """, ("$f", family.ToString("D")), ("$p", principal.ToString("D"))) == 1;

    private static byte[] VisibilityContext(Guid branch, Guid principal) =>
        [..Encoding.UTF8.GetBytes("familytree.relationship-visibility.v1"), ..branch.ToByteArray(), ..principal.ToByteArray()];

    private static void AuditVisibility(SqliteConnection con, SqliteTransaction tx, Guid family, Guid branch,
        Guid administrator, Guid recipient, Guid relationship, string action) =>
        Exec(con, tx, """
            INSERT INTO scoped_relationship_visibility_audit
            (family_id,branch_id,principal_id,relation_id,actor_id,action,created_utc)
            VALUES($f,$b,$p,$r,$a,$action,$time);
            """, ("$f", family.ToString("D")), ("$b", branch.ToString("D")),
            ("$p", recipient.ToString("D")), ("$r", relationship.ToString("D")),
            ("$a", administrator.ToString("D")), ("$action", action),
            ("$time", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
}

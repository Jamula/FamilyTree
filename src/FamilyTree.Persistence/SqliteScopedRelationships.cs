using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FamilyTree.Domain;
using Microsoft.Data.Sqlite;

namespace FamilyTree.Persistence;

/// <summary>
/// Scoped relationship permissions and quarantined publication are local-only.
/// They do not grant relationship visibility, or enable private routes.
/// Detecting truncation or rollback of authenticated database history requires
/// an external trusted checkpoint; SQLite alone cannot provide that guarantee.
/// </summary>
public sealed partial class SqliteFamilyStore
{
    public enum ScopedRelationshipPermission { Propose, Review, Undo }

    public sealed record ScopedRelationshipProposal(Guid ProposalId, Guid SubmittedBy,
        RelationshipProvenance Provenance);

    private sealed record ScopedLinkPayload(Guid Id, Guid From, Guid To, RelationKind Kind,
        string? Label, AssociationCategory Category, string Citation, DateOnly? Start, DateOnly? End,
        string FromFingerprint, string ToFingerprint);

    private static void EnsureScopedRelationshipSchema(SqliteConnection con, SqliteTransaction tx)
    {
        Exec(con, tx, """
            CREATE TABLE IF NOT EXISTS scoped_relationship_grants (
              family_id TEXT NOT NULL, branch_id TEXT NOT NULL, principal_id TEXT NOT NULL,
              permission TEXT NOT NULL CHECK(permission IN ('Propose','Review','Undo')),
              PRIMARY KEY(family_id,branch_id,principal_id,permission),
              FOREIGN KEY(family_id,principal_id) REFERENCES memberships(family_id,principal_id));
            CREATE TABLE IF NOT EXISTS scoped_relationship_families (
              family_id TEXT PRIMARY KEY NOT NULL REFERENCES families(id));
            CREATE TABLE IF NOT EXISTS scoped_relationship_grant_audit (
              seq INTEGER PRIMARY KEY AUTOINCREMENT, family_id TEXT NOT NULL,
              branch_id TEXT NOT NULL, actor_id TEXT, recipient_id TEXT NOT NULL,
              permission TEXT NOT NULL, action TEXT NOT NULL, created_utc TEXT NOT NULL);
            CREATE TRIGGER IF NOT EXISTS scoped_relationship_membership_change
            AFTER UPDATE OF state ON memberships WHEN OLD.state<>NEW.state BEGIN
              INSERT INTO scoped_relationship_grant_audit
                (family_id,branch_id,actor_id,recipient_id,permission,action,created_utc)
              SELECT family_id,branch_id,NULL,principal_id,permission,'membership-changed',
                strftime('%Y-%m-%dT%H:%M:%fZ','now') FROM scoped_relationship_grants
                WHERE family_id=OLD.family_id AND principal_id=OLD.principal_id;
              DELETE FROM scoped_relationship_grants
                WHERE family_id=OLD.family_id AND principal_id=OLD.principal_id;
            END;
            CREATE TABLE IF NOT EXISTS scoped_relationship_proposals (
              id TEXT PRIMARY KEY NOT NULL, family_id TEXT NOT NULL, branch_id TEXT NOT NULL,
              relation_id TEXT NOT NULL, from_id TEXT NOT NULL, to_id TEXT NOT NULL,
              author TEXT NOT NULL, payload BLOB NOT NULL,
              state TEXT NOT NULL CHECK(state IN ('Pending','Approved','Rejected','Undone')),
              reviewer TEXT, seal BLOB NOT NULL, identity_envelope BLOB,
              UNIQUE(family_id,relation_id), FOREIGN KEY(family_id) REFERENCES families(id));
            CREATE TABLE IF NOT EXISTS scoped_relationship_publications (
              family_id TEXT NOT NULL, branch_id TEXT NOT NULL, relation_id TEXT NOT NULL,
              proposal_id TEXT NOT NULL UNIQUE, payload BLOB NOT NULL,
              PRIMARY KEY(family_id,relation_id),
              FOREIGN KEY(proposal_id) REFERENCES scoped_relationship_proposals(id));
            CREATE TABLE IF NOT EXISTS scoped_relationship_events (
              seq INTEGER PRIMARY KEY AUTOINCREMENT, proposal_id TEXT NOT NULL,
              seal BLOB NOT NULL, FOREIGN KEY(proposal_id) REFERENCES scoped_relationship_proposals(id));
            CREATE INDEX IF NOT EXISTS scoped_relationship_event_latest
              ON scoped_relationship_events(proposal_id,seq DESC);
            CREATE TRIGGER IF NOT EXISTS scoped_relationship_protect_endpoints
            BEFORE DELETE ON people WHEN EXISTS (
              SELECT 1 FROM scoped_relationship_proposals
              WHERE family_id=OLD.family_id AND state IN ('Pending','Approved')
                AND (from_id=OLD.id OR to_id=OLD.id))
            BEGIN SELECT RAISE(ABORT,'Person has scoped relationships.'); END;
            CREATE TRIGGER IF NOT EXISTS scoped_relationship_protect_endpoint_identity
            BEFORE UPDATE OF family_id,id ON people WHEN EXISTS (
              SELECT 1 FROM scoped_relationship_proposals
              WHERE family_id=OLD.family_id AND state IN ('Pending','Approved')
                AND (from_id=OLD.id OR to_id=OLD.id))
            BEGIN SELECT RAISE(ABORT,'Person has scoped relationships.'); END;
            """);
        if (Scalar<long>(con, tx, """
            SELECT (SELECT COUNT(*) FROM scoped_relationship_grants g
                    WHERE NOT EXISTS (SELECT 1 FROM scoped_relationship_families f WHERE f.family_id=g.family_id)) +
                   (SELECT COUNT(*) FROM scoped_relationship_grant_audit a
                    WHERE a.action='granted' AND NOT EXISTS
                      (SELECT 1 FROM scoped_relationship_families f WHERE f.family_id=a.family_id));
            """) != 0)
            throw new InvalidDataException("Scoped relationship mode requires explicit migration.");
        using var cmd = Cmd(con, tx, "PRAGMA table_info(scoped_relationship_proposals);");
        using var reader = cmd.ExecuteReader();
        var hasIdentity = false;
        while (reader.Read()) hasIdentity |= reader.GetString(1) == "identity_envelope";
        reader.Close();
        if (!hasIdentity)
            Exec(con, tx, "ALTER TABLE scoped_relationship_proposals ADD COLUMN identity_envelope BLOB;");
    }

    public void GrantScopedRelationshipPermission(Guid familyId, Guid branchId, Guid administratorId,
        Guid recipientId, ScopedRelationshipPermission permission) =>
        ChangeScopedRelationshipPermission(familyId, branchId, administratorId, recipientId, permission, true);

    public void RevokeScopedRelationshipPermission(Guid familyId, Guid branchId, Guid administratorId,
        Guid recipientId, ScopedRelationshipPermission permission) =>
        ChangeScopedRelationshipPermission(familyId, branchId, administratorId, recipientId, permission, false);

    private void ChangeScopedRelationshipPermission(Guid family, Guid branch, Guid administrator,
        Guid recipient, ScopedRelationshipPermission permission, bool grant)
    {
        RequireIds(family, branch, administrator, recipient);
        if (!Enum.IsDefined(permission)) throw new ArgumentOutOfRangeException(nameof(permission));
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: false);
        EnsureScopedRelationshipSchema(con, tx);
        RequireMember(con, tx, family, administrator, true);
        if (grant) RequireMember(con, tx, family, recipient, false);
        if (grant && Scalar<long>(con, tx,
            "SELECT COUNT(*) FROM scoped_relationship_families WHERE family_id=$f;",
            ("$f", family.ToString("D"))) == 0 &&
            Scalar<long>(con, tx, """
                SELECT (SELECT COUNT(*) FROM relationships WHERE family_id=$f) +
                       (SELECT COUNT(*) FROM relationship_proposals WHERE family_id=$f AND state='Pending');
                """, ("$f", family.ToString("D"))) != 0)
            throw new InvalidOperationException("Resolve legacy relationships before enabling scoped mode.");
        if (grant)
            Exec(con, tx, "INSERT OR IGNORE INTO scoped_relationship_families(family_id) VALUES($f);",
                ("$f", family.ToString("D")));
        var args = new[] { ("$f", (object)family.ToString("D")), ("$b", (object)branch.ToString("D")),
            ("$p", (object)recipient.ToString("D")), ("$permission", (object)permission.ToString()) };
        var changed = Exec(con, tx, grant
            ? "INSERT OR IGNORE INTO scoped_relationship_grants VALUES($f,$b,$p,$permission);"
            : "DELETE FROM scoped_relationship_grants WHERE family_id=$f AND branch_id=$b AND principal_id=$p AND permission=$permission;", args);
        if (changed == 1)
            Exec(con, tx, """
                INSERT INTO scoped_relationship_grant_audit
                (family_id,branch_id,actor_id,recipient_id,permission,action,created_utc)
                VALUES($f,$b,$a,$p,$permission,$action,$time);
                """, ("$f", family.ToString("D")), ("$b", branch.ToString("D")),
                ("$a", administrator.ToString("D")), ("$p", recipient.ToString("D")),
                ("$permission", permission.ToString()), ("$action", grant ? "granted" : "revoked"),
                ("$time", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        tx.Commit();
    }

    private static void RequireScopedLinkAccess(SqliteConnection con, SqliteTransaction tx,
        Guid family, Guid branch, Guid principal, ScopedRelationshipPermission permission,
        Guid from, Guid to)
    {
        RequireMember(con, tx, family, principal, false);
        if (Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM scoped_relationship_grants WHERE family_id=$f AND branch_id=$b
            AND principal_id=$p AND permission=$permission;
            """, ("$f", family.ToString("D")), ("$b", branch.ToString("D")),
            ("$p", principal.ToString("D")), ("$permission", permission.ToString())) != 1)
            throw new UnauthorizedAccessException("Access denied.");
        if (Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM sqlite_master WHERE type='table'
              AND name IN ('person_visibility','person_display_name_visibility');
            """) != 2)
            throw new UnauthorizedAccessException("Access denied.");
        if (Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM people AS person
            JOIN person_visibility v ON v.family_id=person.family_id AND v.person_id=person.id
            JOIN person_display_name_visibility d ON d.family_id=v.family_id AND d.branch_id=v.branch_id
              AND d.principal_id=v.principal_id AND d.person_id=v.person_id
            WHERE person.family_id=$f AND v.branch_id=$b AND v.principal_id=$p
              AND person.id IN ($from,$to);
            """, ("$f", family.ToString("D")), ("$b", branch.ToString("D")),
            ("$p", principal.ToString("D")), ("$from", from.ToString("D")),
            ("$to", to.ToString("D"))) != 2)
            throw new UnauthorizedAccessException("Access denied.");
    }

    private static void RequireVisibleScopedLink(SqliteConnection con, SqliteTransaction tx,
        Guid family, Guid branch, Guid principal, Guid proposal, ScopedRelationshipPermission permission)
    {
        RequireMember(con, tx, family, principal, false);
        if (Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM scoped_relationship_grants WHERE family_id=$f
              AND branch_id=$b AND principal_id=$p AND permission=$permission;
            """, ("$f", family.ToString("D")), ("$b", branch.ToString("D")),
            ("$p", principal.ToString("D")), ("$permission", permission.ToString())) != 1 ||
            Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM sqlite_master WHERE type='table'
              AND name IN ('person_visibility','person_display_name_visibility');
            """) != 2)
            throw new UnauthorizedAccessException("Access denied.");
        if (Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM scoped_relationship_proposals s
            WHERE s.id=$id AND s.family_id=$f AND s.branch_id=$b
              AND 2=(SELECT COUNT(*) FROM people person
                JOIN person_visibility v ON v.family_id=person.family_id AND v.person_id=person.id
                JOIN person_display_name_visibility d ON d.family_id=v.family_id AND d.branch_id=v.branch_id
                  AND d.principal_id=v.principal_id AND d.person_id=v.person_id
                WHERE person.family_id=s.family_id AND v.branch_id=s.branch_id AND v.principal_id=$p
                  AND person.id IN (s.from_id,s.to_id));
            """, ("$id", proposal.ToString("D")), ("$f", family.ToString("D")),
            ("$b", branch.ToString("D")), ("$p", principal.ToString("D"))) != 1)
            throw new UnauthorizedAccessException("Access denied.");
    }

    public Guid ProposeScopedRelationship(Guid familyId, Guid branchId, Guid authorId,
        Guid fromPersonId, Guid toPersonId, RelationKind kind, AssociationCategory category,
        string citation, string? label = null, DateOnly? start = null, DateOnly? end = null)
    {
        RequireIds(familyId, branchId, authorId, fromPersonId, toPersonId);
        if (label is { Length: > 200 } || (label is not null && label.Any(char.IsControl)))
            throw new ArgumentException("Invalid relationship label.");
        var edge = new Relationship(Guid.NewGuid(), fromPersonId, toPersonId, kind, label);
        var provenance = RelationshipProvenance.Create(familyId, branchId, edge, category, citation, start, end);
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: false);
        EnsureScopedRelationshipSchema(con, tx);
        RequireScopedLinkAccess(con, tx, familyId, branchId, authorId,
            ScopedRelationshipPermission.Propose, fromPersonId, toPersonId);
        var proposal = Guid.NewGuid();
        var payload = Encrypt(familyId, edge.Id, JsonSerializer.Serialize(new ScopedLinkPayload(
            edge.Id, edge.FromPersonId, edge.ToPersonId, edge.Kind, edge.Label, category,
            provenance.Citation, start, end, PersonFingerprint(con, tx, familyId, fromPersonId),
            PersonFingerprint(con, tx, familyId, toPersonId))),
            LinkContext(proposal, branchId, authorId, fromPersonId, toPersonId));
        var seal = SealLink(familyId, edge.Id, proposal, branchId, authorId, fromPersonId, toPersonId,
            payload, "Pending", null, 1, null);
        Exec(con, tx, """
            INSERT INTO scoped_relationship_proposals
              (id,family_id,branch_id,relation_id,from_id,to_id,author,payload,state,seal,identity_envelope)
            VALUES($id,$f,$b,$r,$from,$to,$author,$payload,'Pending',$seal,$identity);
            """, ("$id", proposal.ToString("D")), ("$f", familyId.ToString("D")),
            ("$b", branchId.ToString("D")), ("$r", edge.Id.ToString("D")),
            ("$from", fromPersonId.ToString("D")), ("$to", toPersonId.ToString("D")),
            ("$author", authorId.ToString("D")), ("$payload", payload), ("$seal", seal),
            ("$identity", Encrypt(familyId, edge.Id, "scoped-relationship-identity-v1",
                LinkContext(proposal, branchId, authorId, fromPersonId, toPersonId))));
        LinkEvent(con, tx, familyId, proposal, authorId, seal, "scoped-relationship-proposed");
        tx.Commit();
        return proposal;
    }

    private sealed record LoadedLink(ScopedRelationshipProposal Proposal, byte[] Payload, byte[] Seal,
        string FromFingerprint, string ToFingerprint, int Version);

    private static string PersonFingerprint(SqliteConnection con, SqliteTransaction tx, Guid family, Guid person)
    {
        using var cmd = Cmd(con, tx, "SELECT encrypted_name,living FROM people WHERE family_id=$f AND id=$p;",
            ("$f", family.ToString("D")), ("$p", person.ToString("D")));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new UnauthorizedAccessException("Access denied.");
        return Convert.ToBase64String(SHA256.HashData(reader.GetFieldValue<byte[]>(0))) + "|" + reader.GetInt64(1);
    }

    private LoadedLink LoadScopedLink(SqliteConnection con, SqliteTransaction tx,
        Guid family, Guid branch, Guid proposal, string expectedState)
    {
        using var cmd = Cmd(con, tx, """
            SELECT relation_id,from_id,to_id,author,payload,state,reviewer,seal
            FROM scoped_relationship_proposals WHERE id=$id AND family_id=$f AND branch_id=$b;
            """, ("$id", proposal.ToString("D")), ("$f", family.ToString("D")), ("$b", branch.ToString("D")));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Proposal unavailable.");
        if (!Guid.TryParseExact(reader.GetString(0), "D", out var id) || id == Guid.Empty ||
            !Guid.TryParseExact(reader.GetString(1), "D", out var from) || from == Guid.Empty ||
            !Guid.TryParseExact(reader.GetString(2), "D", out var to) || to == Guid.Empty || from == to ||
            !Guid.TryParseExact(reader.GetString(3), "D", out var author) || author == Guid.Empty)
            throw new InvalidDataException("Proposal invalid.");
        var payload = reader.GetFieldValue<byte[]>(4);
        var state = reader.GetString(5);
        var reviewer = reader.IsDBNull(6) ? null : reader.GetString(6);
        var seal = reader.GetFieldValue<byte[]>(7);
        reader.Close();
        var context = LinkContext(proposal, branch, author, from, to);
        var version = 0;
        byte[]? previous = null;
        string? previousState = null;
        using (var eventCmd = Cmd(con, tx, """
            SELECT seal FROM scoped_relationship_events WHERE proposal_id=$id ORDER BY seq;
            """, ("$id", proposal.ToString("D"))))
        using (var events = eventCmd.ExecuteReader())
        {
            while (events.Read())
            {
                var current = events.GetFieldValue<byte[]>(0);
                var parts = Decrypt(family, id, current, context).Split('|');
                version++;
                if (version > 3 || parts.Length != 5 ||
                    parts[2] != Convert.ToBase64String(SHA256.HashData(payload)) ||
                    parts[3] != version.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                    parts[4] != (previous is null ? "" : Convert.ToBase64String(SHA256.HashData(previous))) ||
                    (version == 1 && (parts[0] != "Pending" || parts[1] != "")) ||
                    (version == 2 && (parts[0] is not ("Approved" or "Rejected") ||
                        !Guid.TryParseExact(parts[1], "D", out var r) || r == Guid.Empty || r == author)) ||
                    (version == 3 && (previousState != "Approved" || parts[0] != "Undone" ||
                        !Guid.TryParseExact(parts[1], "D", out var r2) || r2 == Guid.Empty || r2 == author)))
                    throw new InvalidDataException("Proposal history invalid.");
                previous = current;
                previousState = parts[0];
                if (parts[0] == state && CryptographicOperations.FixedTimeEquals(current, seal) &&
                    parts[1] != (reviewer ?? ""))
                    throw new InvalidDataException("Proposal state invalid.");
            }
        }
        if (previous is null || !CryptographicOperations.FixedTimeEquals(previous, seal) ||
            previousState != state ||
            Decrypt(family, id, seal, context).Split('|')[1] != (reviewer ?? ""))
            throw new InvalidDataException("Proposal state invalid.");
        if (state != expectedState) throw new InvalidOperationException("Proposal unavailable.");
        ScopedLinkPayload data;
        try
        {
            data = JsonSerializer.Deserialize<ScopedLinkPayload>(Decrypt(family, id, payload,
                LinkContext(proposal, branch, author, from, to))) ?? throw new InvalidDataException("Proposal invalid.");
            if (data.Id != id || data.From != from || data.To != to ||
                data.Label is { Length: > 200 } || (data.Label is not null && data.Label.Any(char.IsControl)))
                throw new InvalidDataException("Proposal invalid.");
            var provenance = RelationshipProvenance.Create(family, branch,
                new Relationship(id, from, to, data.Kind, data.Label), data.Category,
                data.Citation, data.Start, data.End);
            return new(new(proposal, author, provenance), payload, seal, data.FromFingerprint, data.ToFingerprint, version);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidDataException("Proposal invalid.", ex);
        }
    }

    public void ReviewScopedRelationship(Guid familyId, Guid branchId, Guid reviewerId,
        Guid proposalId, bool approve)
    {
        RequireIds(familyId, branchId, reviewerId, proposalId);
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: false);
        EnsureScopedRelationshipSchema(con, tx);
        RequireVisibleScopedLink(con, tx, familyId, branchId, reviewerId, proposalId,
            ScopedRelationshipPermission.Review);
        var loaded = LoadScopedLink(con, tx, familyId, branchId, proposalId, "Pending");
        var edge = loaded.Proposal.Provenance.Link;
        if (loaded.Proposal.SubmittedBy == reviewerId) throw new UnauthorizedAccessException("Access denied.");
        RequireScopedLinkAccess(con, tx, familyId, branchId, reviewerId, ScopedRelationshipPermission.Review, edge.FromPersonId, edge.ToPersonId);
        if (approve)
            RequireScopedLinkAccess(con, tx, familyId, branchId, loaded.Proposal.SubmittedBy, ScopedRelationshipPermission.Propose, edge.FromPersonId, edge.ToPersonId);
        if (approve && (loaded.FromFingerprint != PersonFingerprint(con, tx, familyId, edge.FromPersonId) ||
            loaded.ToFingerprint != PersonFingerprint(con, tx, familyId, edge.ToPersonId)))
            throw new InvalidDataException("Relationship endpoints changed.");
        if (approve)
            Exec(con, tx, """
                INSERT INTO scoped_relationship_publications VALUES($f,$b,$r,$id,$payload);
                """, ("$f", familyId.ToString("D")), ("$b", branchId.ToString("D")),
                ("$r", edge.Id.ToString("D")), ("$id", proposalId.ToString("D")),
                ("$payload", loaded.Payload));
        TransitionScopedLink(con, tx, familyId, branchId, reviewerId, loaded,
            approve ? "Approved" : "Rejected");
        tx.Commit();
    }

    /// <summary>
    /// Rejects a corrupted pending payload without exposing its contents.
    /// Identity, scope and the original pending state must still authenticate.
    /// </summary>
    public void QuarantineScopedRelationship(Guid familyId, Guid branchId, Guid reviewerId, Guid proposalId)
    {
        RequireIds(familyId, branchId, reviewerId, proposalId);
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: false);
        RequireVisibleScopedLink(con, tx, familyId, branchId, reviewerId, proposalId,
            ScopedRelationshipPermission.Review);
        var (id, seal, context, payloadHash, _) =
            ReadCorruptedScopedLink(con, tx, familyId, branchId, reviewerId, proposalId);
        var rejectedSeal = Encrypt(familyId, id,
            "Rejected|" + reviewerId.ToString("D") + "|" + payloadHash + "|2|" +
            Convert.ToBase64String(SHA256.HashData(seal)), context);
        if (Exec(con, tx, """
            UPDATE scoped_relationship_proposals SET state='Rejected',reviewer=$reviewer,seal=$new
            WHERE id=$id AND family_id=$f AND branch_id=$b AND state='Pending' AND seal=$old;
            """, ("$reviewer", reviewerId.ToString("D")), ("$new", rejectedSeal),
            ("$id", proposalId.ToString("D")), ("$f", familyId.ToString("D")),
            ("$b", branchId.ToString("D")), ("$old", seal)) != 1)
            throw new InvalidOperationException("Proposal unavailable.");
        LinkEvent(con, tx, familyId, proposalId, reviewerId, rejectedSeal, "scoped-relationship-quarantined");
        tx.Commit();
    }

    private (Guid Id, byte[] Seal, byte[] Context, string PayloadHash, Guid Author) ReadCorruptedScopedLink(
        SqliteConnection con, SqliteTransaction tx, Guid familyId, Guid branchId, Guid reviewerId, Guid proposalId)
    {
        Guid id, from, to, author;
        byte[] payload, seal, identity;
        using (var cmd = Cmd(con, tx, """
            SELECT relation_id,from_id,to_id,author,payload,seal,identity_envelope
            FROM scoped_relationship_proposals WHERE id=$id AND family_id=$f AND branch_id=$b
              AND state='Pending' AND reviewer IS NULL;
            """, ("$id", proposalId.ToString("D")), ("$f", familyId.ToString("D")),
            ("$b", branchId.ToString("D"))))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read() ||
                !Guid.TryParseExact(reader.GetString(0), "D", out id) || id == Guid.Empty ||
                !Guid.TryParseExact(reader.GetString(1), "D", out from) || from == Guid.Empty ||
                !Guid.TryParseExact(reader.GetString(2), "D", out to) || to == Guid.Empty || from == to ||
                !Guid.TryParseExact(reader.GetString(3), "D", out author) || author == Guid.Empty ||
                reader.IsDBNull(6))
                throw new InvalidDataException("Proposal identity unavailable.");
            payload = reader.GetFieldValue<byte[]>(4);
            seal = reader.GetFieldValue<byte[]>(5);
            identity = reader.GetFieldValue<byte[]>(6);
        }
        var context = LinkContext(proposalId, branchId, author, from, to);
        if (Decrypt(familyId, id, identity, context) != "scoped-relationship-identity-v1")
            throw new InvalidDataException("Proposal identity invalid.");
        if (author == reviewerId) throw new UnauthorizedAccessException("Access denied.");
        var parts = Decrypt(familyId, id, seal, context).Split('|');
        if (parts.Length != 5 || parts[0] != "Pending" || parts[1] != "" ||
            parts[3] != "1" || parts[4] != "" ||
            parts[2] == Convert.ToBase64String(SHA256.HashData(payload)))
            throw new InvalidDataException("Corrupted pending content required.");
        using (var cmd = Cmd(con, tx, """
            SELECT seal FROM scoped_relationship_events WHERE proposal_id=$id ORDER BY seq;
            """, ("$id", proposalId.ToString("D"))))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read() || !CryptographicOperations.FixedTimeEquals(reader.GetFieldValue<byte[]>(0), seal) ||
                reader.Read())
                throw new InvalidDataException("Proposal history invalid.");
        }
        return (id, seal, context, parts[2], author);
    }

    public sealed record ScopedRelationshipQuarantinePage(IReadOnlyList<Guid> ProposalIds, Guid? NextCursor);

    public ScopedRelationshipQuarantinePage ListScopedRelationshipQuarantineCandidates(
        Guid familyId, Guid branchId, Guid reviewerId, Guid? afterProposalId = null)
    {
        RequireIds(familyId, branchId, reviewerId);
        if (afterProposalId == Guid.Empty) throw new ArgumentException("Invalid cursor.");
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: false);
        RequireMember(con, tx, familyId, reviewerId, false);
        if (Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM scoped_relationship_grants WHERE family_id=$f AND branch_id=$b
            AND principal_id=$p AND permission='Review';
            """, ("$f", familyId.ToString("D")), ("$b", branchId.ToString("D")),
            ("$p", reviewerId.ToString("D"))) != 1 ||
            Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM sqlite_master WHERE type='table'
            AND name IN ('person_visibility','person_display_name_visibility');
            """) != 2)
            throw new UnauthorizedAccessException("Access denied.");
        var scanned = new List<Guid>();
        using (var cmd = Cmd(con, tx, """
            SELECT s.id FROM scoped_relationship_proposals s
            WHERE s.family_id=$f AND s.branch_id=$b AND s.state='Pending' AND s.author<>$p
              AND length(s.id)=36 AND length(replace(s.id,'-',''))=32
              AND substr(s.id,9,1)='-' AND substr(s.id,14,1)='-'
              AND substr(s.id,19,1)='-' AND substr(s.id,24,1)='-'
              AND s.id NOT GLOB '*[^0-9a-f-]*'
              AND s.id<>'00000000-0000-0000-0000-000000000000'
              AND ($after IS NULL OR s.id>$after)
              AND 2=(SELECT COUNT(*) FROM people person
                JOIN person_visibility v ON v.family_id=person.family_id AND v.person_id=person.id
                JOIN person_display_name_visibility d ON d.family_id=v.family_id AND d.branch_id=v.branch_id
                  AND d.principal_id=v.principal_id AND d.person_id=v.person_id
                WHERE person.family_id=s.family_id AND v.branch_id=s.branch_id AND v.principal_id=$p
                  AND person.id IN (s.from_id,s.to_id))
            ORDER BY s.id LIMIT 100;
            """, ("$f", familyId.ToString("D")), ("$b", branchId.ToString("D")),
            ("$p", reviewerId.ToString("D")), ("$after", (object?)afterProposalId?.ToString("D") ?? DBNull.Value)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                if (Guid.TryParseExact(reader.GetString(0), "D", out var id) && id != Guid.Empty)
                    scanned.Add(id);
        var candidates = new List<Guid>();
        foreach (var id in scanned)
        {
            try
            {
                _ = ReadCorruptedScopedLink(con, tx, familyId, branchId, reviewerId, id);
                candidates.Add(id);
            }
            catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException)
            {
                // Unauthenticated identities and valid payloads are not candidates.
            }
        }
        tx.Commit();
        return new(candidates, scanned.Count == 100 ? scanned[^1] : null);
    }

    public sealed record ScopedRelationshipReview(Guid ProposalId, Guid SubmittedBy,
        RelationshipProvenance? Provenance, bool ContentUnreadable);

    public sealed record ScopedRelationshipReviewPage(
        IReadOnlyList<ScopedRelationshipReview> Entries, Guid? NextCursor);

    public ScopedRelationshipReviewPage ListScopedPendingRelationships(
        Guid familyId, Guid branchId, Guid reviewerId, Guid? afterProposalId = null) =>
        ListScopedRelationshipsForAction(familyId, branchId, reviewerId, afterProposalId, false);

    public ScopedRelationshipReviewPage ListScopedRelationshipsForUndo(
        Guid familyId, Guid branchId, Guid reviewerId, Guid? afterProposalId = null) =>
        ListScopedRelationshipsForAction(familyId, branchId, reviewerId, afterProposalId, true);

    private ScopedRelationshipReviewPage ListScopedRelationshipsForAction(
        Guid familyId, Guid branchId, Guid reviewerId, Guid? afterProposalId, bool undo)
    {
        RequireIds(familyId, branchId, reviewerId);
        if (afterProposalId == Guid.Empty) throw new ArgumentException("Invalid cursor.");
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: false);
        EnsureScopedRelationshipSchema(con, tx);
        RequireMember(con, tx, familyId, reviewerId, false);
        if (Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM scoped_relationship_grants WHERE family_id=$f AND branch_id=$b
            AND principal_id=$p AND permission=$permission;
            """, ("$f", familyId.ToString("D")), ("$b", branchId.ToString("D")),
            ("$p", reviewerId.ToString("D")), ("$permission", undo ? "Undo" : "Review")) != 1)
            throw new UnauthorizedAccessException("Access denied.");
        if (Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM sqlite_master WHERE type='table'
            AND name IN ('person_visibility','person_display_name_visibility');
            """) != 2)
            throw new UnauthorizedAccessException("Access denied.");
        var ids = new List<Guid>();
        using (var cmd = Cmd(con, tx, """
            SELECT s.id FROM scoped_relationship_proposals s
            WHERE s.family_id=$f AND s.branch_id=$b AND s.state=$state AND s.author<>$p
              AND length(s.id)=36 AND length(replace(s.id,'-',''))=32
              AND substr(s.id,9,1)='-' AND substr(s.id,14,1)='-'
              AND substr(s.id,19,1)='-' AND substr(s.id,24,1)='-'
              AND s.id NOT GLOB '*[^0-9a-f-]*'
              AND s.id<>'00000000-0000-0000-0000-000000000000'
              AND ($after IS NULL OR s.id>$after)
              AND 2=(SELECT COUNT(*) FROM people person
                JOIN person_visibility v ON v.family_id=person.family_id AND v.person_id=person.id
                JOIN person_display_name_visibility d ON d.family_id=v.family_id AND d.branch_id=v.branch_id
                  AND d.principal_id=v.principal_id AND d.person_id=v.person_id
                WHERE person.family_id=s.family_id AND v.branch_id=s.branch_id AND v.principal_id=$p
                  AND person.id IN (s.from_id,s.to_id))
            ORDER BY s.id LIMIT 100;
            """, ("$f", familyId.ToString("D")), ("$b", branchId.ToString("D")),
            ("$p", reviewerId.ToString("D")), ("$state", undo ? "Approved" : "Pending"),
            ("$after", (object?)afterProposalId?.ToString("D") ?? DBNull.Value)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                if (Guid.TryParseExact(reader.GetString(0), "D", out var id) && id != Guid.Empty)
                    ids.Add(id);
        var result = new List<ScopedRelationshipReview>();
        foreach (var id in ids)
        {
            try
            {
                var loaded = LoadScopedLink(con, tx, familyId, branchId, id, undo ? "Approved" : "Pending");
                var proposal = loaded.Proposal;
                if (proposal.SubmittedBy == reviewerId) continue;
                if (undo)
                {
                    using var publication = Cmd(con, tx, """
                        SELECT payload FROM scoped_relationship_publications WHERE proposal_id=$id
                        AND family_id=$f AND branch_id=$b AND relation_id=$r;
                        """, ("$id", id.ToString("D")), ("$f", familyId.ToString("D")),
                        ("$b", branchId.ToString("D")), ("$r", proposal.Provenance.Link.Id.ToString("D")));
                    if (publication.ExecuteScalar() is not byte[] published ||
                        !CryptographicOperations.FixedTimeEquals(published, loaded.Payload))
                        continue;
                }
                result.Add(new(proposal.ProposalId, proposal.SubmittedBy, proposal.Provenance, false));
            }
            catch (InvalidDataException)
            {
                if (undo) continue;
                try
                {
                    var corrupted = ReadCorruptedScopedLink(con, tx, familyId, branchId, reviewerId, id);
                    result.Add(new(id, corrupted.Author, null, true));
                }
                catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException)
                {
                    // Never expose an unauthenticated identity.
                }
            }
        }
        tx.Commit();
        return new(result, ids.Count == 100 ? ids[^1] : null);
    }

    public void UndoScopedRelationship(Guid familyId, Guid branchId, Guid reviewerId, Guid proposalId)
    {
        RequireIds(familyId, branchId, reviewerId, proposalId);
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: false);
        EnsureScopedRelationshipSchema(con, tx);
        RequireVisibleScopedLink(con, tx, familyId, branchId, reviewerId, proposalId,
            ScopedRelationshipPermission.Undo);
        var loaded = LoadScopedLink(con, tx, familyId, branchId, proposalId, "Approved");
        var edge = loaded.Proposal.Provenance.Link;
        if (loaded.Proposal.SubmittedBy == reviewerId) throw new UnauthorizedAccessException("Access denied.");
        RequireScopedLinkAccess(con, tx, familyId, branchId, reviewerId, ScopedRelationshipPermission.Undo, edge.FromPersonId, edge.ToPersonId);
        using (var cmd = Cmd(con, tx, """
            SELECT payload FROM scoped_relationship_publications
            WHERE family_id=$f AND branch_id=$b AND relation_id=$r AND proposal_id=$id;
            """, ("$f", familyId.ToString("D")), ("$b", branchId.ToString("D")),
            ("$r", edge.Id.ToString("D")), ("$id", proposalId.ToString("D"))))
            if (cmd.ExecuteScalar() is not byte[] published ||
                !CryptographicOperations.FixedTimeEquals(published, loaded.Payload))
                throw new InvalidDataException("Published relationship invalid.");
        if (Exec(con, tx, "DELETE FROM scoped_relationship_publications WHERE proposal_id=$id;",
            ("$id", proposalId.ToString("D"))) != 1)
            throw new InvalidOperationException("Published relationship unavailable.");
        TransitionScopedLink(con, tx, familyId, branchId, reviewerId, loaded, "Undone");
        tx.Commit();
    }

    private void TransitionScopedLink(SqliteConnection con, SqliteTransaction tx, Guid family,
        Guid branch, Guid reviewer, LoadedLink loaded, string state)
    {
        var edge = loaded.Proposal.Provenance.Link;
        var id = loaded.Proposal.ProposalId;
        var seal = SealLink(family, edge.Id, id, branch, loaded.Proposal.SubmittedBy,
            edge.FromPersonId, edge.ToPersonId, loaded.Payload, state, reviewer, loaded.Version + 1, loaded.Seal);
        if (Exec(con, tx, """
            UPDATE scoped_relationship_proposals SET state=$state,reviewer=$reviewer,seal=$seal
            WHERE id=$id AND family_id=$f AND branch_id=$b AND seal=$old;
            """, ("$state", state), ("$reviewer", reviewer.ToString("D")), ("$seal", seal),
            ("$id", id.ToString("D")), ("$f", family.ToString("D")),
            ("$b", branch.ToString("D")), ("$old", loaded.Seal)) != 1)
            throw new InvalidOperationException("Proposal unavailable.");
        LinkEvent(con, tx, family, id, reviewer, seal, "scoped-relationship-" + state.ToLowerInvariant());
    }

    private static void LinkEvent(SqliteConnection con, SqliteTransaction tx, Guid family,
        Guid proposal, Guid actor, byte[] seal, string action)
    {
        Exec(con, tx, "INSERT INTO scoped_relationship_events(proposal_id,seal) VALUES($id,$seal);",
            ("$id", proposal.ToString("D")), ("$seal", seal));
        Audit(con, tx, family, proposal, actor, action);
    }

    private byte[] SealLink(Guid family, Guid id, Guid proposal, Guid branch, Guid author,
        Guid from, Guid to, byte[] payload, string state, Guid? reviewer, int version, byte[]? previous) =>
        Encrypt(family, id, LinkStateContent(payload, state, reviewer?.ToString("D"), version, previous),
            LinkContext(proposal, branch, author, from, to));

    private static string LinkStateContent(byte[] payload, string state, string? reviewer, int version, byte[]? previous) =>
        state + "|" + (reviewer ?? "") + "|" + Convert.ToBase64String(SHA256.HashData(payload)) + "|" +
        version.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
        (previous is null ? "" : Convert.ToBase64String(SHA256.HashData(previous)));

    private static byte[] LinkContext(Guid proposal, Guid branch, Guid author, Guid from, Guid to) =>
        [..Encoding.UTF8.GetBytes("familytree.scoped-link.v1"), ..proposal.ToByteArray(),
         ..branch.ToByteArray(), ..author.ToByteArray(), ..from.ToByteArray(), ..to.ToByteArray()];
}

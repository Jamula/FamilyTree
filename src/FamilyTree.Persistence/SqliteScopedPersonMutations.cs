using System.Text;
using System.Security.Cryptography;
using FamilyTree.Domain;
using Microsoft.Data.Sqlite;

namespace FamilyTree.Persistence;

/// <summary>
/// Opt-in local mutation slice. Principal IDs must be supplied by a trusted
/// authenticated caller. This does not expose a route or confer graph visibility.
/// Legacy proposals and family-wide mutation methods are not part of this slice.
/// </summary>
public sealed partial class SqliteFamilyStore
{
    public enum ScopedPersonPermission { CreatePerson, ReviewPerson, UndoPerson }

    public sealed record ScopedPendingPerson(Guid ProposalId, Guid BranchId,
        Guid SubmittedBy, Person Person);

    // Installed transactionally before any grant can be issued. The trigger
    // also covers membership transitions performed by other store instances.
    private static void EnsureScopedPersonSchema(SqliteConnection con, SqliteTransaction tx)
    {
        Exec(con, tx, """
            CREATE TABLE IF NOT EXISTS scoped_person_grants (
                family_id TEXT NOT NULL, branch_id TEXT NOT NULL,
                principal_id TEXT NOT NULL, permission TEXT NOT NULL
                    CHECK(permission IN ('CreatePerson','ReviewPerson','UndoPerson')),
                PRIMARY KEY(family_id,branch_id,principal_id,permission),
                FOREIGN KEY(family_id,principal_id)
                    REFERENCES memberships(family_id,principal_id)
            );
            CREATE TRIGGER IF NOT EXISTS scoped_person_grants_membership_change
            AFTER UPDATE OF state ON memberships
            WHEN OLD.state <> NEW.state
            BEGIN
                INSERT INTO scoped_person_grant_audit
                  (family_id,branch_id,actor_id,recipient_id,permission,action,created_utc)
                SELECT family_id,branch_id,NULL,principal_id,permission,'membership-changed',
                       strftime('%Y-%m-%dT%H:%M:%fZ','now')
                FROM scoped_person_grants
                WHERE family_id=OLD.family_id AND principal_id=OLD.principal_id;
                DELETE FROM scoped_person_grants
                WHERE family_id=OLD.family_id AND principal_id=OLD.principal_id;
            END;
            CREATE TABLE IF NOT EXISTS scoped_person_proposals (
                id TEXT PRIMARY KEY NOT NULL, family_id TEXT NOT NULL,
                branch_id TEXT NOT NULL, person_id TEXT NOT NULL,
                submitter TEXT NOT NULL, living INTEGER NOT NULL CHECK(living IN (0,1)),
                payload BLOB NOT NULL, state_envelope BLOB NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('Pending','Approved','Rejected','Undone')),
                reviewer TEXT NULL,
                FOREIGN KEY(family_id) REFERENCES families(id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS scoped_person_pending_id
                ON scoped_person_proposals(family_id,person_id) WHERE state='Pending';
            CREATE INDEX IF NOT EXISTS scoped_person_family_person
                ON scoped_person_proposals(family_id,person_id);
            CREATE TABLE IF NOT EXISTS scoped_person_events (
                seq INTEGER PRIMARY KEY AUTOINCREMENT, proposal_id TEXT NOT NULL,
                envelope BLOB NOT NULL
            );
            CREATE INDEX IF NOT EXISTS scoped_person_events_proposal
                ON scoped_person_events(proposal_id,seq);
            CREATE TRIGGER IF NOT EXISTS scoped_person_legacy_pending_conflict
            BEFORE INSERT ON person_proposals
            WHEN NEW.state='Pending' AND EXISTS (
                SELECT 1 FROM scoped_person_proposals
                WHERE family_id=NEW.family_id AND person_id=NEW.person_id AND state='Pending')
            BEGIN
                SELECT RAISE(ABORT,'Pending person ID unavailable.');
            END;
            CREATE TABLE IF NOT EXISTS scoped_person_grant_audit (
                seq INTEGER PRIMARY KEY AUTOINCREMENT, family_id TEXT NOT NULL,
                branch_id TEXT NOT NULL, actor_id TEXT NULL, recipient_id TEXT NOT NULL,
                permission TEXT NOT NULL, action TEXT NOT NULL, created_utc TEXT NOT NULL
            );
            """);
    }

    public void GrantScopedPersonPermission(Guid familyId, Guid branchId, Guid administratorId,
        Guid recipientId, ScopedPersonPermission permission)
    {
        RequireScopedPermissionArguments(familyId, branchId, administratorId, recipientId, permission);
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: false);
        EnsureScopedPersonSchema(con, tx);
        RequireMember(con, tx, familyId, administratorId, administrator: true);
        RequireMember(con, tx, familyId, recipientId, administrator: false);
        if (Exec(con, tx, """
            INSERT OR IGNORE INTO scoped_person_grants
            (family_id,branch_id,principal_id,permission) VALUES($f,$b,$p,$permission);
            """, ("$f", familyId.ToString("D")), ("$b", branchId.ToString("D")),
            ("$p", recipientId.ToString("D")), ("$permission", permission.ToString())) == 1)
            AuditScopedGrant(con, tx, familyId, branchId, administratorId, recipientId,
                permission, "granted");
        tx.Commit();
    }

    public void RevokeScopedPersonPermission(Guid familyId, Guid branchId, Guid administratorId,
        Guid recipientId, ScopedPersonPermission permission)
    {
        RequireScopedPermissionArguments(familyId, branchId, administratorId, recipientId, permission);
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: false);
        EnsureScopedPersonSchema(con, tx);
        RequireMember(con, tx, familyId, administratorId, administrator: true);
        if (Exec(con, tx, """
            DELETE FROM scoped_person_grants WHERE family_id=$f AND branch_id=$b
                AND principal_id=$p AND permission=$permission;
            """, ("$f", familyId.ToString("D")), ("$b", branchId.ToString("D")),
            ("$p", recipientId.ToString("D")), ("$permission", permission.ToString())) == 1)
            AuditScopedGrant(con, tx, familyId, branchId, administratorId, recipientId,
                permission, "revoked");
        tx.Commit();
    }

    private static void AuditScopedGrant(SqliteConnection con, SqliteTransaction tx,
        Guid family, Guid branch, Guid actor, Guid recipient,
        ScopedPersonPermission permission, string action)
        => Exec(con, tx, """
            INSERT INTO scoped_person_grant_audit
              (family_id,branch_id,actor_id,recipient_id,permission,action,created_utc)
            VALUES($f,$b,$a,$p,$permission,$action,$created);
            """, ("$f", family.ToString("D")), ("$b", branch.ToString("D")),
            ("$a", actor.ToString("D")), ("$p", recipient.ToString("D")),
            ("$permission", permission.ToString()), ("$action", action),
            ("$created", DateTimeOffset.UtcNow.ToString("O",
                System.Globalization.CultureInfo.InvariantCulture)));

    private static void RequireScopedPermissionArguments(Guid family, Guid branch,
        Guid actor, Guid recipient, ScopedPersonPermission permission)
    {
        RequireIds(family, branch, actor, recipient);
        if (!Enum.IsDefined(permission)) throw new ArgumentOutOfRangeException(nameof(permission));
    }

    private static void RequireScopedPersonPermission(SqliteConnection con, SqliteTransaction tx,
        Guid family, Guid branch, Guid principal, ScopedPersonPermission permission)
    {
        RequireMember(con, tx, family, principal, administrator: false);
        if (Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM scoped_person_grants WHERE family_id=$f
                AND branch_id=$b AND principal_id=$p AND permission=$permission;
            """, ("$f", family.ToString("D")), ("$b", branch.ToString("D")),
            ("$p", principal.ToString("D")), ("$permission", permission.ToString())) != 1)
            throw new UnauthorizedAccessException("Access denied.");
    }

    public Guid ProposeScopedPerson(Guid familyId, Guid branchId, Guid authorId,
        string displayName, bool isLiving = true)
    {
        RequireIds(familyId, branchId, authorId);
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 200 ||
            displayName.Any(char.IsControl))
            throw new ArgumentException("Invalid person display name.");
        var person = new Person(Guid.NewGuid(), displayName, isLiving);
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: false);
        EnsureScopedPersonSchema(con, tx);
        RequireScopedPersonPermission(con, tx, familyId, branchId, authorId,
            ScopedPersonPermission.CreatePerson);
        if (Scalar<long>(con, tx, """
            SELECT (SELECT COUNT(*) FROM people WHERE family_id=$f AND id=$p) +
                   (SELECT COUNT(*) FROM scoped_person_proposals
                    WHERE family_id=$f AND person_id=$p AND state='Pending') +
                   (SELECT COUNT(*) FROM person_proposals
                    WHERE family_id=$f AND person_id=$p AND state='Pending');
            """, ("$f", familyId.ToString("D")), ("$p", person.Id.ToString("D"))) != 0)
            throw new InvalidOperationException("Person ID unavailable.");
        var id = Guid.NewGuid();
        var payload = Encrypt(familyId, person.Id, person.DisplayName,
            ScopedPersonContext(id, branchId, authorId, person.IsLiving));
        var seal = SealScopedState(familyId, branchId, id, authorId, person, "Pending", null, null);
        Exec(con, tx, """
            INSERT INTO scoped_person_proposals
              (id,family_id,branch_id,person_id,submitter,living,payload,state,state_envelope)
              VALUES($id,$f,$b,$p,$author,$living,$payload,'Pending',$seal);
            """, ("$id", id.ToString("D")), ("$f", familyId.ToString("D")),
            ("$b", branchId.ToString("D")), ("$p", person.Id.ToString("D")),
            ("$author", authorId.ToString("D")), ("$living", person.IsLiving ? 1 : 0),
            ("$payload", payload),
            ("$seal", seal));
        PersonEvent(con, tx, familyId, branchId, id, authorId, person, payload, seal, "Pending", null);
        Audit(con, tx, familyId, id, authorId, "scoped-person-proposed");
        tx.Commit();
        return id;
    }

    public IReadOnlyList<ScopedPendingPerson> ListScopedPendingPeople(
        Guid familyId, Guid branchId, Guid reviewerId, Guid? afterProposalId = null)
    {
        var result = new List<ScopedPendingPerson>();
        var cursor = afterProposalId;
        do
        {
            var page = ListScopedPendingPeoplePage(familyId, branchId, reviewerId, cursor);
            result.AddRange(page.Entries.Take(100 - result.Count));
            cursor = page.NextCursor;
        } while (result.Count < 100 && cursor is not null);
        return result;
    }

    public sealed record ScopedPersonReviewPage(
        IReadOnlyList<ScopedPendingPerson> Entries, IReadOnlyList<Guid> QuarantineIds, Guid? NextCursor);

    public ScopedPersonReviewPage ListScopedPendingPeoplePage(
        Guid familyId, Guid branchId, Guid reviewerId, Guid? afterProposalId = null)
    {
        RequireIds(familyId, branchId, reviewerId);
        if (afterProposalId == Guid.Empty) throw new ArgumentException("Invalid cursor.");
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: false);
        EnsureScopedPersonSchema(con, tx);
        RequireScopedPersonPermission(con, tx, familyId, branchId, reviewerId,
            ScopedPersonPermission.ReviewPerson);
        var ids = new List<Guid>();
        using (var cmd = Cmd(con, tx, """
            SELECT id
            FROM scoped_person_proposals
            WHERE family_id=$f AND branch_id=$b AND state='Pending'
                AND submitter<>$reviewer AND ($after IS NULL OR id>$after)
                AND length(id)=36 AND length(replace(id,'-',''))=32
                AND substr(id,9,1)='-' AND substr(id,14,1)='-'
                AND substr(id,19,1)='-' AND substr(id,24,1)='-'
                AND id NOT GLOB '*[^0-9a-f-]*'
                AND id<>'00000000-0000-0000-0000-000000000000'
            ORDER BY id LIMIT 100;
            """, ("$f", familyId.ToString("D")), ("$b", branchId.ToString("D")),
            ("$reviewer", reviewerId.ToString("D")),
            ("$after", (object?)afterProposalId?.ToString("D") ?? DBNull.Value)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                if (Guid.TryParseExact(reader.GetString(0), "D", out var id) && id != Guid.Empty)
                    ids.Add(id);
        var result = new List<ScopedPendingPerson>();
        var quarantines = new List<Guid>();
        foreach (var id in ids)
        {
            try
            {
                var proposal = LoadScopedProposal(con, tx, familyId, branchId, id, "Pending");
                if (proposal.SubmittedBy != reviewerId) result.Add(proposal);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidCastException or FormatException or OverflowException)
            {
                try
                {
                    _ = ReadCorruptScopedPerson(con, tx, familyId, branchId, reviewerId, id);
                    quarantines.Add(id);
                }
                catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException or InvalidCastException or FormatException or OverflowException)
                {
                    continue;
                }
            }
        }
        tx.Commit();
        return new(result, quarantines, ids.Count == 100 ? ids[^1] : null);
    }

    public void QuarantineScopedPerson(Guid familyId, Guid branchId, Guid reviewerId, Guid proposalId)
    {
        RequireIds(familyId, branchId, reviewerId, proposalId);
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: false);
        EnsureScopedPersonSchema(con, tx);
        RequireScopedPersonPermission(con, tx, familyId, branchId, reviewerId, ScopedPersonPermission.ReviewPerson);
        var (person, author, pendingEvent, payloadHash) =
            ReadCorruptScopedPerson(con, tx, familyId, branchId, reviewerId, proposalId);
        var seal = SealScopedState(familyId, branchId, proposalId, author, person, "Rejected", reviewerId, null);
        var content = "Rejected|" + reviewerId.ToString("D") + "|" + payloadHash + "|" +
            Convert.ToBase64String(SHA256.HashData(seal)) + "|" +
            Convert.ToBase64String(SHA256.HashData(pendingEvent));
        var envelope = Encrypt(familyId, person.Id, content,
            [..ScopedStateContext(proposalId, branchId, author, person.IsLiving), 2]);
        if (Exec(con, tx, """
            UPDATE scoped_person_proposals SET state='Rejected',reviewer=$reviewer,state_envelope=$seal
            WHERE id=$id AND family_id=$f AND branch_id=$b AND state='Pending';
            """, ("$reviewer", reviewerId.ToString("D")), ("$seal", seal),
            ("$id", proposalId.ToString("D")), ("$f", familyId.ToString("D")),
            ("$b", branchId.ToString("D"))) != 1)
            throw new InvalidOperationException("Proposal unavailable.");
        Exec(con, tx, "INSERT INTO scoped_person_events(proposal_id,envelope) VALUES($id,$event);",
            ("$id", proposalId.ToString("D")), ("$event", envelope));
        Audit(con, tx, familyId, proposalId, reviewerId, "scoped-person-quarantined");
        tx.Commit();
    }

    private (Person Person, Guid Author, byte[] PendingEvent, string PayloadHash) ReadCorruptScopedPerson(
        SqliteConnection con, SqliteTransaction tx, Guid family, Guid branch, Guid reviewer, Guid proposal)
    {
        Guid personId, author;
        bool living;
        byte[] payload, seal;
        using (var cmd = Cmd(con, tx, """
            SELECT person_id,submitter,living,payload,state_envelope,reviewer
            FROM scoped_person_proposals WHERE id=$id AND family_id=$f AND branch_id=$b AND state='Pending';
            """, ("$id", proposal.ToString("D")), ("$f", family.ToString("D")), ("$b", branch.ToString("D"))))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read() || !Guid.TryParseExact(reader.GetString(0), "D", out personId) || personId == Guid.Empty ||
                !Guid.TryParseExact(reader.GetString(1), "D", out author) || author == Guid.Empty ||
                reader.GetInt64(2) is not (0 or 1) || !reader.IsDBNull(5))
                throw new InvalidDataException("Proposal invalid.");
            living = reader.GetInt64(2) == 1;
            payload = reader.GetFieldValue<byte[]>(3);
            seal = reader.GetFieldValue<byte[]>(4);
        }
        if (author == reviewer) throw new UnauthorizedAccessException("Independent reviewer required.");
        byte[] pendingEvent;
        using (var cmd = Cmd(con, tx, "SELECT envelope FROM scoped_person_events WHERE proposal_id=$id ORDER BY seq;",
            ("$id", proposal.ToString("D"))))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidDataException("Proposal history unavailable.");
            pendingEvent = reader.GetFieldValue<byte[]>(0);
            if (reader.Read()) throw new InvalidDataException("Proposal history invalid.");
        }
        var parts = Decrypt(family, personId, pendingEvent,
            [..ScopedStateContext(proposal, branch, author, living), 2]).Split('|');
        if (parts.Length != 5 || parts[0] != "Pending" || parts[1] != "" || parts[4] != "")
            throw new InvalidDataException("Proposal history invalid.");
        VerifyPersonAudit(con, tx, family, proposal, [("scoped-person-proposed", author.ToString("D"))]);
        if (parts[2] == Convert.ToBase64String(SHA256.HashData(payload)) &&
            parts[3] == Convert.ToBase64String(SHA256.HashData(seal)))
            throw new InvalidDataException("Corrupted proposal required.");
        return (new Person(personId, "Quarantined", living), author, pendingEvent, parts[2]);
    }

    public void ReviewScopedPerson(Guid familyId, Guid branchId, Guid reviewerId,
        Guid proposalId, bool approve)
    {
        RequireIds(familyId, branchId, reviewerId, proposalId);
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: false);
        EnsureScopedPersonSchema(con, tx);
        RequireScopedPersonPermission(con, tx, familyId, branchId, reviewerId,
            ScopedPersonPermission.ReviewPerson);
        var proposal = LoadScopedProposal(con, tx, familyId, branchId, proposalId, "Pending");
        if (proposal.SubmittedBy == reviewerId)
            throw new UnauthorizedAccessException("Independent reviewer required.");
        byte[]? published = null;
        if (approve)
        {
            RequireScopedPersonPermission(con, tx, familyId, branchId, proposal.SubmittedBy,
                ScopedPersonPermission.CreatePerson);
            published = Encrypt(familyId, proposal.Person.Id, proposal.Person.DisplayName);
            Exec(con, tx, """
                INSERT INTO people(family_id,id,encrypted_name,living)
                VALUES($f,$p,$name,$living);
                """, ("$f", familyId.ToString("D")), ("$p", proposal.Person.Id.ToString("D")),
                ("$name", published),
                ("$living", proposal.Person.IsLiving ? 1 : 0));
        }
        var state = approve ? "Approved" : "Rejected";
        var seal = SealScopedState(familyId, branchId, proposalId, proposal.SubmittedBy,
            proposal.Person, state, reviewerId, published);
        Exec(con, tx, """
            UPDATE scoped_person_proposals SET state=$state,reviewer=$reviewer,state_envelope=$seal
            WHERE id=$id AND family_id=$f AND branch_id=$b AND state='Pending';
            """, ("$state", approve ? "Approved" : "Rejected"),
            ("$reviewer", reviewerId.ToString("D")), ("$id", proposalId.ToString("D")),
            ("$f", familyId.ToString("D")), ("$b", branchId.ToString("D")),
            ("$seal", seal));
        var payload = ScopedPersonPayload(con, tx, proposalId);
        PersonEvent(con, tx, familyId, branchId, proposalId, proposal.SubmittedBy,
            proposal.Person, payload, seal, state, reviewerId);
        Audit(con, tx, familyId, proposalId, reviewerId,
            approve ? "scoped-person-approved" : "scoped-person-rejected");
        tx.Commit();
    }

    public void UndoScopedPerson(Guid familyId, Guid branchId, Guid reviewerId, Guid proposalId)
    {
        RequireIds(familyId, branchId, reviewerId, proposalId);
        using var con = Open();
        using var tx = con.BeginTransaction(deferred: false);
        EnsureScopedPersonSchema(con, tx);
        RequireScopedPersonPermission(con, tx, familyId, branchId, reviewerId,
            ScopedPersonPermission.UndoPerson);
        var (proposal, stateContent) = LoadScopedProposalAndState(con, tx, familyId, branchId,
            proposalId, "Approved");
        if (proposal.SubmittedBy == reviewerId)
            throw new UnauthorizedAccessException("Independent reviewer required.");
        byte[] name;
        long living;
        using (var cmd = Cmd(con, tx, """
            SELECT encrypted_name,living FROM people WHERE family_id=$f AND id=$p;
            """, ("$f", familyId.ToString("D")), ("$p", proposal.Person.Id.ToString("D"))))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("Approved person unavailable.");
            name = reader.GetFieldValue<byte[]>(0);
            living = reader.GetInt64(1);
        }
        if (!stateContent.EndsWith("|" + Convert.ToBase64String(name), StringComparison.Ordinal) ||
            living != (proposal.Person.IsLiving ? 1 : 0) ||
            Decrypt(familyId, proposal.Person.Id, name) != proposal.Person.DisplayName)
            throw new InvalidOperationException("Approved person has changed.");
        if (Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM relationship_proposals
            WHERE family_id=$f AND state='Pending' AND (from_id=$p OR to_id=$p);
            """, ("$f", familyId.ToString("D")),
            ("$p", proposal.Person.Id.ToString("D"))) != 0)
            throw new InvalidOperationException("Person has pending relationships.");
        if (Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM scoped_relationship_proposals
            WHERE family_id=$f AND state IN ('Pending','Approved') AND (from_id=$p OR to_id=$p);
            """, ("$f", familyId.ToString("D")),
            ("$p", proposal.Person.Id.ToString("D"))) != 0)
            throw new InvalidOperationException("Person has scoped relationships.");
        // Foreign keys prevent deleting a person with existing graph dependencies.
        Exec(con, tx, "DELETE FROM people WHERE family_id=$f AND id=$p;",
            ("$f", familyId.ToString("D")), ("$p", proposal.Person.Id.ToString("D")));
        var seal = SealScopedState(familyId, branchId, proposalId, proposal.SubmittedBy,
            proposal.Person, "Undone", reviewerId, null);
        Exec(con, tx, """
            UPDATE scoped_person_proposals SET state='Undone',reviewer=$reviewer,state_envelope=$seal
            WHERE family_id=$f AND branch_id=$b AND id=$id AND state='Approved';
            """, ("$f", familyId.ToString("D")), ("$b", branchId.ToString("D")),
            ("$id", proposalId.ToString("D")), ("$reviewer", reviewerId.ToString("D")),
            ("$seal", seal));
        var payload = ScopedPersonPayload(con, tx, proposalId);
        PersonEvent(con, tx, familyId, branchId, proposalId, proposal.SubmittedBy,
            proposal.Person, payload, seal, "Undone", reviewerId);
        Audit(con, tx, familyId, proposalId, reviewerId, "scoped-person-undone");
        tx.Commit();
    }

    private ScopedPendingPerson LoadScopedProposal(SqliteConnection con, SqliteTransaction tx,
        Guid family, Guid branch, Guid proposal, string state)
        => LoadScopedProposalAndState(con, tx, family, branch, proposal, state).Proposal;

    private (ScopedPendingPerson Proposal, string StateContent) LoadScopedProposalAndState(
        SqliteConnection con, SqliteTransaction tx, Guid family, Guid branch, Guid proposal, string state)
    {
        using var cmd = Cmd(con, tx, """
            SELECT id,person_id,submitter,living,payload,state,reviewer,state_envelope
            FROM scoped_person_proposals
            WHERE family_id=$f AND branch_id=$b AND id=$id AND state=$state;
            """, ("$f", family.ToString("D")), ("$b", branch.ToString("D")),
            ("$id", proposal.ToString("D")), ("$state", state));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Proposal unavailable.");
        var result = ReadScopedProposal(family, branch, reader);
        var payload = reader.GetFieldValue<byte[]>(4);
        var seal = reader.GetFieldValue<byte[]>(7);
        var content = Decrypt(family, result.Person.Id, reader.GetFieldValue<byte[]>(7),
            ScopedStateContext(result.ProposalId, branch, result.SubmittedBy, result.Person.IsLiving));
        reader.Close();
        VerifyPersonHistory(con, tx, family, branch, result, payload, seal, state);
        return (result, content);
    }

    private static byte[] ScopedPersonPayload(SqliteConnection con, SqliteTransaction tx, Guid proposal)
    {
        using var cmd = Cmd(con, tx, "SELECT payload FROM scoped_person_proposals WHERE id=$id;",
            ("$id", proposal.ToString("D")));
        return (byte[])cmd.ExecuteScalar()!;
    }

    private void PersonEvent(SqliteConnection con, SqliteTransaction tx, Guid family, Guid branch,
        Guid proposal, Guid author, Person person, byte[] payload, byte[] seal, string state, Guid? reviewer)
    {
        using var cmd = Cmd(con, tx,
            "SELECT envelope FROM scoped_person_events WHERE proposal_id=$id ORDER BY seq DESC LIMIT 1;",
            ("$id", proposal.ToString("D")));
        var previous = cmd.ExecuteScalar() as byte[];
        var content = state + "|" + (reviewer?.ToString("D") ?? "") + "|" +
            Convert.ToBase64String(SHA256.HashData(payload)) + "|" +
            Convert.ToBase64String(SHA256.HashData(seal)) + "|" +
            (previous is null ? "" : Convert.ToBase64String(SHA256.HashData(previous)));
        var envelope = Encrypt(family, person.Id, content,
            [..ScopedStateContext(proposal, branch, author, person.IsLiving), 2]);
        Exec(con, tx, "INSERT INTO scoped_person_events(proposal_id,envelope) VALUES($id,$event);",
            ("$id", proposal.ToString("D")), ("$event", envelope));
    }

    private void VerifyPersonHistory(SqliteConnection con, SqliteTransaction tx, Guid family, Guid branch,
        ScopedPendingPerson proposal, byte[] payload, byte[] seal, string state)
    {
        using var cmd = Cmd(con, tx, "SELECT envelope FROM scoped_person_events WHERE proposal_id=$id ORDER BY seq;",
            ("$id", proposal.ProposalId.ToString("D")));
        using var reader = cmd.ExecuteReader();
        byte[]? previous = null;
        string? lastState = null;
        string? lastSeal = null;
        var count = 0;
        var expectedAudit = new List<(string Action, string Actor)>();
        while (reader.Read())
        {
            var current = reader.GetFieldValue<byte[]>(0);
            var parts = Decrypt(family, proposal.Person.Id, current,
                [..ScopedStateContext(proposal.ProposalId, branch, proposal.SubmittedBy, proposal.Person.IsLiving), 2]).Split('|');
            count++;
            if (parts.Length != 5 || count > 3 ||
                parts[2] != Convert.ToBase64String(SHA256.HashData(payload)) ||
                parts[4] != (previous is null ? "" : Convert.ToBase64String(SHA256.HashData(previous))) ||
                (count == 1 && (parts[0] != "Pending" || parts[1] != "")) ||
                (count == 2 && (parts[0] is not ("Approved" or "Rejected") ||
                    !Guid.TryParseExact(parts[1], "D", out var reviewer) || reviewer == Guid.Empty || reviewer == proposal.SubmittedBy)) ||
                (count == 3 && (lastState != "Approved" || parts[0] != "Undone" ||
                    !Guid.TryParseExact(parts[1], "D", out var undoer) || undoer == Guid.Empty || undoer == proposal.SubmittedBy)))
                throw new InvalidDataException("Proposal history invalid.");
            previous = current;
            lastState = parts[0];
            lastSeal = parts[3];
            expectedAudit.Add((count == 1 ? "scoped-person-proposed" : "scoped-person-" + parts[0].ToLowerInvariant(),
                count == 1 ? proposal.SubmittedBy.ToString("D") : parts[1]));
        }
        reader.Close();
        if (previous is null || lastState != state ||
            lastSeal != Convert.ToBase64String(SHA256.HashData(seal)))
            throw new InvalidDataException("Proposal history invalid.");
        VerifyPersonAudit(con, tx, family, proposal.ProposalId, expectedAudit);
    }

    private static void VerifyPersonAudit(SqliteConnection con, SqliteTransaction tx, Guid family, Guid proposal,
        IReadOnlyList<(string Action, string Actor)> expected)
    {
        using var cmd = Cmd(con, tx, """
            SELECT action,reviewer FROM audit WHERE family_id=$f AND subject_id=$id
              AND action IN ('scoped-person-proposed','scoped-person-approved','scoped-person-rejected',
                             'scoped-person-undone','scoped-person-quarantined')
            ORDER BY seq;
            """, ("$f", family.ToString("D")), ("$id", proposal.ToString("D")));
        using var reader = cmd.ExecuteReader();
        var index = 0;
        while (reader.Read())
        {
            if (index >= expected.Count || reader.GetString(0) != expected[index].Action ||
                reader.GetString(1) != expected[index].Actor)
                throw new InvalidDataException("Proposal audit invalid.");
            index++;
        }
        if (index != expected.Count) throw new InvalidDataException("Proposal audit invalid.");
    }

    private ScopedPendingPerson ReadScopedProposal(Guid family, Guid branch, SqliteDataReader reader)
    {
        if (!Guid.TryParseExact(reader.GetString(0), "D", out var proposal) || proposal == Guid.Empty ||
            !Guid.TryParseExact(reader.GetString(1), "D", out var person) || person == Guid.Empty ||
            !Guid.TryParseExact(reader.GetString(2), "D", out var author) || author == Guid.Empty ||
            reader.GetInt64(3) is not (0 or 1))
            throw new InvalidDataException("Proposal invalid.");
        var living = reader.GetInt64(3) == 1;
        var name = Decrypt(family, person, reader.GetFieldValue<byte[]>(4),
            ScopedPersonContext(proposal, branch, author, living));
        var state = reader.GetString(5);
        var reviewer = reader.IsDBNull(6) ? "" : reader.GetString(6);
        var stateContent = Decrypt(family, person, reader.GetFieldValue<byte[]>(7),
            ScopedStateContext(proposal, branch, author, living));
        var parts = stateContent.Split('|');
        if (parts.Length != 3 || parts[0] != state || parts[1] != reviewer ||
            (state == "Pending" && (reviewer != "" || parts[2] != "")) ||
            (state != "Pending" && !Guid.TryParseExact(reviewer, "D", out _)) ||
            (state != "Approved" && parts[2] != "") ||
            (state == "Approved" && parts[2] == ""))
            throw new InvalidDataException("Proposal state invalid.");
        return new(proposal, branch, author, new Person(person, name, living));
    }

    private byte[] SealScopedState(Guid family, Guid branch, Guid proposal, Guid author,
        Person person, string state, Guid? reviewer, byte[]? published)
        => Encrypt(family, person.Id,
            state + "|" + (reviewer?.ToString("D") ?? "") + "|" +
            (published is null ? "" : Convert.ToBase64String(published)),
            ScopedStateContext(proposal, branch, author, person.IsLiving));

    private static byte[] ScopedStateContext(Guid proposal, Guid branch, Guid author, bool living)
        => [..ScopedPersonContext(proposal, branch, author, living), 1];

    private static byte[] ScopedPersonContext(Guid proposal, Guid branch, Guid author, bool living)
        => [..Encoding.UTF8.GetBytes("familytree-scoped-person-v1"),
            ..proposal.ToByteArray(), ..branch.ToByteArray(), ..author.ToByteArray(),
            living ? (byte)1 : (byte)0];
}

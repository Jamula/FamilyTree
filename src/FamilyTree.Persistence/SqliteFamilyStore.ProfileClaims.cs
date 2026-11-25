using FamilyTree.Domain;
using Microsoft.Data.Sqlite;

namespace FamilyTree.Persistence;

public sealed partial class SqliteFamilyStore
{
    public sealed record StoredProfileClaim(
        Guid Id, Guid FamilyId, Guid BranchId, Guid PersonId, Guid PrincipalId,
        ProfileClaimState State, Guid? ReviewedBy);

    public sealed record ProfileClaimAuditEntry(
        long Sequence, Guid ClaimId, Guid ActorId, string Action);

    public StoredProfileClaim ProposeProfileClaim(
        Guid familyId, Guid principalId, Guid branchId, Guid personId)
    {
        RequireIds(familyId, principalId, branchId, personId);
        var claim = ProfileClaim.Submit(familyId, principalId, personId,
            activeFamilyMember: true, canViewPerson: true);

        using var con = Open();
        using var tx = con.BeginTransaction();
        EnsureProfileClaimSchema(con, tx);
        RequireMember(con, tx, familyId, principalId, administrator: false);
        if (Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM person_visibility
            WHERE family_id=$f AND branch_id=$b AND principal_id=$p AND person_id=$person;
            """, ("$f", familyId.ToString("D")), ("$b", branchId.ToString("D")),
            ("$p", principalId.ToString("D")), ("$person", personId.ToString("D"))) != 1)
            throw new UnauthorizedAccessException("Access denied.");

        try
        {
            var identityEnvelope = Encrypt(familyId, claim.Id,
                $"{personId:D}\0{principalId:D}\0{branchId:D}", principalId.ToByteArray());
            Exec(con, tx, """
                INSERT INTO profile_claims
                  (id,family_id,branch_id,person_id,principal_id,identity_envelope,state)
                VALUES($id,$f,$branch,$person,$principal,$identity,'Pending');
                """, ("$id", claim.Id.ToString("D")), ("$f", familyId.ToString("D")),
                ("$branch", branchId.ToString("D")), ("$person", personId.ToString("D")),
                ("$principal", principalId.ToString("D")),
                ("$identity", identityEnvelope));
        }
        catch (SqliteException ex) when (
            ex.SqliteExtendedErrorCode is 1555 or 2067)
        {
            throw new InvalidOperationException("An active profile claim already exists.", ex);
        }

        Audit(con, tx, familyId, claim.Id, principalId, "profile-claim-proposed");
        tx.Commit();
        return new StoredProfileClaim(claim.Id, familyId, branchId, personId, principalId,
            claim.State, null);
    }

    public StoredProfileClaim ReviewProfileClaim(
        Guid familyId, Guid reviewerId, Guid claimId, bool approve)
    {
        RequireIds(familyId, reviewerId, claimId);
        using var con = Open();
        using var tx = con.BeginTransaction();
        EnsureProfileClaimSchema(con, tx);
        RequireMember(con, tx, familyId, reviewerId, administrator: true);
        RequireVisibleProfileClaim(con, tx, familyId, reviewerId, claimId);
        var claim = FindPendingProfileClaim(con, tx, familyId, claimId);
        if (claim.PrincipalId == reviewerId)
            throw new UnauthorizedAccessException("Access denied.");
        if (approve)
        {
            RequireMember(con, tx, familyId, claim.PrincipalId, administrator: false);
            if (Scalar<long>(con, tx, """
                SELECT COUNT(*) FROM person_visibility
                WHERE family_id=$f AND branch_id=$b AND principal_id=$p AND person_id=$person;
                """, ("$f", familyId.ToString("D")), ("$b", claim.BranchId.ToString("D")),
                ("$p", claim.PrincipalId.ToString("D")), ("$person", claim.PersonId.ToString("D"))) != 1)
                throw new UnauthorizedAccessException("Access denied.");
        }
        var reviewed = ProfileClaim.Submit(familyId, claim.PrincipalId, claim.PersonId,
            activeFamilyMember: true, canViewPerson: true)
            .Review(reviewerId, familyId, canApproveClaims: true, approve);
        if (Exec(con, tx, """
            UPDATE profile_claims SET state=$state, reviewed_by=$reviewer
            WHERE family_id=$f AND id=$id AND state='Pending';
            """, ("$state", reviewed.State.ToString()), ("$reviewer", reviewerId.ToString("D")),
            ("$f", familyId.ToString("D")), ("$id", claimId.ToString("D"))) != 1)
            throw new InvalidOperationException("Pending profile claim unavailable.");

        Audit(con, tx, familyId, claimId, reviewerId,
            approve ? "profile-claim-approved" : "profile-claim-rejected");
        tx.Commit();
        return claim with { State = reviewed.State, ReviewedBy = reviewerId };
    }

    public StoredProfileClaim WithdrawProfileClaim(
        Guid familyId, Guid principalId, Guid claimId)
    {
        RequireIds(familyId, principalId, claimId);
        using var con = Open();
        using var tx = con.BeginTransaction();
        EnsureProfileClaimSchema(con, tx);
        RequireMember(con, tx, familyId, principalId, administrator: false);
        if (Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM profile_claims c
            WHERE c.family_id=$f AND c.id=$id AND c.principal_id=$p AND c.state='Pending'
              AND EXISTS (SELECT 1 FROM person_visibility v
                  WHERE v.family_id=c.family_id AND v.branch_id=c.branch_id
                    AND v.person_id=c.person_id AND v.principal_id=$p);
            """, ("$f", familyId.ToString("D")), ("$id", claimId.ToString("D")),
            ("$p", principalId.ToString("D"))) != 1)
            throw new UnauthorizedAccessException("Access denied.");
        var claim = FindPendingProfileClaim(con, tx, familyId, claimId);
        if (claim.PrincipalId != principalId)
            throw new UnauthorizedAccessException("Withdrawal denied.");
        var withdrawn = ProfileClaim.Submit(familyId, principalId, claim.PersonId,
            activeFamilyMember: true, canViewPerson: true).Withdraw(principalId);
        if (Exec(con, tx, """
            UPDATE profile_claims SET state='Withdrawn'
            WHERE family_id=$f AND id=$id AND state='Pending' AND principal_id=$p;
            """, ("$f", familyId.ToString("D")), ("$id", claimId.ToString("D")),
            ("$p", principalId.ToString("D"))) != 1)
            throw new InvalidOperationException("Pending profile claim unavailable.");
        Audit(con, tx, familyId, claimId, principalId, "profile-claim-withdrawn");
        tx.Commit();
        return claim with { State = withdrawn.State };
    }

    public IReadOnlyList<StoredProfileClaim> ListPendingProfileClaims(
        Guid familyId, Guid administratorId)
    {
        RequireIds(familyId, administratorId);
        using var con = Open();
        using var tx = con.BeginTransaction();
        EnsureProfileClaimSchema(con, tx);
        RequireMember(con, tx, familyId, administratorId, administrator: true);
        var claims = new List<StoredProfileClaim>();
        using (var query = Cmd(con, tx, """
            SELECT c.id,c.person_id,c.principal_id,c.branch_id,c.reviewed_by,c.identity_envelope
            FROM profile_claims c WHERE c.family_id=$f AND c.state='Pending'
              AND EXISTS (SELECT 1 FROM person_visibility v
                  WHERE v.family_id=c.family_id AND v.branch_id=c.branch_id
                    AND v.person_id=c.person_id AND v.principal_id=$admin)
            ORDER BY c.id LIMIT 100;
            """, ("$f", familyId.ToString("D")), ("$admin", administratorId.ToString("D"))))
        using (var reader = query.ExecuteReader())
            while (reader.Read())
            {
                var claimId = Guid.Parse(reader.GetString(0));
                var personId = Guid.Parse(reader.GetString(1));
                var principalId = Guid.Parse(reader.GetString(2));
                var branchId = Guid.Parse(reader.GetString(3));
                VerifyProfileClaimIdentity(familyId, claimId, branchId, personId, principalId,
                    reader.GetFieldValue<byte[]>(5));
                claims.Add(new StoredProfileClaim(Guid.Parse(reader.GetString(0)), familyId,
                    branchId, personId, principalId,
                    ProfileClaimState.Pending,
                    reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4))));
            }
        tx.Commit();
        return claims;
    }

    public IReadOnlyList<ProfileClaimAuditEntry> ReadProfileClaimAudit(
        Guid familyId, Guid administratorId, Guid claimId)
    {
        RequireIds(familyId, administratorId, claimId);
        using var con = Open();
        using var tx = con.BeginTransaction();
        EnsureProfileClaimSchema(con, tx);
        RequireMember(con, tx, familyId, administratorId, administrator: true);
        RequireVisibleProfileClaim(con, tx, familyId, administratorId, claimId);
        var entries = new List<ProfileClaimAuditEntry>();
        using (var query = Cmd(con, tx, """
            SELECT seq,reviewer,action FROM audit
            WHERE family_id=$f AND subject_id=$claim
              AND action LIKE 'profile-claim-%'
            ORDER BY seq;
            """, ("$f", familyId.ToString("D")), ("$claim", claimId.ToString("D"))))
        using (var reader = query.ExecuteReader())
            while (reader.Read())
                entries.Add(new ProfileClaimAuditEntry(reader.GetInt64(0), claimId,
                    Guid.Parse(reader.GetString(1)), reader.GetString(2)));
        tx.Commit();
        return entries;
    }

    private void RequireVisibleProfileClaim(
        SqliteConnection con, SqliteTransaction tx, Guid familyId, Guid viewerId, Guid claimId)
    {
        using var query = Cmd(con, tx, """
            SELECT c.branch_id,c.person_id,c.principal_id,c.identity_envelope
            FROM profile_claims c
            WHERE c.family_id=$f AND c.id=$id AND EXISTS (
                SELECT 1 FROM person_visibility v
                WHERE v.family_id=c.family_id AND v.branch_id=c.branch_id
                  AND v.person_id=c.person_id AND v.principal_id=$viewer);
            """, ("$f", familyId.ToString("D")), ("$id", claimId.ToString("D")),
            ("$viewer", viewerId.ToString("D")));
        using var reader = query.ExecuteReader();
        if (!reader.Read())
            throw new UnauthorizedAccessException("Access denied.");
        VerifyProfileClaimIdentity(familyId, claimId, Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
            reader.GetFieldValue<byte[]>(3));
    }

    private StoredProfileClaim FindPendingProfileClaim(
        SqliteConnection con, SqliteTransaction tx, Guid familyId, Guid claimId)
    {
        using var query = Cmd(con, tx, """
            SELECT branch_id,person_id,principal_id,identity_envelope FROM profile_claims
            WHERE family_id=$f AND id=$id AND state='Pending';
            """, ("$f", familyId.ToString("D")), ("$id", claimId.ToString("D")));
        using var reader = query.ExecuteReader();
        if (!reader.Read())
            throw new UnauthorizedAccessException("Access denied.");
        var branchId = Guid.Parse(reader.GetString(0));
        var personId = Guid.Parse(reader.GetString(1));
        var principalId = Guid.Parse(reader.GetString(2));
        VerifyProfileClaimIdentity(familyId, claimId, branchId, personId, principalId,
            reader.GetFieldValue<byte[]>(3));
        return new StoredProfileClaim(claimId, familyId, branchId, personId, principalId,
            ProfileClaimState.Pending, null);
    }

    private void VerifyProfileClaimIdentity(
        Guid familyId, Guid claimId, Guid branchId, Guid personId,
        Guid principalId, byte[] envelope)
    {
        var identity = Decrypt(familyId, claimId, envelope, principalId.ToByteArray());
        var expected = $"{personId:D}\0{principalId:D}\0{branchId:D}";
        if (!string.Equals(identity, expected, StringComparison.Ordinal))
            throw new InvalidDataException("Profile claim identity integrity check failed.");
    }

    private static void EnsureProfileClaimSchema(SqliteConnection con, SqliteTransaction tx)
    {
        using var command = con.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS profile_claims (
                id TEXT PRIMARY KEY NOT NULL,
                family_id TEXT NOT NULL,
                branch_id TEXT NOT NULL,
                person_id TEXT NOT NULL,
                principal_id TEXT NOT NULL,
                identity_envelope BLOB NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('Pending','Approved','Rejected','Withdrawn')),
                reviewed_by TEXT NULL,
                FOREIGN KEY(family_id,person_id) REFERENCES people(family_id,id),
                FOREIGN KEY(family_id,principal_id) REFERENCES memberships(family_id,principal_id),
                FOREIGN KEY(family_id,reviewed_by) REFERENCES memberships(family_id,principal_id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS profile_claim_person_active
                ON profile_claims(family_id,person_id)
                WHERE state IN ('Pending','Approved');
            CREATE UNIQUE INDEX IF NOT EXISTS profile_claim_principal_active
                ON profile_claims(family_id,principal_id)
                WHERE state IN ('Pending','Approved');
            """;
        command.ExecuteNonQuery();
    }
}

using FamilyTree.Domain;
using Microsoft.Data.Sqlite;

namespace FamilyTree.Persistence;

public sealed partial class SqliteFamilyStore
{
    public sealed record StoredProfileClaim(
        Guid Id, Guid FamilyId, Guid PersonId, Guid PrincipalId,
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
            Exec(con, tx, """
                INSERT INTO profile_claims(id,family_id,person_id,principal_id,state)
                VALUES($id,$f,$person,$principal,'Pending');
                """, ("$id", claim.Id.ToString("D")), ("$f", familyId.ToString("D")),
                ("$person", personId.ToString("D")), ("$principal", principalId.ToString("D")));
        }
        catch (SqliteException ex) when (
            ex.SqliteExtendedErrorCode is 1555 or 2067)
        {
            throw new InvalidOperationException("An active profile claim already exists.", ex);
        }

        Audit(con, tx, familyId, claim.Id, principalId, "profile-claim-proposed");
        tx.Commit();
        return new StoredProfileClaim(claim.Id, familyId, personId, principalId,
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
        var claim = FindPendingProfileClaim(con, tx, familyId, claimId);
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
            SELECT id,person_id,principal_id,reviewed_by
            FROM profile_claims WHERE family_id=$f AND state='Pending'
            ORDER BY id LIMIT 100;
            """, ("$f", familyId.ToString("D"))))
        using (var reader = query.ExecuteReader())
            while (reader.Read())
                claims.Add(new StoredProfileClaim(Guid.Parse(reader.GetString(0)), familyId,
                    Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
                    ProfileClaimState.Pending,
                    reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3))));
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

    private static StoredProfileClaim FindPendingProfileClaim(
        SqliteConnection con, SqliteTransaction tx, Guid familyId, Guid claimId)
    {
        using var query = Cmd(con, tx, """
            SELECT person_id,principal_id FROM profile_claims
            WHERE family_id=$f AND id=$id AND state='Pending';
            """, ("$f", familyId.ToString("D")), ("$id", claimId.ToString("D")));
        using var reader = query.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("Pending profile claim unavailable.");
        return new StoredProfileClaim(claimId, familyId,
            Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
            ProfileClaimState.Pending, null);
    }

    private static void EnsureProfileClaimSchema(SqliteConnection con, SqliteTransaction tx)
    {
        using var command = con.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS profile_claims (
                id TEXT PRIMARY KEY NOT NULL,
                family_id TEXT NOT NULL,
                person_id TEXT NOT NULL,
                principal_id TEXT NOT NULL,
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

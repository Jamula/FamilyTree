using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FamilyTree.Domain;
using FamilyTree.Persistence;
using Microsoft.Data.Sqlite;
using Permission = FamilyTree.Persistence.SqliteFamilyStore.ScopedPersonPermission;

internal static class SqliteScopedPersonMutationAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var directory = Path.Join(Path.GetTempPath(), "scoped-person-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "synthetic.sqlite");
        var key = RandomNumberGenerator.GetBytes(32);
        var family = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var otherBranch = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var author = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        try
        {
            using var store = new SqliteFamilyStore(path, key);
            store.CreateFictionalFamily(family, admin);
            Join(store, family, admin, author);
            Join(store, family, admin, reviewer);
            var visibility = new SqliteGraphGrantStore(path);
            var seed = new Person(Guid.NewGuid(), "Synthetic Seed");
            var legacy = store.ProposePerson(family, author, seed);
            store.ReviewPerson(family, admin, legacy, true);
            visibility.GrantPerson(family, branch, admin, author, seed.Id);
            const string name = "Synthetic Scoped Person";
            Expect<UnauthorizedAccessException>(() => store.ProposeScopedPerson(family, branch, author, name));
            Expect<UnauthorizedAccessException>(() => store.GrantScopedPersonPermission(
                family, branch, author, author, Permission.CreatePerson));
            store.GrantScopedPersonPermission(family, branch, admin, author, Permission.CreatePerson);
            store.GrantScopedPersonPermission(family, branch, admin, reviewer, Permission.ReviewPerson);
            store.GrantScopedPersonPermission(family, branch, admin, reviewer, Permission.UndoPerson);
            Expect<UnauthorizedAccessException>(() => store.ProposeScopedPerson(family, otherBranch, author, name));
            var proposal = store.ProposeScopedPerson(family, branch, author, name, false);
            var person = store.ListScopedPendingPeople(family, branch, reviewer).Single().Person;
            Check(person.Id != Guid.Empty && person.DisplayName == name && !person.IsLiving,
                "server generated identity and exact payload");
            Expect<SqliteException>(() => store.ProposePerson(family, author, person));
            Expect<InvalidOperationException>(() => store.ReviewPerson(family, admin, proposal, true));
            Expect<UnauthorizedAccessException>(() => store.ReviewScopedPerson(family, otherBranch, reviewer, proposal, true));
            store.GrantScopedPersonPermission(family, branch, admin, author, Permission.ReviewPerson);
            Expect<UnauthorizedAccessException>(() => store.ReviewScopedPerson(family, branch, author, proposal, true));
            Check(store.ListScopedPendingPeople(family, branch, reviewer).Single().Person == person, "exact pending content");
            Check(store.ListScopedPendingPeople(family, branch, author).Count == 0, "self excluded");
            var legacyPerson = new Person(Guid.NewGuid(), "Synthetic Legacy");
            var legacyPending = store.ProposePerson(family, author, legacyPerson);
            Expect<InvalidOperationException>(() => store.ReviewScopedPerson(family, branch, reviewer, legacyPending, true));
            store.ReviewScopedPerson(family, branch, reviewer, proposal, true);
            Check(!store.ReadPeople(family, admin).Any(x => x == person) &&
                !store.ReadPeople(family, reviewer).Any(x => x == person), "legacy reader excludes scoped person");
            Check(!visibility.ReadVisiblePersonIds(family, branch, author).Contains(person.Id),
                "no implicit visibility");
            Expect<InvalidOperationException>(() => store.ReviewScopedPerson(family, branch, reviewer, proposal, true));
            visibility.GrantPerson(family, branch, admin, author, person.Id);
            visibility.GrantDisplayName(family, branch, admin, author, person.Id);
            using (var source = new SqliteTreeReadSource(path, key))
            {
                var read = new SqlitePersonReadService(source).GetAsync(author, family, branch, person.Id)
                    .GetAwaiter().GetResult();
                Check(read is not null && read.DisplayName == name, "explicitly granted reader decrypts standard ciphertext");
            }

            Expect<SqliteException>(() => store.UndoScopedPerson(family, branch, reviewer, proposal));
            visibility.RevokePerson(family, branch, admin, author, person.Id);
            var link = store.ProposeRelationship(family, author,
                Relationship.Create(person.Id, seed.Id, RelationKind.Friend));
            Expect<InvalidOperationException>(() => store.UndoScopedPerson(family, branch, reviewer, proposal));
            store.ReviewRelationship(family, admin, link, false);
            store.UndoScopedPerson(family, branch, reviewer, proposal);
            Check(!store.ReadPeople(family, admin).Any(x => x.Id == person.Id), "undo removed person");
            Expect<InvalidOperationException>(() => store.UndoScopedPerson(family, branch, reviewer, proposal));

            var replacedProposal = store.ProposeScopedPerson(family, branch, author, "Synthetic Replacement");
            var replaced = store.ListScopedPendingPeople(family, branch, reviewer).Single().Person;
            store.ReviewScopedPerson(family, branch, reviewer, replacedProposal, true);
            using (var db = Connect(path))
            {
                Execute(db, "DELETE FROM people WHERE family_id=$f AND id=$p;", ("$f", family.ToString("D")), ("$p", replaced.Id.ToString("D")));
            }
            var replacementLegacy = store.ProposePerson(family, author, replaced);
            store.ReviewPerson(family, admin, replacementLegacy, true);
            Expect<InvalidOperationException>(() => store.UndoScopedPerson(family, branch, reviewer, replacedProposal));
            using (var db = Connect(path))
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM people WHERE family_id=$f AND id=$p;";
                cmd.Parameters.AddWithValue("$f", family.ToString("D"));
                cmd.Parameters.AddWithValue("$p", replaced.Id.ToString("D"));
                Check((long)cmd.ExecuteScalar()! == 1, "replacement survives undo");
            }

            var tampered = store.ProposeScopedPerson(family, branch, author, "Synthetic Tamper");
            using (var db = Connect(path))
            {
                Execute(db, "UPDATE scoped_person_proposals SET submitter=$p WHERE id=$id;",
                    ("$p", reviewer.ToString("D")), ("$id", tampered.ToString("D")));
            }
            Expect<InvalidDataException>(() => store.ReviewScopedPerson(family, branch, reviewer, tampered, true));
            using (var db = Connect(path))
            {
                Execute(db, "UPDATE scoped_person_proposals SET submitter=$p,state='Approved' WHERE id=$id;",
                    ("$p", author.ToString("D")), ("$id", tampered.ToString("D")));
            }
            Expect<InvalidDataException>(() => store.UndoScopedPerson(family, branch, reviewer, tampered));
            using (var db = Connect(path))
            {
                Execute(db, "UPDATE scoped_person_proposals SET state='Pending',living=1-living WHERE id=$id;",
                    ("$id", tampered.ToString("D")));
            }
            Expect<InvalidDataException>(() => store.ReviewScopedPerson(family, branch, reviewer, tampered, false));
            using (var db = Connect(path))
            {
                Execute(db, "UPDATE scoped_person_proposals SET living=1-living WHERE id=$id;",
                    ("$id", tampered.ToString("D")));
            }
            var changedPersonId = Guid.NewGuid();
            var changedProposalId = Guid.NewGuid();
            store.GrantScopedPersonPermission(family, otherBranch, admin, reviewer, Permission.ReviewPerson);
            using (var db = Connect(path))
            {
                Execute(db, "UPDATE scoped_person_proposals SET branch_id=$b WHERE id=$id;",
                    ("$b", otherBranch.ToString("D")), ("$id", tampered.ToString("D")));
                Expect<InvalidDataException>(() => store.ReviewScopedPerson(family, otherBranch, reviewer, tampered, true));
                Execute(db, "UPDATE scoped_person_proposals SET branch_id=$b WHERE id=$id;",
                    ("$b", branch.ToString("D")), ("$id", tampered.ToString("D")));
                using var query = db.CreateCommand();
                query.CommandText = "SELECT person_id,payload FROM scoped_person_proposals WHERE id=$id;";
                query.Parameters.AddWithValue("$id", tampered.ToString("D"));
                using var reader = query.ExecuteReader();
                Check(reader.Read(), "tamper proposal exists");
                var originalPerson = reader.GetString(0);
                var originalPayload = reader.GetFieldValue<byte[]>(1);
                reader.Close();
                Execute(db, "UPDATE scoped_person_proposals SET person_id=$p WHERE id=$id;",
                    ("$p", changedPersonId.ToString("D")), ("$id", tampered.ToString("D")));
                Expect<InvalidDataException>(() => store.ReviewScopedPerson(family, branch, reviewer, tampered, true));
                Execute(db, "UPDATE scoped_person_proposals SET person_id=$p,id=$new WHERE id=$id;",
                    ("$p", originalPerson), ("$new", changedProposalId.ToString("D")),
                    ("$id", tampered.ToString("D")));
                Expect<InvalidDataException>(() => store.ReviewScopedPerson(family, branch, reviewer, changedProposalId, true));
                Execute(db, "UPDATE scoped_person_proposals SET id=$id,payload=X'01' WHERE id=$new;",
                    ("$id", tampered.ToString("D")), ("$new", changedProposalId.ToString("D")));
                Expect<InvalidDataException>(() => store.ReviewScopedPerson(family, branch, reviewer, tampered, true));
                Execute(db, "UPDATE scoped_person_proposals SET payload=$payload WHERE id=$id;",
                    ("$payload", originalPayload), ("$id", tampered.ToString("D")));
            }
            store.ReviewScopedPerson(family, branch, reviewer, tampered, false);
            Check(!store.ReadPeople(family, admin).Any(x => x.DisplayName == "Synthetic Tamper"), "rejection publishes nothing");

            var stale = store.ProposeScopedPerson(family, branch, author, "Synthetic Stale");
            store.RevokeMember(family, admin, author);
            Expect<UnauthorizedAccessException>(() => store.ReviewScopedPerson(family, branch, reviewer, stale, true));
            Join(store, family, admin, author);
            Expect<UnauthorizedAccessException>(() => store.ProposeScopedPerson(family, branch, author, "Synthetic Rejoin"));
            Expect<UnauthorizedAccessException>(() => store.ReviewScopedPerson(family, branch, reviewer, stale, true));
            store.ReviewScopedPerson(family, branch, reviewer, stale, false);
            store.GrantScopedPersonPermission(family, branch, admin, author, Permission.CreatePerson);
            store.GrantScopedPersonPermission(family, branch, admin, reviewer, Permission.CreatePerson);
            for (var i = 0; i < 101; i++)
                store.ProposeScopedPerson(family, branch, reviewer, "Synthetic Own");
            for (var i = 0; i < 101; i++)
                store.ProposeScopedPerson(family, branch, author, "Synthetic Page");
            var firstPage = store.ListScopedPendingPeople(family, branch, reviewer);
            var nextPage = store.ListScopedPendingPeople(family, branch, reviewer, firstPage[^1].ProposalId);
            Check(firstPage.Count == 100 && nextPage.Count == 1 &&
                firstPage.Concat(nextPage).Select(x => x.ProposalId).Distinct().Count() == 101,
                "keyset pagination excludes own proposals before limit");
            store.RevokeScopedPersonPermission(family, branch, admin, author, Permission.CreatePerson);
            using (var reopened = new SqliteFamilyStore(path, key))
            {
                Expect<UnauthorizedAccessException>(() => reopened.ProposeScopedPerson(family, branch, author, "Synthetic Restart"));
                Check(!reopened.ReadPeople(family, admin).Any(x => x.Id == replaced.Id), "restart preserves legacy isolation");
            }
            using (var db = Connect(path))
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COUNT(*) FROM scoped_person_grant_audit
                    WHERE family_id=$f AND branch_id=$b AND recipient_id=$p
                      AND permission='CreatePerson' AND action IN ('granted','membership-changed','revoked');
                    """;
                cmd.Parameters.AddWithValue("$f", family.ToString("D"));
                cmd.Parameters.AddWithValue("$b", branch.ToString("D"));
                cmd.Parameters.AddWithValue("$p", author.ToString("D"));
                Check((long)cmd.ExecuteScalar()! == 4, "grant audit contains branch and permission");
            }
            Console.WriteLine("PASS SQLite scoped person grants, review, undo, isolation and tamper");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Join(SqliteFamilyStore store, Guid family, Guid admin, Guid member)
    {
        var now = DateTimeOffset.UtcNow;
        var invite = store.IssueInvitation(family, admin, member, now.AddHours(1), now);
        store.AcceptInvitation(family, member, invite, now);
        store.ApproveMember(family, admin, member);
    }

    private static SqliteConnection Connect(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
        db.Open();
        return db;
    }

    private static void Execute(SqliteConnection db, string sql, params (string, object)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        Check(cmd.ExecuteNonQuery() == 1, "tamper affected one row");
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new Exception(message);
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}

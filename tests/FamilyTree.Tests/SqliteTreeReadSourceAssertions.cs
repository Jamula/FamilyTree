using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using FamilyTree.Domain;
using FamilyTree.Persistence;
using Microsoft.Data.Sqlite;

internal static class SqliteTreeReadSourceAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var folder = Path.Join(Path.GetTempPath(), "familytree-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Join(folder, "fictional.sqlite");
        var key = RandomNumberGenerator.GetBytes(32);
        var family = Guid.NewGuid();
        var otherFamily = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var member = Guid.NewGuid();
        var pending = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var person = new Person(Guid.NewGuid(), "Fictional Read Person");
        var hidden = new Person(Guid.NewGuid(), "Fictional Hidden Person");
        try
        {
            using var store = new SqliteFamilyStore(path, key);
            store.CreateFictionalFamily(family, admin);
            store.CreateFictionalFamily(otherFamily, Guid.NewGuid());
            Invite(store, family, admin, member, true);
            Invite(store, family, admin, pending, false);
            var proposal = store.ProposePerson(family, member, person);
            store.ReviewPerson(family, admin, proposal, true);
            proposal = store.ProposePerson(family, member, hidden);
            store.ReviewPerson(family, admin, proposal, true);
            var grants = new SqliteGraphGrantStore(path);
            using var source = new SqliteTreeReadSource(path, key);
            var service = new PrivateTreeReadService(source);
            var people = new SqlitePersonReadService(source);
            AuthorizedTreeProjection? Read(Guid viewer, Guid tenant, Guid scope, Guid center) =>
                service.GetAsync(viewer, tenant, scope, center, new TreeOptions()).GetAwaiter().GetResult();
            AuthorizedPersonSummary? Get(Guid viewer, Guid tenant, Guid scope, Guid id) =>
                people.GetAsync(viewer, tenant, scope, id).GetAwaiter().GetResult();
            IReadOnlyList<AuthorizedPersonSummary> Search(Guid viewer, Guid tenant, Guid scope, string? text) =>
                people.SearchAsync(viewer, tenant, scope, text).GetAwaiter().GetResult();

            Check(Read(member, family, branch, person.Id) is null, "membership alone denied");
            Check(Get(member, family, branch, person.Id) is null &&
                Search(member, family, branch, "Fictional").Count == 0,
                "membership alone denies lookup and search");
            Expect<UnauthorizedAccessException>(() => grants.GrantDisplayName(
                family, branch, admin, member, person.Id), "field grant requires person grant");
            grants.GrantPerson(family, branch, admin, member, person.Id);
            grants.GrantPerson(family, branch, admin, member, hidden.Id);
            Check(Read(member, family, branch, person.Id) is null, "person grant alone denied");
            Expect<UnauthorizedAccessException>(() => grants.GrantDisplayName(
                family, branch, member, member, person.Id), "non-admin grant denied");
            grants.GrantDisplayName(family, branch, admin, member, person.Id);
            grants.GrantDisplayName(family, branch, admin, member, person.Id);
            var result = Read(member, family, branch, person.Id);
            Check(result is not null && result.Nodes.Count == 1 &&
                result.Nodes[0].DisplayName == person.DisplayName && result.Edges.Count == 0,
                "explicit grants allow only isolated redacted person");
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false }.ToString()))
            {
                db.Open();
                using var writer = db.BeginTransaction();
                Check(Read(member, family, branch, person.Id)?.Nodes.Single().DisplayName == person.DisplayName,
                    "tree read does not compete for a write reservation");
            }
            Check(Get(member, family, branch, person.Id)?.DisplayName == person.DisplayName &&
                Search(member, family, branch, "fictional").Single().PersonId == person.Id,
                "lookup and search require and use persisted grants");
            Check(Get(member, family, branch, hidden.Id) is null &&
                Get(member, family, branch, Guid.NewGuid()) is null &&
                Get(member, family, branch, Guid.Empty) is null &&
                Search(member, family, branch, "Hidden").Count == 0 &&
                Search(member, family, branch, null).Count == 0 &&
                Search(member, family, branch, "x").Count == 0 &&
                Search(member, family, branch, new string('x', 81)).Count == 0,
                "hidden and unknown lookup and invalid search deny without disclosure");
            Check(Read(member, family, branch, hidden.Id) is null &&
                Read(member, family, branch, Guid.NewGuid()) is null,
                "hidden and unknown centers indistinguishable");
            Check(Read(Guid.Empty, family, branch, person.Id) is null &&
                Read(Guid.NewGuid(), family, branch, person.Id) is null &&
                Read(pending, family, branch, person.Id) is null &&
                Read(member, otherFamily, branch, person.Id) is null &&
                Read(member, family, Guid.NewGuid(), person.Id) is null,
                "anonymous, forged, pending and cross-scope principals denied");
            Check(Get(Guid.NewGuid(), family, branch, person.Id) is null &&
                Get(pending, family, branch, person.Id) is null &&
                Get(member, otherFamily, branch, person.Id) is null &&
                Search(member, family, Guid.NewGuid(), "Fictional").Count == 0,
                "lookup and search deny untrusted, inactive and cross-scope principals");
            using (var reopened = new SqliteTreeReadSource(path, key))
                Check(new PrivateTreeReadService(reopened).GetAsync(member, family, branch,
                    person.Id, new TreeOptions()).GetAwaiter().GetResult() is not null,
                    "grants survive reopening");
            grants.RevokeDisplayName(family, branch, admin, member, person.Id);
            grants.RevokeDisplayName(family, branch, admin, member, person.Id);
            Check(Read(member, family, branch, person.Id) is null, "field revocation denies read");
            grants.GrantDisplayName(family, branch, admin, member, person.Id);
            grants.RevokePerson(family, branch, admin, member, person.Id);
            grants.GrantPerson(family, branch, admin, member, person.Id);
            Check(Read(member, family, branch, person.Id) is null,
                "person revocation removes field grant permanently");
            grants.GrantDisplayName(family, branch, admin, member, person.Id);
            store.RevokeMember(family, admin, member);
            Check(Read(member, family, branch, person.Id) is null, "revoked member denied immediately");
            Check(Get(member, family, branch, person.Id) is null &&
                Search(member, family, branch, "Fictional").Count == 0,
                "revoked member denied lookup and search");
            Invite(store, family, admin, member, true);
            grants.GrantPerson(family, branch, admin, member, person.Id);
            Check(Read(member, family, branch, person.Id) is null,
                "rejoined member does not inherit field permissions");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Expect<OperationCanceledException>(() => source.LoadAsync(member, family, branch,
                person.Id, cancelled.Token).GetAwaiter().GetResult(), "cancelled source denied");
            Expect<OperationCanceledException>(() => people.GetAsync(member, family, branch,
                person.Id, cancelled.Token).GetAwaiter().GetResult(), "cancelled lookup denied");
            Expect<OperationCanceledException>(() => people.SearchAsync(member, family, branch,
                "Fictional", cancelled.Token).GetAwaiter().GetResult(), "cancelled search denied");
            Expect<ArgumentNullException>(() => new SqlitePersonReadService(null!), "source required");
            Check(source.LoadAsync(Guid.Empty, family, branch, person.Id, default)
                .GetAwaiter().GetResult() is null,
                "empty source identity denied");
            Scale(path, key, family, admin, member, branch);
            Console.WriteLine("PASS persisted tree read grants, isolation and revocation");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(folder, true);
        }
    }

    private static void Scale(string path, byte[] key, Guid family, Guid admin, Guid member, Guid branch)
    {
        static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:x12}");
        byte[] Seal(Guid id, string name)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var clear = Encoding.UTF8.GetBytes(name);
            var cipher = new byte[clear.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            byte[] aad = [..family.ToByteArray(), ..id.ToByteArray()];
            aes.Encrypt(nonce, clear, cipher, tag, aad);
            return [..nonce, ..tag, ..cipher];
        }

        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, ForeignKeys = true, Pooling = false }.ToString()))
        {
            db.Open();
            using var tx = db.BeginTransaction();
            using var insert = db.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO people(family_id,id,encrypted_name,living) VALUES($f,$id,$name,0);
                INSERT INTO person_visibility VALUES($f,$b,$p,$id);
                INSERT INTO person_display_name_visibility VALUES($f,$b,$p,$id);
                """;
            insert.Parameters.AddWithValue("$f", family.ToString("D"));
            insert.Parameters.AddWithValue("$b", branch.ToString("D"));
            insert.Parameters.AddWithValue("$p", member.ToString("D"));
            var idParameter = insert.Parameters.Add("$id", SqliteType.Text);
            var nameParameter = insert.Parameters.Add("$name", SqliteType.Blob);
            for (var i = 1; i <= 10020; i++)
            {
                var id = Id(i);
                idParameter.Value = id.ToString("D");
                nameParameter.Value = i == 10020 ? new byte[] { 0 } :
                    Seal(id, i == 10019 ? "Fictional Needle" : "Fictional Common");
                insert.ExecuteNonQuery();
            }
            tx.Commit();
        }
        using var source = new SqliteTreeReadSource(path, key);
        var people = new SqlitePersonReadService(source);
        var trees = new PrivateTreeReadService(source);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open();
            using var writer = db.BeginTransaction();
            Check(people.GetAsync(member, family, branch, Id(1)).GetAwaiter().GetResult() is not null &&
                people.SearchAsync(member, family, branch, "common").GetAwaiter().GetResult().Count == 25 &&
                trees.GetAsync(member, family, branch, Id(1), new TreeOptions())
                    .GetAwaiter().GetResult()?.Nodes.Single().DisplayName == "Fictional Common",
                "read transactions do not compete for a write reservation");
        }
        Check(trees.GetAsync(member, family, branch, Id(10019), new TreeOptions())
            .GetAwaiter().GetResult()?.Nodes.Single().DisplayName == "Fictional Needle",
            "center-aware tree reads work beyond the former branch snapshot limit");
        Check(people.GetAsync(member, family, branch, Id(10019)).GetAwaiter().GetResult()?.DisplayName
            == "Fictional Needle", "targeted lookup does not decrypt unrelated corrupt records");
        var first = people.SearchAsync(member, family, branch, "common").GetAwaiter().GetResult();
        Check(first.Count == 25 && first[0].PersonId == Id(1) && first[24].PersonId == Id(25),
            "search stops after 25 matches without decrypting the rest");
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open();
            using var update = db.CreateCommand();
            update.CommandText = "UPDATE people SET encrypted_name=$name WHERE family_id=$f AND id=$id;";
            update.Parameters.AddWithValue("$name", Seal(Id(10020), "Fictional Last"));
            update.Parameters.AddWithValue("$f", family.ToString("D"));
            update.Parameters.AddWithValue("$id", Id(10020).ToString("D"));
            update.ExecuteNonQuery();
        }
        Check(people.SearchAsync(member, family, branch, "needle").GetAwaiter().GetResult()
            .Single().PersonId == Id(10019), "search reaches matches beyond tree limit");
        Check(people.SearchAsync(member, family, branch, "not present").GetAwaiter().GetResult().Count == 0,
            "no-match search traverses every authorized batch");
        var grants = new SqliteGraphGrantStore(path);
        grants.RevokeDisplayName(family, branch, admin, member, Id(10019));
        grants.RevokeDisplayName(family, branch, admin, member, Id(10020));
        Check(people.GetAsync(member, family, branch, Id(10019)).GetAwaiter().GetResult() is null &&
            people.GetAsync(member, family, branch, Guid.NewGuid()).GetAwaiter().GetResult() is null &&
            people.SearchAsync(member, family, branch, "needle").GetAwaiter().GetResult().Count == 0 &&
            trees.GetAsync(member, family, branch, Id(10020), new TreeOptions())
                .GetAwaiter().GetResult() is null &&
            trees.GetAsync(member, family, branch, Id(20000), new TreeOptions())
                .GetAwaiter().GetResult() is null,
            "revoked field and unknown person remain indistinguishable at scale");
    }

    private static void Invite(SqliteFamilyStore store, Guid family, Guid admin, Guid member, bool approve)
    {
        var now = DateTimeOffset.UtcNow;
        var invitation = store.IssueInvitation(family, admin, member, now.AddDays(1), now);
        store.AcceptInvitation(family, member, invitation, now);
        if (approve) store.ApproveMember(family, admin, member);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Expect<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException(message);
    }
}

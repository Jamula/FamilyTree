using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FamilyTree.Domain;

internal static class EncryptedSnapshotAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var path=Path.Join(Path.GetTempPath(),"ft-synthetic-"+Guid.NewGuid().ToString("N"),
            "local.snapshot");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var key=RandomNumberGenerator.GetBytes(32);
            var parent=new Person(Guid.NewGuid(),"Fictional Parent");
            var child=new Person(Guid.NewGuid(),"Fictional Child");
            var relation=Relationship.Create(parent.Id,child.Id,RelationKind.Parent);
            var data=new LocalFamilySnapshot(Guid.NewGuid(),7,[parent,child],[relation]);
            using(var store=new EncryptedLocalSnapshotStore(path,key))
            {
                var missingFileRejected = false;
                try { store.LoadAsync().GetAwaiter().GetResult(); }
                catch(FileNotFoundException) { missingFileRejected = true; }
                Check(missingFileRejected, "missing local snapshot rejected");
                store.SaveAsync(data).GetAwaiter().GetResult();
                var ciphertext=File.ReadAllBytes(path);
                Check(!System.Text.Encoding.UTF8.GetString(ciphertext)
                    .Contains(parent.DisplayName,StringComparison.Ordinal),
                    "no plaintext persisted");
                var loaded=store.LoadAsync().GetAwaiter().GetResult();
                Check(loaded.Revision==7 && loaded.People.Count==2 &&
                    loaded.Relationships.Count==1,"encrypted save/load roundtrip");
                store.SaveAsync(data with { Revision=8 }).GetAwaiter().GetResult();
                Check(store.LoadAsync().GetAwaiter().GetResult().Revision==8,
                    "atomic snapshot replacement");
                ciphertext=File.ReadAllBytes(path);
                ciphertext[^1]^=0x40;
                File.WriteAllBytes(path,ciphertext);
                Expect<InvalidDataException>(()=>store.LoadAsync().GetAwaiter().GetResult(),
                    "tampered authenticated envelope");
                store.SaveAsync(data).GetAwaiter().GetResult();
                using(var other=new EncryptedLocalSnapshotStore(path,RandomNumberGenerator.GetBytes(32)))
                    Expect<InvalidDataException>(()=>other.LoadAsync().GetAwaiter().GetResult(),
                        "wrong encryption key");
                Expect<InvalidDataException>(()=>store.SaveAsync(
                    data with { People=[parent,parent] }).GetAwaiter().GetResult(),
                    "duplicate people rejected");
                Expect<InvalidDataException>(()=>store.SaveAsync(
                    data with { Relationships=[Relationship.Create(parent.Id,Guid.NewGuid(),
                        RelationKind.Parent)] }).GetAwaiter().GetResult(),
                    "orphan link rejected");
                Expect<OperationCanceledException>(()=>store.SaveAsync(data,
                    new CancellationToken(true)).GetAwaiter().GetResult(),
                    "cancellation propagates");
                store.Dispose();
                Expect<ObjectDisposedException>(()=>store.LoadAsync().GetAwaiter().GetResult(),
                    "disposed store inaccessible");
            }
            Expect<ArgumentException>(()=>new EncryptedLocalSnapshotStore(
                path,new byte[5]),"invalid key rejected");
            Console.WriteLine("PASS encrypted local synthetic snapshot and tamper/error tests");
        }
        finally
        {
            var dir=Path.GetDirectoryName(path)!;
            if(Directory.Exists(dir))Directory.Delete(dir,recursive:true);
        }
    }
    static void Check(bool yes,string reason)
    {if(!yes)throw new InvalidOperationException(reason);}
    static void Expect<T>(Action action,string reason) where T:Exception
    {try{action();}catch(T){return;}throw new InvalidOperationException(reason);}
}

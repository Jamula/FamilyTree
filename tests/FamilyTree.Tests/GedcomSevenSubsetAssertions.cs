using System.Runtime.CompilerServices;
using FamilyTree.Domain;

internal static class GedcomSevenSubsetAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var path=Path.Join(AppContext.BaseDirectory,"fixtures","synthetic-family.ged.txt");
        var fixture=File.ReadAllText(path);
        var parsed=GedcomSevenSubset.Preview(fixture);
        Check(parsed.Valid && parsed.People.Count==3 && parsed.Families.Count==1,
            "valid synthetic GEDCOM 7 fixture preview");
        Check(parsed.Families[0].ParentPointers.Count==2 &&
              parsed.Families[0].ChildPointers.Count==1,
            "both family parent records retained");
        Check(parsed.People.Any(x=>x.DisplayName=="Alex Example"),
            "name slash syntax normalized");
        Check(!GedcomSevenSubset.Preview(null).Valid &&
              !GedcomSevenSubset.Preview("0 TRLR").Valid,
              "malformed data rejected without crashing");
        Check(!GedcomSevenSubset.Preview(fixture.Replace("7.0","5.5.1",
            StringComparison.Ordinal)).Valid, "5.5.1 not silently misparsed as 7");
        Check(!GedcomSevenSubset.Preview(fixture.Replace("Alex /Example/","",
            StringComparison.Ordinal)).Valid, "empty name rejected");
        var viewer=Guid.NewGuid();var family=Guid.NewGuid();
        var parent=new Person(Guid.NewGuid(),"Fictional Parent");
        var child=new Person(Guid.NewGuid(),"Fictional Child");
        var hidden=new Person(Guid.NewGuid(),"SECRET FAMILY");
        var edge=Relationship.Create(parent.Id,child.Id,RelationKind.Parent);
        var secretEdge=Relationship.Create(parent.Id,hidden.Id,RelationKind.Parent);
        var auth=new MembershipScopedAuthorization(family,[
            new ScopedFamilyGrant(viewer,family,MembershipState.Active,
                new HashSet<Guid>{parent.Id,child.Id},
                new HashSet<Guid>{edge.Id,secretEdge.Id})
        ]);
        var output=GedcomSevenSubset.ExportAuthorizedPreviewAsync(viewer,
            [parent,child,hidden],[edge,secretEdge],auth).GetAwaiter().GetResult();
        Check(!output.Contains("SECRET",StringComparison.Ordinal) &&
              !output.Contains(hidden.Id.ToString(),StringComparison.Ordinal),
              "export does not leak hidden people or IDs");
        var roundtrip=GedcomSevenSubset.Preview(output);
        Check(roundtrip.Valid && roundtrip.People.Count==2 &&
              roundtrip.Families.Count==1,
              "authorized synthetic subset round-trip");
        // The independent relationship gate must deny a family edge even if
        // both people have an explicit person-visibility grant.
        var noEdgeAuthorization = new MembershipScopedAuthorization(family,[
            new ScopedFamilyGrant(viewer,family,MembershipState.Active,
                new HashSet<Guid>{parent.Id,child.Id},
                new HashSet<Guid>())
        ]);
        var onlyPeople = GedcomSevenSubset.ExportAuthorizedPreviewAsync(viewer,
            [parent,child],[edge],noEdgeAuthorization).GetAwaiter().GetResult();
        var onlyPeoplePreview=GedcomSevenSubset.Preview(onlyPeople);
        Check(onlyPeoplePreview.Valid && onlyPeoplePreview.People.Count==2 &&
              onlyPeoplePreview.Families.Count==0,
              "relationship denied independently of visible endpoints");
        var denied=GedcomSevenSubset.ExportAuthorizedPreviewAsync(Guid.NewGuid(),
            [parent,child,hidden],[edge,secretEdge],auth).GetAwaiter().GetResult();
        Check(GedcomSevenSubset.Preview(denied).Valid &&
              GedcomSevenSubset.Preview(denied).People.Count==0,
            "unknown viewer gets no individual records");
        var bad=new Person(Guid.NewGuid(),"unsafe\nNAME");
        var badAuth=new MembershipScopedAuthorization(family,[
            new ScopedFamilyGrant(viewer,family,MembershipState.Active,
                new HashSet<Guid>{bad.Id},new HashSet<Guid>())
        ]);
        var blockedUnsafeName=false;
        try
        {
            GedcomSevenSubset.ExportAuthorizedPreviewAsync(viewer,[bad],[],badAuth)
                .GetAwaiter().GetResult();
        }
        catch(InvalidOperationException ex) when (ex.Message=="A display name cannot be safely exported.")
        {blockedUnsafeName=true;}
        Check(blockedUnsafeName,"unsafe name rejected");
        foreach (var invalid in new[] {"///", "Fictional\tPerson"}
            .Select(name => new Person(Guid.NewGuid(), name)))
        {
            var scope = new MembershipScopedAuthorization(family,[
                new ScopedFamilyGrant(viewer,family,MembershipState.Active,
                    new HashSet<Guid>{invalid.Id},new HashSet<Guid>())
            ]);
            var rejected=false;
            try
            {
                GedcomSevenSubset.ExportAuthorizedPreviewAsync(viewer,[invalid],[],scope)
                    .GetAwaiter().GetResult();
            }
            catch(InvalidOperationException ex) when (ex.Message=="A display name cannot be safely exported.")
            {rejected=true;}
            Check(rejected,"invalid canonical GEDCOM name rejected");
        }
        Console.WriteLine("PASS bounded GEDCOM preview, auth export, round trip and error tests");
    }
    static void Check(bool value,string why)
    {if(!value)throw new InvalidOperationException(why);}
}

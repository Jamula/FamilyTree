using FamilyTree.Domain;

var assertions = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); assertions++; }
var defaults = new TreeOptions();
Check(defaults.GenerationsAbove == 4 && defaults.GenerationsBelow == 4, "default depths");
Check(defaults.Spouses && defaults.SiblingBranches && !defaults.SocialConnections, "default toggles");
Check(new TreeOptions(0, 8).Validate().GenerationsBelow == 8, "bounds");
try { new TreeOptions(9, 1).Validate(); throw new Exception("invalid depth accepted"); }
catch (ArgumentOutOfRangeException) { assertions++; }
var a = Guid.NewGuid(); var b = Guid.NewGuid();
var first = Relationship.Create(a,b,RelationKind.Friend);
var second = Relationship.Create(a,b,RelationKind.Neighbor);
Check(first.Id != second.Id, "multiple independent relationships");
try { Relationship.Create(a,a,RelationKind.Parent); throw new Exception("self relationship accepted"); }
catch (ArgumentException) { assertions++; }
try { Relationship.Create(a,b,RelationKind.Other); throw new Exception("unlabeled other relationship accepted"); }
catch (ArgumentException) { assertions++; }
var gratitude = new Gratitude(Guid.NewGuid(),a,b,"Thank you");
Check(gratitude.Visibility == Visibility.PersonAndRecipient,"gratitude private default");
var unjoined = new Person(Guid.NewGuid(),"Sample Person");
Check(unjoined.Id != Guid.Empty, "unjoined person can exist");
Console.WriteLine($"PASS {assertions} domain assertions");
GedcomFixtureAssertions.Run();
TelemetryAssertions.Run();
await TreeProjectionAssertions.RunAsync();

SyntheticDemoAssertions.Run();

await MembershipAuthorizationAssertions.RunAsync();

// Run async HTTP integration checks outside module initializers to avoid
// CLR type-initialization locks blocking the request continuations.
await FictionalWorkbenchHttpAssertions.RunAsync();

// Keep HTTP integration off module initializers to avoid CLR startup locks.
await ApiLoopbackIntegrationAssertions.RunAsync();

await VerifiedCookieHandlerAssertions.RunAsync();
await SqliteInvitationRaceAssertions.RunAsync();

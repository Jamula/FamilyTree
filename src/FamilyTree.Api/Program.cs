using FamilyTree.Api;

// The same composition root is exercised by loopback integration tests.
ApiApplication.Build(WebApplication.CreateBuilder(args)).Run();

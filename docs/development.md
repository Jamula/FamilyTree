# Local development (.NET 11)

The repository targets the **.NET 11 GA SDK** and `net11.0` in every app and test project.

## Prerequisites
- Install the free [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0).
- Git, a terminal, and optionally VS Code or Visual Studio supporting .NET 11.
- No Azure subscription, Cloudflare account, Docker, or paid AI service is needed to build or test this foundation.

## Build and run
```bash
dotnet --version
dotnet restore FamilyTree.slnx
dotnet build FamilyTree.slnx -c Release
dotnet run --project tests/FamilyTree.Tests/FamilyTree.Tests.csproj -c Release
dotnet run --project src/FamilyTree.Web/FamilyTree.Web.csproj
```
For the development API, use `dotnet run --project src/FamilyTree.Api/FamilyTree.Api.csproj` in a second terminal.

**Important:** The web page is only a landing page, not an authenticated genealogy app. The API deliberately returns 501 for people/tree endpoints until access control is implemented. Do not run the prototype with private family data.

## CI and cost
PR checks use GitHub's standard hosted runners for this public repository; GitHub documents those runners as free for public repositories. The workflow generates coverage without uploading artifact storage or enabling usage-billed AI Code Quality. Do not enable paid services or deployments without explicit owner approval. See [cost_policy.md](cost_policy.md).

## Local orchestration
Aspire/AppHost and OpenTelemetry service defaults are tracked separately in #23 and #22 and must preserve optional, no-cost local operation.

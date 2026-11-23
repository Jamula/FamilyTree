# FamilyTree local MCP agent playground (#44, #45, #46)

The project includes an official Model Context Protocol **C# SDK 2.4.0**, `.NET 11` local **stdio** server, and a completely fictional 32-person, 5-generation extended family.

**This is only a synthetic demo. The repository never contains actual Jamula family records or private household details.** The agent tools cannot query the production API, filesystem, GitHub emails, chats or any real-person store. A tool proposal is kept only in process memory and is never approved or saved to genealogy data.

## Start locally

Install the .NET 11 SDK, clone this repository and from the repository root:

```sh
dotnet restore FamilyTree.slnx
dotnet build FamilyTree.slnx --configuration Release
dotnet run --project src/FamilyTree.Mcp/FamilyTree.Mcp.csproj --configuration Release
```

The terminal waits for MCP JSON-RPC messages on **stdin** and sends MCP responses on **stdout**. Normal output is not a CLI prompt. Stdio diagnostics use **stderr**, not stdout, to avoid corrupting the MCP channel. The process does not listen on an HTTP port or require Docker, Azure, Cloudflare, any account or model API key.

## GitHub Copilot / VS Code

The repository contains workspace-root `.mcp.json` in the new portable Copilot configuration format. Open the repository in VS Code, select **Chat: Open Customizations → MCP Servers**, and allow the `familytree-fictional` server. Ensure the Copilot session has access to the .NET 11 SDK and runs commands from the repository root. This config is for local code and fictional data, not a hosted MCP service. See [VS Code MCP config](https://code.visualstudio.com/docs/agents/reference/mcp-configuration).

## Tools

| Tool | Behavior |
|---|---|
| `FamilyOverview` | Report fictional dataset size, read-only scope and draft limits |
| `ListDemoPeople` | List fictional people with stable GUIDs |
| `SearchDemoPeople` | Case-insensitive fictional name search |
| `InspectDemoPerson` | Show a fictional person and their fictional relations |
| `ListDemoRelationships` | List typed and labeled fictional connections |
| `ProposeDemoPerson` | Stage fictional new-person draft in memory (not approved) |
| `ProposeDemoRelationship` | Stage link draft between existing fictional people |
| `ListDemoDrafts` | Read in-process drafts (lost when server exits) |

Try: **"Using familytree-fictional tools, describe the fictional five-generation family and propose two new characters with relationships. Do not publish anything."**

## CI and safety

- `scripts/smoke-mcp.py` exercises MCP initialization, discovery and a read tool over stdio on the standard public GitHub Actions runner.
- `SyntheticDemoAssertions` validates stable fictional IDs, relationship endpoints, no self-links, a disconnected person, step relationships and the five-generation chain.
- Proposals are explicitly **pending**, unapproved and ephemeral. No tool can approve, persist, delete or export actual genealogy data.
- Stdio does not make this localhost service accessible from the ChatGPT website or a remote agent automatically. ChatGPT's supported connected app/remote MCP workflow would require a separate, authenticated connector, hosting/privacy review and cost approval. Never expose the stdio app as a public anonymous endpoint.
- MCP tooling is not an identity/authorization solution. Integrate with scoped membership and approvals in #9, #10, #12 and #46 before attempting to use actual family records.
- Product version remains **0.1.0 until the MVP is approved**. Hosting stays at $0 incremental; GitHub engineering spend is accounted for across projects.

## Scope tracking

Parent gateway work: [#44](https://github.com/Jamula/FamilyTree/issues/44); fictional dataset: [#45](https://github.com/Jamula/FamilyTree/issues/45); approved real-family write access, remote ChatGPT connections and privacy controls: [#46](https://github.com/Jamula/FamilyTree/issues/46).

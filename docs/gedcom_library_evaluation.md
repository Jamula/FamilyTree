# M02 GEDCOM library selection and preview boundary

## Candidate library review (November 2026)

Official GEDCOM7 tools directory lists the MIT-licensed C# GEDCOM 7 parser/
validator at https://github.com/ArmidaleSoftware/gedcom7 . That is the leading
candidate for a maintained standards-aware adapter, subject to checks for
current maintenance, license, compatibility with .NET 11, resource bounds,
malformed-input security and round-trip conformance. The existing
Gedcom.Net.SDK NuGet package (https://www.nuget.org/packages/Gedcom.Net.SDK)
is another candidate but GEDCOM 7 conformance has not yet been established.

Official source: https://gedcom.io/tools/
FamilySearch GEDCOM v7: https://gedcom.io/specifications/FamilySearchGEDCOMv7.html

## Current prototype

`GedcomSevenSubset` is a tiny **synthetic-only** regression boundary built
to make the end-to-end project skeleton executable before selecting a
full standards adapter. `Preview` extracts individual names, raw birth/death
date strings, and spouse/child family pointer relationships after
bounded structural preflight. It does not import any private family data,
preserve all source citations, parse all events or perform GEDZIP validation.

`ExportAuthorizedPreviewAsync` checks every person and relationship against
server-trusted authorization. It generates a minimal Family record per visible
parent-child edge. Such output is **not a faithful complex family export**:
multi-parent families, partnerships, social connections, provenance,
citations, events, attachments, dates, privacy sidecar and true round-trip
semantics are not represented. Do not use it for backup or place it behind a
family-data HTTP route. The API remains 501 for real data.

Remaining work before #13 completion:
1. Vet and integrate a maintained GEDCOM 7 parser and validator.
2. Build complete preview with warnings, events, dates, citations, gender/
   adoption/family semantics and duplicate reconciliation.
3. Require moderator-approved import, policy-filtered export and a versioned
   sidecar preserving all app-specific fields.
4. Test extensive synthetic GEDCOM7/5.5.1 corpora with an independent tool.
5. Conformance/size/performance/fuzz tests and privacy leak regression suite.

# M1 synthetic GEDCOM 7 slice (issue #13)

This work is a bounded synthetic preview, not full GEDCOM interoperability
or an approved private-data workflow. Issue #13 and the integration acceptance
in #90 remain open. No private API route or persistence is enabled by it.

## Library evaluation

The [GEDCOM tools directory](https://gedcom.io/tools/) identifies
[ArmidaleSoftware/gedcom7](https://github.com/ArmidaleSoftware/gedcom7), an
MIT-licensed C# parser and GEDCOM 7 validator, as a candidate. Its
[README](https://github.com/ArmidaleSoftware/gedcom7/blob/main/README.md)
documents in-memory parsing, schema validation and comparison; its
[library project](https://github.com/ArmidaleSoftware/gedcom7/blob/main/Gedcom7/Gedcom7.csproj)
targets .NET 8. As of November 2026 its
[v1.6.2 release](https://github.com/ArmidaleSoftware/gedcom7/releases/tag/v1.6.2)
was published in August 2026, and the repository had November 2026 maintenance
commits. This is evidence of activity, not a guarantee of continued support.

[GedcomSharp](https://github.com/gordon-matt/GedcomSharp) is another
MIT-licensed C# candidate targeting .NET 10. Its README claims typed records
and 5.5.1/7.0 reading and writing, but it is a newer project; its conformance,
distribution and maintenance longevity require independent verification.
[Gedcom.Net.SDK](https://www.nuget.org/packages/Gedcom.Net.SDK) was also
previously identified, but its GEDCOM 7 coverage and current maintenance
could not be verified here. None of these candidates has been integrated
or independently tested for .NET 11, bounded resource use, cancellation,
malformed-input behavior or round-trip conformance in this repository. A
standards-aware adapter must be vetted on those points before production use.
The synthetic subset is deliberately not a replacement for that adapter or
an assertion of GEDCOM 7 conformance.

## Boundary

The preview creates an in-memory proposal and duplicate/conflict candidates;
it does not approve or apply changes. Its supported subset is deliberately
limited to individuals, family links and roles, birth/death event date text
and source citations. Other structures are rejected except `SEX`, which is
accepted but not preserved. Dates remain text, not interpreted precision;
the proposal warns that the subset is lossy.

The export accepts only an already-authorized synthetic projection. The
caller must independently authorize every included record, link and field,
including dates and sources; wrapping a full graph does not authorize it.
Invalid references fail without returning a partial file. This is not a
privacy filter or a substitute for an active membership check. Export warns
that application-specific and unsupported semantics are not preserved.

## Remaining gates

Verified identity and persisted membership (#9), privacy and revocation (#10),
durable reviewed edits (#12), and the complete local acceptance contract (#90)
must precede real-data integration. Independent GEDCOM 7/5.5.1 conformance,
complex family and adoption semantics, media, lossless application-specific
sidecars, external round-trips and privacy regression tests remain open. Do not
deploy a private GEDCOM endpoint or treat this slice as a backup.

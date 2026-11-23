# GEDCOM 7 interoperability contract (#13)

FamilyTree uses the **FamilySearch GEDCOM 7** standard as its primary export/import interchange boundary. We do **not** create a proprietary replacement for GEDCOM.

References:
- Official published standard: https://gedcom.io/specifications/FamilySearchGEDCOMv7.html
- Official FamilySearch GitHub reference repository: https://github.com/FamilySearch/GEDCOM
- Tools and validation guidance: https://gedcom.io/tools/
- Previous GEDCOM 5.5.1 spec: https://gedcom.io/specs/

## Safe fixtures
`tests/fixtures/gedcom7/synthetic-family.ged.txt` is a tiny **fully fictional** record set. Copy it to a `.ged` filename when testing with an external standard validator. Keeping `.ged.txt` in git minimizes accidental inclusion of personal GEDCOM exports that are ignored by `.gitignore`.

## Design commitments
1. Evaluate a maintained GEDCOM 7 parser/serializer (with license and .NET 11 compatibility) before implementing any custom parser.
2. Import into a *preview*, not directly into the private tree. Retain source citations, unknown/approximate dates, pointer identities, events, provenance and validation warnings.
3. Verify duplicates and merge decisions through the same administrator approval workflow as other family changes.
4. Export only records the signed-in member is permitted to see; never leak hidden people by pointers, file names, notes, association records, counts or media.
5. Family/social associations use GEDCOM `ASSO` where semantically representable, but unsupported details (membership consent, gratitude, approvals, multiple distinct social links) require a namespaced sidecar for complete lossless backup. Do not misrepresent them as `FAM` ancestry.
6. A complete backup is GEDCOM 7 + versioned sidecar JSON + permitted media archive. The GEDCOM-only export may lose application-specific semantics and must provide a clear warning.
7. GEDZIP support is a later phase; never ingest archives without zip-slip, decompression bomb, virus scanning and media permissions testing.
8. Test against independent tools (e.g., Gramps and webtrees) using a **synthetic** corpus.

## Import and export test inventory (implementation pending)
| ID | Fixture / scenario | Expected property |
|---|---|---|
| G01 | 7.0 minimal parents + child | Record and family links preserved |
| G02 | Unknown, approximate and multiple-calendar dates | No invented precision |
| G03 | Multiple marriages, adoption and blended family | Family roles preserved |
| G04 | `ASSO` friendship and mentor | Social links not treated as ancestors |
| G05 | Private person / relationship / source | Forbidden records absent in export |
| G06 | Duplicate individuals in source files | Human confirmation required |
| G07 | GEDCOM 5.5.1 input | Compatibility warnings and correct conversion |
| G08 | Round-trip with a second application | No silent loss without a report |
| G09 | Malformed pointers and excessive nesting | Explicit bounded failure |
| G10 | Media and external file paths | No unsafe reading or network fetches |

**Current scope:** synthetic fixture and minimal file smoke assertions. This PR does **not** implement import/export, pass the official GEDCOM conformance suite, or authorize any family-data endpoints.

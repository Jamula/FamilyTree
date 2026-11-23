## GEDCOM 5.5.1 UTF-8 compatibility preview

`GedcomLegacyCompatibility.Preview` performs a strictly bounded **inspection-only** upgrade of the version header for simple 5.5.1 UTF-8 files, then runs the existing 7.0 preflight/preview parser. It explicitly refuses ANSI/unknown encodings, duplicate version declarations, malformed references, oversized files and unsupported versions. It never writes a database, touches a private family, or silently claims lossless conversion.

The unit suite includes a synthetic 5.5.1 round-trip-style preview and hostile inputs; this is not full independent GEDCOM 5.5.1 conformance, duplicate reconciliation, source/date semantics, or server-side authorized import/export. #13 remains open until that full acceptance and #90 complete.

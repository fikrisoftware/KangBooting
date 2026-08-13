# Test ISO Fixture

`IsoInspectorTests` builds a synthetic ISO in-memory using `DiscUtils.Iso9660.CDBuilder`
at test setup time rather than shipping a binary .iso file. This keeps the repo
free of large binary fixtures and makes the exact byte layout (file sizes, presence
of boot files) explicit and easy to vary per test case.

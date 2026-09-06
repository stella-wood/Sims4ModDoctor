# Controlled duplicate fixture

This fixture contains plain text files with Mod-like extensions. It validates whole-file duplicate scanning only; it is not DBPF input.

- `Mods/家具/椅子.package` and `Downloads/椅子-copy.package` are byte-for-byte identical.
- `Downloads/not-duplicate.package` has the same byte length but different content.
- `Downloads/ignored.txt` is excluded when the CLI is limited to `.package`.

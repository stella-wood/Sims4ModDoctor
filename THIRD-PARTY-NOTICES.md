# Third-party notices

The production projects currently use the .NET shared framework and do not
reference third-party runtime NuGet packages.

## Test-only dependencies

- `MSTest` 4.3.3 — MIT — https://github.com/microsoft/testfx
- `Microsoft.NET.Test.Sdk` 18.9.0 — MIT — https://github.com/microsoft/vstest

These packages are used only to build and run the automated test project; they are not runtime dependencies of the duplicate scanner or CLI.

# Contributing

Thanks for helping improve PowerRecover.

## Good First Areas

- Improve previews for more file types.
- Add focused tests for scanners and recovery policies.
- Improve wording in the UI.
- Improve documentation and screenshots.
- Add safe sample files to the test kit.

## Development Setup

1. Install .NET 8 SDK.
2. Install Visual Studio 2022 with .NET desktop development, or use the CLI.
3. Build the solution:

```powershell
dotnet build .\PowerRecover.sln -c Release
```

4. Run tests:

```powershell
dotnet test .\PowerRecover.Tests\PowerRecover.Tests.csproj -c Release
```

## Rules For Recovery Code

- Never write to the source drive during scanning.
- Prefer clear, boring code over clever code.
- Add tests for scanner behavior and file-quality filtering.
- Do not add personal recovery samples, customer files, or real private data.
- Do not commit EXE builds, release ZIPs, or bootable ISO files.

## Pull Request Checklist

- The app builds.
- Tests pass.
- New behavior is documented.
- No local paths, private files, or generated binaries are committed.
- Recovery output goes to a different destination than the source.

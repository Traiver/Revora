# Contributing

## Build

Windows x64, Visual Studio 2022 with .NET desktop development, and .NET SDK 8.0.414 or later within .NET 8 are required. `global.json` excludes preview SDKs. Open `Revora.sln` and select **Release / x64**, or run from the repository root:

```text
dotnet build Revora.sln -c Release -p:Platform=x64
dotnet publish src/Revora/Revora.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/publish
```

The self-contained executable is `artifacts/publish/Revora.exe`. Distribute it with `licenses/`, `LICENSE` and `THIRD-PARTY-NOTICES.md`. The regular build requires the .NET 8 desktop runtime; the self-contained publish does not. Do not trim WPF resources. The runtime version is pinned to 8.0.33 in the project file.

## Validation

```text
artifacts/publish/Revora.exe --self-test --report TESTREPORT.json
```

The native EXE requires administrator privileges. For local logic checks without elevation, build first and run `dotnet src/Revora/bin/x64/Release/net8.0-windows/win-x64/Revora.dll --self-test --report TESTREPORT.json`. The tests verify the embedded administrator manifest and use an unconfirmed worker fixture. Use a writable temporary report path if Windows protects your working directory. The self-test uses simulated WinGet, temporary settings and startup links and in-memory scheduled-task fixtures; it never installs programs or registers real startup tasks. Exit code 0 means success. The optional `--scan-test --report SCANREPORT.json` runs a real read-only inventory scan and reports counts, not the program list.

## Changes

Please preserve:

- Windows x64, automatic scanning on launch, and no preselected updates.
- Explicit consent before package/source agreements, downloads, execution and installer migrations.
- No shell command composition from package IDs, URLs or filenames. Use `ProcessStartInfo.ArgumentList`.
- No PowerShell, batch or command script files and no runtime script-shell invocations.
- Configuration compatibility and atomic saves with backups.
- User choice for multiple versions, downloads and folders.
- Silent background scans, deduplicated notifications and per-program reminder settings.
- No horizontal scrolling at the supported minimum window size.

Add meaningful regression cases to SelfTests for behavior that could affect installed software. Test actual installers only in a disposable VM. Remove private download tokens, local paths and program lists from reports before sharing them.

Commit source files only. Executables, runtime licenses copied during publishing, build output, logs and personal settings stay outside Git. Historical identifiers in `LegacyCompatibility.cs` intentionally preserve existing profiles and startup links. The opt-in background startup uses a per-user Task Scheduler logon task with the highest available privileges and an interactive token. It never stores a password or runs as SYSTEM. Existing owned startup links are removed only after the task is registered and verified. Disabled or foreign entries remain protected.

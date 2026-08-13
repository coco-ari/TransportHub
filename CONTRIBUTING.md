# Contributing

Thank you for helping improve TransportHub.

## Development setup

You need Windows 10/11, PowerShell 5.1 or later, Visual Studio 2022 or later Build Tools
with the C# compiler, and the .NET Framework 4.8 Developer Pack.

Building the distributable installer additionally requires Inno Setup 6.7.3.

Build and run the self-tests from the repository root:

```powershell
& .\scripts\build-desktop.ps1 -Configuration Release

$log = Join-Path $env:TEMP 'TransportHub-self-test.log'
$env:TRANSPORTHUB_SELF_TEST_LOG = $log
$process = Start-Process `
  -FilePath .\artifacts\TransportHub.Desktop\TransportHub.exe `
  -ArgumentList '--self-test' `
  -Wait -PassThru
Get-Content -LiteralPath $log
if ($process.ExitCode -ne 0) { throw "Self-tests failed: $($process.ExitCode)" }
```

Build the one-click Windows installer with:

```powershell
& .\scripts\package-release.ps1
```

## Pull requests

- Keep changes focused and explain the user-visible behavior.
- Add or update self-tests for protocol, transfer, or path-safety changes.
- Test at 100% and 200% display scaling for UI changes when possible.
- Do not commit anything from `artifacts/`.
- Do not commit Syncthing configuration, API keys, certificates, private keys,
  device IDs, synchronized user data, or personal absolute paths.
- Preserve compatibility with Windows PowerShell 5.1 unless a change explicitly
  documents a new minimum version.

## Reporting problems

Use a GitHub issue for ordinary bugs and feature requests. Use private
vulnerability reporting for security issues; see [SECURITY.md](SECURITY.md).

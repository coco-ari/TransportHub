#Requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-NormalizedPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path))
}

function Test-PathIsInside {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Candidate,

        [Parameter(Mandatory)]
        [string] $Parent
    )

    $normalizedCandidate = Resolve-NormalizedPath -Path $Candidate
    $normalizedParent = (Resolve-NormalizedPath -Path $Parent).TrimEnd([char[]] @(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar
        ))
    $parentPrefix = $normalizedParent + [IO.Path]::DirectorySeparatorChar

    return $normalizedCandidate.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase)
}

function Resolve-RelativeInstallPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RelativePath,

        [Parameter(Mandatory)]
        [string] $InstallRoot
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) {
        throw "Invalid installed-file path: '$RelativePath'."
    }

    $resolvedPath = Resolve-NormalizedPath -Path (Join-Path -Path $InstallRoot -ChildPath $RelativePath)
    if (-not (Test-PathIsInside -Candidate $resolvedPath -Parent $InstallRoot)) {
        throw "Installed-file path escapes the install directory: '$RelativePath'."
    }

    return $resolvedPath
}

function Stop-InstalledTransportHub {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $ExecutablePath
    )

    $normalizedExecutablePath = Resolve-NormalizedPath -Path $ExecutablePath
    $matchingProcesses = New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'
    foreach ($process in @(Get-Process -Name 'TransportHub' -ErrorAction SilentlyContinue)) {
        try {
            if ([string]::Equals((Resolve-NormalizedPath -Path $process.Path), $normalizedExecutablePath,
                    [StringComparison]::OrdinalIgnoreCase)) {
                [void] $matchingProcesses.Add($process)
            }
        }
        catch {
            # Never terminate a process whose executable path cannot be inspected.
        }
    }

    foreach ($process in $matchingProcesses) {
        Write-Host "[STOP] TransportHub process $($process.Id)"
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
        try {
            [void] $process.WaitForExit(5000)
        }
        catch {
        }
    }
}

$localApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$roamingApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
$userProfileDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
if ([string]::IsNullOrWhiteSpace($localApplicationData) -or
    [string]::IsNullOrWhiteSpace($roamingApplicationData) -or
    [string]::IsNullOrWhiteSpace($userProfileDirectory)) {
    throw 'The current user profile folders could not be resolved.'
}

$programsDirectory = Resolve-NormalizedPath -Path (Join-Path -Path $localApplicationData -ChildPath 'Programs')
$installDirectory = Resolve-NormalizedPath -Path (Join-Path -Path $programsDirectory -ChildPath 'TransportHub')
$installedExecutable = Join-Path -Path $installDirectory -ChildPath 'TransportHub.exe'
$manifestPath = Join-Path -Path $installDirectory -ChildPath 'install-manifest.json'
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValueName = 'TransportHub'
$startMenuProgramsDirectory = Resolve-NormalizedPath -Path (Join-Path -Path $roamingApplicationData `
        -ChildPath 'Microsoft\Windows\Start Menu\Programs')
$startMenuDirectory = Resolve-NormalizedPath -Path (Join-Path -Path $startMenuProgramsDirectory -ChildPath 'TransportHub')
$shortcutPath = Join-Path -Path $startMenuDirectory -ChildPath 'TransportHub.lnk'
$syncthingConfigDirectory = Join-Path -Path $localApplicationData -ChildPath 'Syncthing'
$synchronizedDataDirectory = Join-Path -Path $userProfileDirectory -ChildPath 'TransportHub'

if (-not (Test-PathIsInside -Candidate $installDirectory -Parent $programsDirectory) -or
    -not [string]::Equals((Split-Path -Path $installDirectory -Leaf), 'TransportHub',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to uninstall from unexpected directory: $installDirectory"
}

if (Test-Path -LiteralPath $installDirectory) {
    $existingInstallItem = Get-Item -LiteralPath $installDirectory -Force
    if (($existingInstallItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to uninstall through a reparse-point directory: $installDirectory"
    }

    $existingInstallReparsePoints = @(Get-ChildItem -LiteralPath $installDirectory -Force -Recurse |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
    if ($existingInstallReparsePoints.Count -gt 0) {
        throw "Refusing to remove an installation containing reparse points: $($existingInstallReparsePoints[0].FullName)"
    }
}

if (-not (Test-PathIsInside -Candidate $startMenuDirectory -Parent $startMenuProgramsDirectory) -or
    -not [string]::Equals((Split-Path -Path $startMenuDirectory -Leaf), 'TransportHub',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove a shortcut from an unexpected directory: $startMenuDirectory"
}
if (Test-Path -LiteralPath $startMenuDirectory) {
    $existingStartMenuItem = Get-Item -LiteralPath $startMenuDirectory -Force
    if (($existingStartMenuItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to remove through a reparse-point Start Menu directory: $startMenuDirectory"
    }
}

Stop-InstalledTransportHub -ExecutablePath $installedExecutable

if (Test-Path -LiteralPath $runKeyPath) {
    $runKey = Get-ItemProperty -LiteralPath $runKeyPath -ErrorAction SilentlyContinue
    $runValueProperty = $null
    if ($null -ne $runKey) {
        $runValueProperty = $runKey.PSObject.Properties[$runValueName]
    }

    if ($null -ne $runValueProperty) {
        $expectedRunCommand = '"{0}"' -f $installedExecutable
        if ([string]::Equals([string] $runValueProperty.Value, $expectedRunCommand,
                [StringComparison]::OrdinalIgnoreCase) -or
            [string]::Equals([string] $runValueProperty.Value, $installedExecutable,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-ItemProperty -LiteralPath $runKeyPath -Name $runValueName -Force
        }
        else {
            Write-Warning "The HKCU Run value named TransportHub points elsewhere and was preserved."
        }
    }
}

if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
    $removeShortcut = $false
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $removeShortcut = [string]::Equals((Resolve-NormalizedPath -Path $shortcut.TargetPath),
            (Resolve-NormalizedPath -Path $installedExecutable), [StringComparison]::OrdinalIgnoreCase)
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    }
    catch {
        Write-Warning "Could not verify the Start Menu shortcut target; it was preserved: $shortcutPath"
    }

    if ($removeShortcut) {
        Remove-Item -LiteralPath $shortcutPath -Force
    }
}

$installedRelativeFiles = @()
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    try {
        $installManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
        if ($null -eq $installManifest.PSObject.Properties['Files']) {
            throw 'The Files property is missing.'
        }

        $installedRelativeFiles = @($installManifest.Files | ForEach-Object { [string] $_ })
    }
    catch {
        throw "The install manifest is invalid; refusing to delete application files: $manifestPath"
    }
}
else {
    # Backward-compatible fallback for an installation made before manifests were
    # introduced. Only known application outputs are eligible for removal.
    $installedRelativeFiles = @(
        'TransportHub.exe',
        'TransportHub.exe.config',
        'TransportHub.pdb'
    )
}

foreach ($relativeFile in $installedRelativeFiles) {
    $installedFile = Resolve-RelativeInstallPath -RelativePath $relativeFile -InstallRoot $installDirectory
    if (Test-Path -LiteralPath $installedFile -PathType Leaf) {
        Remove-Item -LiteralPath $installedFile -Force
    }
}

if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    Remove-Item -LiteralPath $manifestPath -Force
}

if (Test-Path -LiteralPath $installDirectory -PathType Container) {
    $childDirectories = @(Get-ChildItem -LiteralPath $installDirectory -Directory -Recurse |
            Sort-Object { $_.FullName.Length } -Descending)
    foreach ($childDirectory in $childDirectories) {
        if (@(Get-ChildItem -LiteralPath $childDirectory.FullName -Force).Count -eq 0) {
            Remove-Item -LiteralPath $childDirectory.FullName -Force
        }
    }

    if (@(Get-ChildItem -LiteralPath $installDirectory -Force).Count -eq 0) {
        Remove-Item -LiteralPath $installDirectory -Force
    }
    else {
        Write-Warning "Unknown files remain in the install directory and were preserved: $installDirectory"
    }
}

if (Test-Path -LiteralPath $startMenuDirectory -PathType Container) {
    if (@(Get-ChildItem -LiteralPath $startMenuDirectory -Force).Count -eq 0) {
        Remove-Item -LiteralPath $startMenuDirectory -Force
    }
}

Write-Host '[OK] TransportHub Desktop application files and per-user launch entries were removed.'
Write-Host "[PRESERVED] Syncthing configuration: $syncthingConfigDirectory"
Write-Host "[PRESERVED] Synchronized data: $synchronizedDataDirectory"

[pscustomobject] @{
    ApplicationRemoved = -not (Test-Path -LiteralPath $installedExecutable)
    SyncthingPreserved  = $true
    DataPreserved       = $true
}

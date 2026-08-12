#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter()]
    [switch] $NoLaunch
)

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
            # Ignore inaccessible processes. We never terminate a process unless its
            # executable path is proven to be this per-user installation.
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

$repositoryRoot = Resolve-NormalizedPath -Path (Join-Path -Path $PSScriptRoot -ChildPath '..')
$artifactDirectory = Resolve-NormalizedPath -Path (Join-Path -Path $repositoryRoot -ChildPath 'artifacts\TransportHub.Desktop')
$artifactExecutable = Join-Path -Path $artifactDirectory -ChildPath 'TransportHub.exe'
$localApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$roamingApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
if ([string]::IsNullOrWhiteSpace($localApplicationData) -or
    [string]::IsNullOrWhiteSpace($roamingApplicationData)) {
    throw 'The current user LocalAppData and AppData folders could not be resolved.'
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

if (-not (Test-Path -LiteralPath $artifactExecutable -PathType Leaf)) {
    throw "Desktop artifact not found: $artifactExecutable. Run scripts\build-desktop.ps1 first."
}
if (-not (Test-PathIsInside -Candidate $artifactDirectory -Parent $repositoryRoot)) {
    throw "Artifact directory is outside the repository: $artifactDirectory"
}
if (-not (Test-PathIsInside -Candidate $installDirectory -Parent $programsDirectory) -or
    -not [string]::Equals((Split-Path -Path $installDirectory -Leaf), 'TransportHub',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to install into unexpected directory: $installDirectory"
}

if (Test-Path -LiteralPath $installDirectory) {
    $existingInstallItem = Get-Item -LiteralPath $installDirectory -Force
    if (($existingInstallItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to install through a reparse-point directory: $installDirectory"
    }

    $existingInstallReparsePoints = @(Get-ChildItem -LiteralPath $installDirectory -Force -Recurse |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
    if ($existingInstallReparsePoints.Count -gt 0) {
        throw "Refusing to update an installation containing reparse points: $($existingInstallReparsePoints[0].FullName)"
    }
}

if (-not (Test-PathIsInside -Candidate $startMenuDirectory -Parent $startMenuProgramsDirectory) -or
    -not [string]::Equals((Split-Path -Path $startMenuDirectory -Leaf), 'TransportHub',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to create a shortcut in an unexpected directory: $startMenuDirectory"
}
if (Test-Path -LiteralPath $startMenuDirectory) {
    $existingStartMenuItem = Get-Item -LiteralPath $startMenuDirectory -Force
    if (($existingStartMenuItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to write through a reparse-point Start Menu directory: $startMenuDirectory"
    }
}

$artifactItems = @(Get-ChildItem -LiteralPath $artifactDirectory -Force -Recurse)
$artifactReparsePoints = @($artifactItems |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
if ($artifactReparsePoints.Count -gt 0) {
    throw "Refusing to install a reparse-point artifact: $($artifactReparsePoints[0].FullName)"
}

$artifactPrefix = $artifactDirectory.TrimEnd([char[]] @(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )) + [IO.Path]::DirectorySeparatorChar
$expectedArtifactNames = @(
    'TransportHub.exe',
    'TransportHub.exe.config',
    'TransportHub.pdb'
)
$artifactFiles = @($artifactItems | Where-Object {
        -not $_.PSIsContainer -and
        $expectedArtifactNames -contains $_.Name -and
        [string]::Equals($_.DirectoryName, $artifactDirectory, [StringComparison]::OrdinalIgnoreCase)
    })
foreach ($expectedArtifactName in $expectedArtifactNames) {
    if (-not @($artifactFiles | Where-Object {
                [string]::Equals($_.Name, $expectedArtifactName, [StringComparison]::OrdinalIgnoreCase)
            }).Count) {
        throw "Required desktop artifact not found: $(Join-Path -Path $artifactDirectory -ChildPath $expectedArtifactName)"
    }
}

$newRelativeFiles = New-Object 'System.Collections.Generic.List[string]'

foreach ($artifactFile in $artifactFiles) {
    $relativePath = $artifactFile.FullName.Substring($artifactPrefix.Length)
    if ([string]::Equals($relativePath, 'install-manifest.json', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The artifact directory must not contain the reserved file install-manifest.json.'
    }

    [void] $newRelativeFiles.Add($relativePath)
}

$previousRelativeFiles = @()
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    try {
        $previousManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
        if ($null -ne $previousManifest.PSObject.Properties['Files']) {
            $previousRelativeFiles = @($previousManifest.Files | ForEach-Object { [string] $_ })
        }
    }
    catch {
        throw "The existing install manifest is invalid; refusing to overwrite it: $manifestPath"
    }
}

Stop-InstalledTransportHub -ExecutablePath $installedExecutable
New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null

foreach ($artifactFile in $artifactFiles) {
    $relativePath = $artifactFile.FullName.Substring($artifactPrefix.Length)
    $destinationPath = Resolve-RelativeInstallPath -RelativePath $relativePath -InstallRoot $installDirectory
    $destinationDirectory = Split-Path -Path $destinationPath -Parent
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Copy-Item -LiteralPath $artifactFile.FullName -Destination $destinationPath -Force
}

foreach ($previousRelativeFile in $previousRelativeFiles) {
    if ($newRelativeFiles -contains $previousRelativeFile) {
        continue
    }

    $stalePath = Resolve-RelativeInstallPath -RelativePath $previousRelativeFile -InstallRoot $installDirectory
    if (Test-Path -LiteralPath $stalePath -PathType Leaf) {
        Remove-Item -LiteralPath $stalePath -Force
    }
}

$installManifest = [ordered] @{
    SchemaVersion  = 1
    InstalledAtUtc = [DateTime]::UtcNow.ToString('o')
    InstallRoot    = $installDirectory
    Files          = @($newRelativeFiles)
}
$installManifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

New-Item -Path $runKeyPath -Force | Out-Null
$runCommand = '"{0}"' -f $installedExecutable
New-ItemProperty -Path $runKeyPath -Name $runValueName -Value $runCommand `
    -PropertyType String -Force | Out-Null

New-Item -ItemType Directory -Path $startMenuDirectory -Force | Out-Null
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $installedExecutable
$shortcut.WorkingDirectory = $installDirectory
$shortcut.Description = 'TransportHub desktop transfer window'
$shortcut.IconLocation = "$installedExecutable,0"
$shortcut.Save()
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null

if (-not $NoLaunch) {
    Start-Process -FilePath $installedExecutable -WorkingDirectory $installDirectory | Out-Null
}

$installedAssembly = [Reflection.AssemblyName]::GetAssemblyName($installedExecutable)
[pscustomobject] @{
    Executable       = $installedExecutable
    Version          = $installedAssembly.Version.ToString()
    AutoStart        = "$runKeyPath\$runValueName"
    StartMenuShortcut = $shortcutPath
    Launched         = -not $NoLaunch
}

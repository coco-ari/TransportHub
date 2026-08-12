#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '',

    [Parameter()]
    [string] $InnoCompilerPath = ''
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

$repositoryRoot = Resolve-NormalizedPath -Path (Join-Path -Path $PSScriptRoot -ChildPath '..')
$artifactsRoot = Resolve-NormalizedPath -Path (Join-Path -Path $repositoryRoot -ChildPath 'artifacts')
$desktopArtifacts = Join-Path -Path $artifactsRoot -ChildPath 'TransportHub.Desktop'
$releaseRoot = Resolve-NormalizedPath -Path (Join-Path -Path $artifactsRoot -ChildPath 'release')
$sourceExecutable = Join-Path -Path $desktopArtifacts -ChildPath 'TransportHub.exe'
$sourceConfig = $sourceExecutable + '.config'

foreach ($requiredFile in @($sourceExecutable, $sourceConfig)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required Release artifact not found: $requiredFile. Run scripts\build-desktop.ps1 first."
    }
}

$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($sourceExecutable).Version
$detectedVersion = '{0}.{1}.{2}' -f $assemblyVersion.Major, $assemblyVersion.Minor, $assemblyVersion.Build
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $detectedVersion
}
elseif (-not [string]::Equals($Version, $detectedVersion, [StringComparison]::Ordinal)) {
    throw "Requested package version '$Version' does not match TransportHub.exe version '$detectedVersion'."
}

if (-not (Test-PathIsInside -Candidate $releaseRoot -Parent $artifactsRoot) -or
    -not [string]::Equals((Split-Path -Path $releaseRoot -Leaf), 'release',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean unexpected release directory: $releaseRoot"
}

if (Test-Path -LiteralPath $releaseRoot) {
    $releaseRootItem = Get-Item -LiteralPath $releaseRoot -Force
    if (($releaseRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to clean a reparse-point release directory: $releaseRoot"
    }

    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}

$compilerCandidates = New-Object 'System.Collections.Generic.List[string]'
if (-not [string]::IsNullOrWhiteSpace($InnoCompilerPath)) {
    [void] $compilerCandidates.Add((Resolve-NormalizedPath -Path $InnoCompilerPath))
}
$innoCommand = Get-Command -Name 'ISCC.exe' -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($null -ne $innoCommand) {
    [void] $compilerCandidates.Add((Resolve-NormalizedPath -Path $innoCommand.Source))
}
foreach ($programFilesRoot in @(
        (Join-Path -Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
            -ChildPath 'Programs'),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    )) {
    if (-not [string]::IsNullOrWhiteSpace($programFilesRoot)) {
        [void] $compilerCandidates.Add((Join-Path -Path $programFilesRoot -ChildPath 'Inno Setup 6\ISCC.exe'))
    }
}

$innoCompiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace([string] $innoCompiler)) {
    throw 'Inno Setup 6 compiler (ISCC.exe) was not found. Install Inno Setup or pass -InnoCompilerPath.'
}

$innoScript = Join-Path -Path $repositoryRoot -ChildPath 'packaging\TransportHub.iss'
if (-not (Test-Path -LiteralPath $innoScript -PathType Leaf)) {
    throw "Inno Setup script not found: $innoScript"
}

$innoArguments = @(
    "/DAppVersion=$Version",
    "/DRepositoryRoot=$repositoryRoot",
    "/DOutputDirectory=$releaseRoot",
    $innoScript
)
& $innoCompiler @innoArguments
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

$setupPath = Join-Path -Path $releaseRoot -ChildPath "TransportHub-Setup-v$Version.exe"
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "Inno Setup reported success but did not create: $setupPath"
}

$checksumPath = Join-Path -Path $releaseRoot -ChildPath 'SHA256SUMS.txt'
$checksumFiles = @($setupPath)
$checksumLines = foreach ($checksumFile in $checksumFiles) {
    $hash = Get-FileHash -LiteralPath $checksumFile -Algorithm SHA256
    '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), (Split-Path -Path $checksumFile -Leaf)
}
$checksumLines | Set-Content -LiteralPath $checksumPath -Encoding ASCII

[pscustomobject] @{
    Version              = $Version
    Installer            = $setupPath
    Checksums            = $checksumPath
}

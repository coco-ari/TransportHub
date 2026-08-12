#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter()]
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [Parameter()]
    [string] $CompilerPath = ''
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

function Get-ProjectProperty {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [xml] $Project,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $node = $Project.SelectSingleNode("//*[local-name()='$Name']")
    if ($null -eq $node) {
        return ''
    }

    return [string] $node.InnerText
}

function Resolve-RoslynCompiler {
    [CmdletBinding()]
    param(
        [Parameter()]
        [string] $RequestedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolvedRequestedPath = Resolve-NormalizedPath -Path $RequestedPath
        if (-not (Test-Path -LiteralPath $resolvedRequestedPath -PathType Leaf)) {
            throw "The requested C# compiler does not exist: $resolvedRequestedPath"
        }

        return $resolvedRequestedPath
    }

    $candidates = New-Object 'System.Collections.Generic.List[string]'
    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $vsWherePath = Join-Path -Path $programFilesX86 -ChildPath 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vsWherePath -PathType Leaf) {
        $vsWhereOutput = @(& $vsWherePath -latest -products '*' -version '[17.0,)' `
                -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\Roslyn\csc.exe' 2>$null)
        foreach ($resultPath in $vsWhereOutput) {
            if (-not [string]::IsNullOrWhiteSpace([string] $resultPath)) {
                [void] $candidates.Add([string] $resultPath)
            }
        }
    }

    foreach ($edition in @('BuildTools', 'Community', 'Professional', 'Enterprise')) {
        [void] $candidates.Add((Join-Path -Path $programFilesX86 `
                    -ChildPath "Microsoft Visual Studio\2022\$edition\MSBuild\Current\Bin\Roslyn\csc.exe"))
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return Resolve-NormalizedPath -Path $candidate
        }
    }

    throw 'A Visual Studio 2022 or later Roslyn csc.exe was not found. Install the MSBuild/C# build tools or pass -CompilerPath.'
}

$repositoryRoot = Resolve-NormalizedPath -Path (Join-Path -Path $PSScriptRoot -ChildPath '..')
$projectDirectory = Join-Path -Path $repositoryRoot -ChildPath 'apps\TransportHub.Desktop'
$projectPath = Join-Path -Path $projectDirectory -ChildPath 'TransportHub.Desktop.csproj'
$outputDirectory = Join-Path -Path $repositoryRoot -ChildPath 'artifacts\TransportHub.Desktop'
$outputExecutable = Join-Path -Path $outputDirectory -ChildPath 'TransportHub.exe'
$outputPdb = Join-Path -Path $outputDirectory -ChildPath 'TransportHub.pdb'

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Desktop project not found: $projectPath"
}

[xml] $projectXml = Get-Content -LiteralPath $projectPath -Raw
$targetFramework = Get-ProjectProperty -Project $projectXml -Name 'TargetFrameworkVersion'
$outputType = Get-ProjectProperty -Project $projectXml -Name 'OutputType'
$assemblyName = Get-ProjectProperty -Project $projectXml -Name 'AssemblyName'

if ($targetFramework -ne 'v4.8') {
    throw "TransportHub.Desktop must target .NET Framework v4.8; the project declares '$targetFramework'."
}
if ($outputType -ne 'WinExe') {
    throw "TransportHub.Desktop must use OutputType WinExe; the project declares '$outputType'."
}
if ($assemblyName -ne 'TransportHub') {
    throw "TransportHub.Desktop must produce TransportHub.exe; AssemblyName is '$assemblyName'."
}

$sourceFiles = New-Object 'System.Collections.Generic.List[string]'
$compileNodes = @($projectXml.SelectNodes("//*[local-name()='Compile' and @Include]"))
if ($compileNodes.Count -eq 0) {
    throw 'The old-style project does not contain any explicit Compile items.'
}

foreach ($compileNode in $compileNodes) {
    $relativeSourcePath = [string] $compileNode.Include
    if ([string]::IsNullOrWhiteSpace($relativeSourcePath) -or
        [IO.Path]::IsPathRooted($relativeSourcePath) -or
        $relativeSourcePath.IndexOfAny([char[]] @('*', '?')) -ge 0) {
        throw "Unsupported Compile Include path: '$relativeSourcePath'."
    }

    $sourcePath = Resolve-NormalizedPath -Path (Join-Path -Path $projectDirectory -ChildPath $relativeSourcePath)
    if (-not (Test-PathIsInside -Candidate $sourcePath -Parent $projectDirectory)) {
        throw "Compile item escapes the project directory: '$relativeSourcePath'."
    }
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Compile item does not exist: $sourcePath"
    }

    [void] $sourceFiles.Add($sourcePath)
}

$compiler = Resolve-RoslynCompiler -RequestedPath $CompilerPath
$frameworkDirectory = Join-Path -Path $env:WINDIR -ChildPath 'Microsoft.NET\Framework64\v4.0.30319'
$referenceNames = @(
    'mscorlib.dll',
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll',
    'System.Xml.dll',
    'System.Net.Http.dll',
    'System.Net.Http.WebRequest.dll',
    'System.Web.Extensions.dll'
)
$referencePaths = New-Object 'System.Collections.Generic.List[string]'

foreach ($referenceName in $referenceNames) {
    $referencePath = Join-Path -Path $frameworkDirectory -ChildPath $referenceName
    if (-not (Test-Path -LiteralPath $referencePath -PathType Leaf)) {
        throw "Required .NET Framework runtime assembly was not found: $referencePath"
    }

    [void] $referencePaths.Add($referencePath)
}

$manifestPath = Join-Path -Path $projectDirectory -ChildPath 'app.manifest'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Application manifest not found: $manifestPath"
}

$expectedOutputDirectory = Resolve-NormalizedPath -Path (Join-Path -Path $repositoryRoot -ChildPath 'artifacts\TransportHub.Desktop')
$resolvedOutputDirectory = Resolve-NormalizedPath -Path $outputDirectory
if (-not [string]::Equals($resolvedOutputDirectory, $expectedOutputDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-PathIsInside -Candidate $resolvedOutputDirectory -Parent $repositoryRoot)) {
    throw "Refusing to clean unexpected output directory: $resolvedOutputDirectory"
}

if (Test-Path -LiteralPath $resolvedOutputDirectory) {
    $existingOutputItem = Get-Item -LiteralPath $resolvedOutputDirectory -Force
    if (($existingOutputItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to clean a reparse-point output directory: $resolvedOutputDirectory"
    }
    Remove-Item -LiteralPath $resolvedOutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null

$compilerArguments = New-Object 'System.Collections.Generic.List[string]'
foreach ($argument in @(
        '/nologo',
        '/noconfig',
        '/nostdlib+',
        '/target:winexe',
        '/platform:anycpu',
        '/langversion:latest',
        '/utf8output',
        '/warn:4',
        '/deterministic+',
        "/pathmap:$repositoryRoot=.",
        "/out:$outputExecutable",
        "/win32manifest:$manifestPath"
    )) {
    [void] $compilerArguments.Add($argument)
}

if ($Configuration -eq 'Debug') {
    foreach ($argument in @('/optimize-', '/debug:full', '/define:DEBUG;TRACE', "/pdb:$outputPdb")) {
        [void] $compilerArguments.Add($argument)
    }
}
else {
    foreach ($argument in @('/optimize+', '/debug:pdbonly', '/define:TRACE', "/pdb:$outputPdb")) {
        [void] $compilerArguments.Add($argument)
    }
}

foreach ($referencePath in $referencePaths) {
    [void] $compilerArguments.Add("/reference:$referencePath")
}
foreach ($sourceFile in $sourceFiles) {
    [void] $compilerArguments.Add($sourceFile)
}

Write-Host "[BUILD] Compiler: $compiler"
Write-Host "[BUILD] Configuration: $Configuration"
Write-Host "[BUILD] Sources: $($sourceFiles.Count)"
& $compiler @compilerArguments
$compilerExitCode = $LASTEXITCODE
if ($compilerExitCode -ne 0) {
    throw "Roslyn compilation failed with exit code $compilerExitCode."
}

if (-not (Test-Path -LiteralPath $outputExecutable -PathType Leaf)) {
    throw "Compilation reported success but did not create: $outputExecutable"
}

$appConfigPath = Join-Path -Path $projectDirectory -ChildPath 'App.config'
if (Test-Path -LiteralPath $appConfigPath -PathType Leaf) {
    Copy-Item -LiteralPath $appConfigPath -Destination ($outputExecutable + '.config') -Force
}

$builtAssembly = [Reflection.AssemblyName]::GetAssemblyName($outputExecutable)
if ($builtAssembly.Name -ne 'TransportHub') {
    throw "Built assembly identity is '$($builtAssembly.Name)', expected 'TransportHub'."
}

[pscustomobject] @{
    Executable    = $outputExecutable
    Configuration = $Configuration
    AssemblyName  = $builtAssembly.Name
    Version       = $builtAssembly.Version.ToString()
    Compiler      = $compiler
    SourceCount   = $sourceFiles.Count
}

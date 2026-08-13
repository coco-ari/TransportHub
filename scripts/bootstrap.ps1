#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string] $FolderPath = (Join-Path -Path $env:USERPROFILE -ChildPath 'TransportHub'),

    [Parameter()]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string] $FolderId = 'transporthub-data',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string] $FolderLabel = 'TransportHub',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string] $ConfigDirectory = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Syncthing'),

    [Parameter()]
    [ValidateRange(5, 600)]
    [int] $ApiTimeoutSeconds = 90,

    [Parameter()]
    [switch] $SkipInstall,

    [Parameter()]
    [switch] $SkipFirewall,

    [Parameter()]
    [switch] $SkipAutoStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageId = 'BillStewart.SyncthingWindowsSetup'
$packageVersion = '2.0.2'
$staggeredMaxAgeSeconds = '7776000'
$versionCleanupIntervalSeconds = '3600'

function Resolve-NormalizedPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'A filesystem path cannot be empty.'
    }

    $expandedPath = [Environment]::ExpandEnvironmentVariables($Path.Trim())
    $fullPath = [IO.Path]::GetFullPath($expandedPath)
    $pathRoot = [IO.Path]::GetPathRoot($fullPath)

    if (-not [string]::Equals($fullPath, $pathRoot, [StringComparison]::OrdinalIgnoreCase)) {
        $fullPath = $fullPath.TrimEnd([char[]] @(
                [IO.Path]::DirectorySeparatorChar,
                [IO.Path]::AltDirectorySeparatorChar
            ))
    }

    return $fullPath
}

function Resolve-ConfiguredFolderPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $configuredPath = $Path.Trim()
    if ($configuredPath -eq '~') {
        return Resolve-NormalizedPath -Path $env:USERPROFILE
    }

    if ($configuredPath.StartsWith('~\', [StringComparison]::Ordinal) -or
        $configuredPath.StartsWith('~/', [StringComparison]::Ordinal)) {
        return Resolve-NormalizedPath -Path (Join-Path -Path $env:USERPROFILE -ChildPath $configuredPath.Substring(2))
    }

    if (-not [IO.Path]::IsPathRooted($configuredPath)) {
        throw "Cannot safely resolve existing Syncthing folder path '$configuredPath'."
    }

    return Resolve-NormalizedPath -Path $configuredPath
}

function Test-PathsOverlap {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $FirstPath,

        [Parameter(Mandatory)]
        [string] $SecondPath
    )

    if ([string]::Equals($FirstPath, $SecondPath, [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    $separator = [string] [IO.Path]::DirectorySeparatorChar
    $firstPrefix = if ($FirstPath.EndsWith($separator, [StringComparison]::Ordinal)) {
        $FirstPath
    }
    else {
        $FirstPath + $separator
    }
    $secondPrefix = if ($SecondPath.EndsWith($separator, [StringComparison]::Ordinal)) {
        $SecondPath
    }
    else {
        $SecondPath + $separator
    }

    return $FirstPath.StartsWith($secondPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $SecondPath.StartsWith($firstPrefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoExistingReparsePoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $current = Resolve-NormalizedPath -Path $Path
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse points are not allowed in the TransportHub path: '$current'."
            }
        }
        $parent = Split-Path -Path $current -Parent
        if ([string]::IsNullOrWhiteSpace($parent) -or
            [string]::Equals($parent, $current, [StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $current = $parent
    }
}

function Invoke-NativeCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [Parameter()]
        [string[]] $ArgumentList = @()
    )

    $outputLines = @(& $FilePath @ArgumentList 2>&1)
    $exitCode = $LASTEXITCODE
    $outputText = ($outputLines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine

    return [pscustomobject] @{
        ExitCode = $exitCode
        Output   = $outputText
    }
}

function Get-BillStewartInstallation {
    [CmdletBinding()]
    param()

    $candidateDirectories = New-Object 'System.Collections.Generic.List[string]'
    $defaultInstallDirectory = Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Programs\Syncthing'
    [void] $candidateDirectories.Add($defaultInstallDirectory)

    $uninstallRegistryPaths = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )

    foreach ($registryPath in $uninstallRegistryPaths) {
        $entries = @(Get-ItemProperty -Path $registryPath -ErrorAction SilentlyContinue)
        foreach ($entry in $entries) {
            $displayNameProperty = $entry.PSObject.Properties['DisplayName']
            $installLocationProperty = $entry.PSObject.Properties['InstallLocation']
            if ($null -eq $displayNameProperty -or $null -eq $installLocationProperty) {
                continue
            }

            $displayName = [string] $displayNameProperty.Value
            $installLocation = [string] $installLocationProperty.Value
            if ($displayName -like 'Syncthing*' -and -not [string]::IsNullOrWhiteSpace($installLocation)) {
                [void] $candidateDirectories.Add($installLocation)
            }
        }
    }

    $visited = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($candidateDirectory in $candidateDirectories) {
        if ([string]::IsNullOrWhiteSpace($candidateDirectory)) {
            continue
        }

        $normalizedDirectory = Resolve-NormalizedPath -Path $candidateDirectory
        if (-not $visited.Add($normalizedDirectory)) {
            continue
        }

        $executablePath = Join-Path -Path $normalizedDirectory -ChildPath 'syncthing.exe'
        $firewallScriptPath = Join-Path -Path $normalizedDirectory -ChildPath 'SyncthingFirewallRule.js'
        $logonTaskScriptPath = Join-Path -Path $normalizedDirectory -ChildPath 'SyncthingLogonTask.js'
        $controlExecutablePath = Join-Path -Path $normalizedDirectory -ChildPath 'stctl.exe'
        $hasFirewallHelper = Test-Path -LiteralPath $firewallScriptPath -PathType Leaf
        $hasLogonTaskHelper = (Test-Path -LiteralPath $logonTaskScriptPath -PathType Leaf) -and
            (Test-Path -LiteralPath $controlExecutablePath -PathType Leaf)
        if ((Test-Path -LiteralPath $executablePath -PathType Leaf) -and
            ($hasFirewallHelper -or $hasLogonTaskHelper)) {
            return [pscustomobject] @{
                InstallDirectory = $normalizedDirectory
                ExecutablePath   = $executablePath
                FirewallScript   = if ($hasFirewallHelper) {
                    $firewallScriptPath
                }
                else {
                    $null
                }
                LogonTaskScript  = if ($hasLogonTaskHelper) {
                    $logonTaskScriptPath
                }
                else {
                    $null
                }
                IsBillStewart    = $true
            }
        }
    }

    return $null
}

function Get-AnySyncthingExecutable {
    [CmdletBinding()]
    param()

    $pathCommand = Get-Command -Name 'syncthing.exe' -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $pathCommand) {
        return $null
    }

    return Resolve-NormalizedPath -Path $pathCommand.Source
}

function Install-BillStewartSyncthing {
    [CmdletBinding()]
    param()

    $wingetCommand = Get-Command -Name 'winget.exe' -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $wingetCommand) {
        throw "WinGet is required to install package '$packageId'. Install App Installer or rerun with -SkipInstall and an existing syncthing.exe on PATH."
    }

    $installArguments = @(
        'install',
        '--id', $packageId,
        '--exact',
        '--version', $packageVersion,
        '--scope', 'user',
        '--silent',
        '--accept-package-agreements',
        '--accept-source-agreements',
        '--disable-interactivity'
    )

    $installResult = Invoke-NativeCommand -FilePath $wingetCommand.Source -ArgumentList $installArguments
    if ($installResult.ExitCode -ne 0) {
        throw "WinGet could not install '$packageId' (exit code $($installResult.ExitCode))."
    }
}

function Invoke-SyncthingCli {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $ExecutablePath,

        [Parameter(Mandatory)]
        [string] $HomeDirectory,

        [Parameter(Mandatory)]
        [string[]] $CliArguments,

        [Parameter()]
        [switch] $AllowFailure
    )

    # The CLI authenticates locally using the selected home directory. Never read,
    # pass, log, or print the GUI API key.
    $arguments = @('cli', "--home=$HomeDirectory") + $CliArguments
    $result = Invoke-NativeCommand -FilePath $ExecutablePath -ArgumentList $arguments
    if (-not $AllowFailure -and $result.ExitCode -ne 0) {
        throw "Syncthing CLI operation failed (exit code $($result.ExitCode))."
    }

    return $result
}

function Get-SyncthingSystemStatus {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $ExecutablePath,

        [Parameter(Mandatory)]
        [string] $HomeDirectory,

        [Parameter()]
        [switch] $AllowFailure
    )

    $result = Invoke-SyncthingCli -ExecutablePath $ExecutablePath -HomeDirectory $HomeDirectory `
        -CliArguments @('show', 'system') -AllowFailure:$AllowFailure
    if ($result.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($result.Output)) {
        return $null
    }

    try {
        return $result.Output | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        if ($AllowFailure) {
            return $null
        }

        throw 'Syncthing returned an invalid system-status response.'
    }
}

function Start-AndWaitForSyncthing {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $ExecutablePath,

        [Parameter(Mandatory)]
        [string] $HomeDirectory,

        [Parameter(Mandatory)]
        [int] $TimeoutSeconds
    )

    $status = Get-SyncthingSystemStatus -ExecutablePath $ExecutablePath -HomeDirectory $HomeDirectory -AllowFailure
    if ($null -eq $status) {
        $quotedHomeArgument = '--home="{0}"' -f $HomeDirectory
        $serveArguments = @(
            'serve',
            $quotedHomeArgument,
            '--no-browser',
            '--no-console'
        )

        Start-Process -FilePath $ExecutablePath -ArgumentList $serveArguments -WindowStyle Hidden | Out-Null
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $status = Get-SyncthingSystemStatus -ExecutablePath $ExecutablePath -HomeDirectory $HomeDirectory -AllowFailure
        if ($null -ne $status) {
            $deviceIdProperty = $status.PSObject.Properties['myID']
            if ($null -ne $deviceIdProperty -and -not [string]::IsNullOrWhiteSpace([string] $deviceIdProperty.Value)) {
                return $status
            }
        }

        Start-Sleep -Milliseconds 750
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Syncthing did not become ready within $TimeoutSeconds seconds."
}

function Enable-SyncthingAutoStart {
    [CmdletBinding()]
    param(
        [Parameter()]
        [AllowNull()]
        [psobject] $Installation,

        [Parameter(Mandatory)]
        [string] $ExecutablePath,

        [Parameter(Mandatory)]
        [string] $HomeDirectory
    )

    $logonTaskScript = if ($null -ne $Installation -and
        $null -ne $Installation.PSObject.Properties['LogonTaskScript']) {
        [string] $Installation.LogonTaskScript
    }
    else {
        ''
    }

    if (-not [string]::IsNullOrWhiteSpace($logonTaskScript) -and
        (Test-Path -LiteralPath $logonTaskScript -PathType Leaf)) {
        $defaultHomeDirectory = Resolve-NormalizedPath -Path (Join-Path -Path $env:LOCALAPPDATA `
                -ChildPath 'Syncthing')
        if (-not [string]::Equals((Resolve-NormalizedPath -Path $HomeDirectory), $defaultHomeDirectory,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Syncthing Windows Setup logon startup supports its default per-user configuration directory only. Rerun with the default ConfigDirectory or -SkipAutoStart.'
        }

        $cscriptPath = Join-Path -Path $env:SystemRoot -ChildPath 'System32\cscript.exe'
        if (-not (Test-Path -LiteralPath $cscriptPath -PathType Leaf)) {
            throw 'Windows Script Host is required to configure Syncthing logon startup.'
        }

        # Create-or-update is intentionally unconditional: the helper replaces a
        # stale task with the supported stctl.exe action and remains idempotent.
        $taskCreate = Invoke-NativeCommand -FilePath $cscriptPath `
            -ArgumentList @('//nologo', $logonTaskScript, '/create', '/silent')
        if ($taskCreate.ExitCode -ne 0) {
            throw "Syncthing Windows Setup could not create its logon task (exit code $($taskCreate.ExitCode))."
        }

        $taskTest = Invoke-NativeCommand -FilePath $cscriptPath `
            -ArgumentList @('//nologo', $logonTaskScript, '/test')
        if ($taskTest.ExitCode -ne 0) {
            throw 'Syncthing Windows Setup logon task could not be verified after creation.'
        }

        return 'BillStewartScheduledTask'
    }

    # Portable Syncthing installations do not include the Windows Setup task
    # helper. Use a narrowly named per-user Run value as a safe fallback. This
    # entry remains owned by Syncthing bootstrap and is intentionally preserved
    # when TransportHub itself is uninstalled.
    $runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $runValueName = 'TransportHubSyncthing'
    $runCommand = '"{0}" serve --home="{1}" --no-browser --no-console' -f `
        $ExecutablePath, $HomeDirectory

    New-Item -Path $runKeyPath -Force | Out-Null
    New-ItemProperty -Path $runKeyPath -Name $runValueName -Value $runCommand `
        -PropertyType String -Force | Out-Null

    $verifiedRunValue = [string] (Get-ItemPropertyValue -LiteralPath $runKeyPath `
            -Name $runValueName -ErrorAction Stop)
    if (-not [string]::Equals($verifiedRunValue, $runCommand, [StringComparison]::Ordinal)) {
        throw 'The fallback Syncthing per-user startup entry could not be verified.'
    }

    return 'CurrentUserRunValue'
}

function Get-ConfiguredFolders {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $ExecutablePath,

        [Parameter(Mandatory)]
        [string] $HomeDirectory
    )

    $listResult = Invoke-SyncthingCli -ExecutablePath $ExecutablePath -HomeDirectory $HomeDirectory `
        -CliArguments @('config', 'folders', 'list')
    $folderIds = @($listResult.Output -split '\r?\n' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $configuredFolders = @()

    foreach ($configuredFolderId in $folderIds) {
        $dumpResult = Invoke-SyncthingCli -ExecutablePath $ExecutablePath -HomeDirectory $HomeDirectory `
            -CliArguments @('config', 'folders', $configuredFolderId, 'dump-json')
        try {
            $configuredFolders += $dumpResult.Output | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            throw "Syncthing returned invalid folder data for '$configuredFolderId'."
        }
    }

    return $configuredFolders
}

if ($env:OS -ne 'Windows_NT') {
    throw 'This bootstrap script supports Windows only.'
}

if ([string]::IsNullOrWhiteSpace($env:USERPROFILE) -or [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw 'USERPROFILE and LOCALAPPDATA must be available for the current Windows user.'
}

$resolvedFolderPath = Resolve-NormalizedPath -Path $FolderPath
$resolvedConfigDirectory = Resolve-NormalizedPath -Path $ConfigDirectory

$folderPathRoot = [IO.Path]::GetPathRoot($resolvedFolderPath)
if ([string]::Equals($resolvedFolderPath, $folderPathRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "FolderPath must not be a drive root ('$resolvedFolderPath')."
}
if ($resolvedFolderPath.StartsWith('\\', [StringComparison]::Ordinal)) {
    throw 'FolderPath must be a local path; UNC paths are not supported.'
}

if (Test-PathsOverlap -FirstPath $resolvedFolderPath -SecondPath $resolvedConfigDirectory) {
    throw 'The synchronized folder must not contain, or be contained by, the Syncthing configuration directory.'
}
Assert-NoExistingReparsePoint -Path $resolvedFolderPath
Assert-NoExistingReparsePoint -Path $resolvedConfigDirectory

$existingService = Get-Service -Name 'syncthing' -ErrorAction SilentlyContinue
if ($null -ne $existingService) {
    throw '检测到全局服务版 Syncthing。TransportHub 当前只支持当前用户安装；请先停用服务版，或改用独立 Windows 用户安装。'
}

$installation = Get-BillStewartInstallation
$syncthingExecutable = if ($null -ne $installation) {
    $installation.ExecutablePath
}
else {
    Get-AnySyncthingExecutable
}

# Preserve portable and other existing installs. WinGet is used only when no
# usable executable exists anywhere we can discover.
if ($null -eq $syncthingExecutable -and -not $SkipInstall) {
    Install-BillStewartSyncthing
    $installation = Get-BillStewartInstallation
    if ($null -eq $installation) {
        throw "Package '$packageId' completed installation, but its current-user executable could not be found."
    }
    $syncthingExecutable = $installation.ExecutablePath
}

if ($null -eq $syncthingExecutable) {
    throw "Syncthing is not installed. Rerun without -SkipInstall to install '$packageId'."
}

if (-not $SkipFirewall) {
    if ($null -eq $installation -or [string]::IsNullOrWhiteSpace([string] $installation.FirewallScript)) {
        Write-Warning "The Bill Stewart firewall helper was not found; continuing without changing Windows Firewall."
    }
    else {
        $cscriptPath = Join-Path -Path $env:SystemRoot -ChildPath 'System32\cscript.exe'
        if (-not (Test-Path -LiteralPath $cscriptPath -PathType Leaf)) {
            Write-Warning 'Windows Script Host was not found; continuing without changing Windows Firewall.'
        }
        else {
            $firewallTest = Invoke-NativeCommand -FilePath $cscriptPath `
                -ArgumentList @('//nologo', $installation.FirewallScript, '/test')
            if ($firewallTest.ExitCode -ne 0) {
                # /silent suppresses the helper's confirmation dialog, but Windows
                # still presents the normal UAC consent prompt. Creation is async.
                $firewallCreate = Invoke-NativeCommand -FilePath $cscriptPath `
                    -ArgumentList @('//nologo', $installation.FirewallScript, '/create', '/silent')
                $firewallDeadline = [DateTime]::UtcNow.AddSeconds([Math]::Min(30, $ApiTimeoutSeconds))
                do {
                    Start-Sleep -Milliseconds 750
                    $firewallTest = Invoke-NativeCommand -FilePath $cscriptPath `
                        -ArgumentList @('//nologo', $installation.FirewallScript, '/test')
                } while ($firewallTest.ExitCode -ne 0 -and [DateTime]::UtcNow -lt $firewallDeadline)

                if ($firewallTest.ExitCode -ne 0) {
                    throw 'The Syncthing firewall rule is still absent. Approve the Windows UAC prompt and retry installation.'
                }
            }
        }
    }
}

if (-not (Test-Path -LiteralPath $resolvedConfigDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $resolvedConfigDirectory -Force | Out-Null
}

$systemStatus = Start-AndWaitForSyncthing -ExecutablePath $syncthingExecutable `
    -HomeDirectory $resolvedConfigDirectory -TimeoutSeconds $ApiTimeoutSeconds
$deviceId = [string] $systemStatus.PSObject.Properties['myID'].Value
$syncthingVersionProperty = $systemStatus.PSObject.Properties['version']
$guiAddressProperty = $systemStatus.PSObject.Properties['guiAddressUsed']
$syncthingVersion = if ($null -ne $syncthingVersionProperty) { [string] $syncthingVersionProperty.Value } else { '' }
$webGuiAddress = if ($null -ne $guiAddressProperty) { [string] $guiAddressProperty.Value } else { '' }
if ([string]::IsNullOrWhiteSpace($syncthingVersion)) {
    $versionResult = Invoke-NativeCommand -FilePath $syncthingExecutable `
        -ArgumentList @('version', "--home=$resolvedConfigDirectory")
    if ($versionResult.ExitCode -eq 0 -and $versionResult.Output -match '(?i)\bsyncthing\s+(v[0-9][^\s"]*)') {
        $syncthingVersion = $Matches[1]
    }
}

$configuredFolders = @(Get-ConfiguredFolders -ExecutablePath $syncthingExecutable `
        -HomeDirectory $resolvedConfigDirectory)
$targetFolder = $null

foreach ($configuredFolder in $configuredFolders) {
    $configuredId = [string] $configuredFolder.id
    $configuredPath = Resolve-ConfiguredFolderPath -Path ([string] $configuredFolder.path)

    if ([string]::Equals($configuredId, $FolderId, [StringComparison]::Ordinal)) {
        if (-not [string]::Equals($configuredPath, $resolvedFolderPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Folder ID '$FolderId' already exists at '$configuredPath'; refusing to replace its path."
        }

        $targetFolder = $configuredFolder
        continue
    }

    if (Test-PathsOverlap -FirstPath $configuredPath -SecondPath $resolvedFolderPath) {
        throw "Existing Syncthing folder '$configuredId' at '$configuredPath' conflicts with '$resolvedFolderPath'."
    }
}

if (-not (Test-Path -LiteralPath $resolvedFolderPath -PathType Container)) {
    New-Item -ItemType Directory -Path $resolvedFolderPath -Force | Out-Null
}
Assert-NoExistingReparsePoint -Path $resolvedFolderPath

$machineDirectoryName = [Environment]::MachineName.Trim()
$machineDirectoryName = $machineDirectoryName -replace '[<>:"/\\|?*]', '_'
$machineDirectoryName = $machineDirectoryName.TrimEnd([char[]] @(' ', '.'))
$machineDirectoryBase = if ([string]::IsNullOrWhiteSpace($machineDirectoryName)) { 'device' } else { $machineDirectoryName }
$machineDirectoryName = $machineDirectoryBase + '-' + $deviceId.Substring(0, 7)

$machineDirectory = Join-Path -Path $resolvedFolderPath -ChildPath $machineDirectoryName
if (Test-Path -LiteralPath $machineDirectory -PathType Leaf) {
    throw "The machine subdirectory path '$machineDirectory' is occupied by a file."
}
if (-not (Test-Path -LiteralPath $machineDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $machineDirectory -Force | Out-Null
}
Assert-NoExistingReparsePoint -Path $machineDirectory

$folderWasCreated = $null -eq $targetFolder
if ($folderWasCreated) {
    Invoke-SyncthingCli -ExecutablePath $syncthingExecutable -HomeDirectory $resolvedConfigDirectory `
        -CliArguments @(
            'config', 'folders', 'add',
            '--id', $FolderId,
            '--label', $FolderLabel,
            '--path', $resolvedFolderPath,
            '--type', 'sendreceive'
        ) | Out-Null
}

# Update only the requested folder's individual fields. This keeps its device list
# and all unrelated local Syncthing settings intact.
Invoke-SyncthingCli -ExecutablePath $syncthingExecutable -HomeDirectory $resolvedConfigDirectory `
    -CliArguments @('config', 'folders', $FolderId, 'type', 'set', 'sendreceive') | Out-Null
Invoke-SyncthingCli -ExecutablePath $syncthingExecutable -HomeDirectory $resolvedConfigDirectory `
    -CliArguments @('config', 'folders', $FolderId, 'label', 'set', $FolderLabel) | Out-Null
Invoke-SyncthingCli -ExecutablePath $syncthingExecutable -HomeDirectory $resolvedConfigDirectory `
    -CliArguments @('config', 'folders', $FolderId, 'versioning', 'type', 'set', 'staggered') | Out-Null
Invoke-SyncthingCli -ExecutablePath $syncthingExecutable -HomeDirectory $resolvedConfigDirectory `
    -CliArguments @('config', 'folders', $FolderId, 'versioning', 'params', 'set', 'maxAge', $staggeredMaxAgeSeconds) | Out-Null
Invoke-SyncthingCli -ExecutablePath $syncthingExecutable -HomeDirectory $resolvedConfigDirectory `
    -CliArguments @('config', 'folders', $FolderId, 'versioning', 'cleanup-intervals', 'set', $versionCleanupIntervalSeconds) | Out-Null

# TransportHub promises automatic LAN and Internet connectivity. Normalize these
# options even when an existing per-user Syncthing profile had disabled them.
Invoke-SyncthingCli -ExecutablePath $syncthingExecutable -HomeDirectory $resolvedConfigDirectory `
    -CliArguments @('config', 'options', 'global-ann-enabled', 'set', 'true') | Out-Null
Invoke-SyncthingCli -ExecutablePath $syncthingExecutable -HomeDirectory $resolvedConfigDirectory `
    -CliArguments @('config', 'options', 'local-ann-enabled', 'set', 'true') | Out-Null
Invoke-SyncthingCli -ExecutablePath $syncthingExecutable -HomeDirectory $resolvedConfigDirectory `
    -CliArguments @('config', 'options', 'relays-enabled', 'set', 'true') | Out-Null
Invoke-SyncthingCli -ExecutablePath $syncthingExecutable -HomeDirectory $resolvedConfigDirectory `
    -CliArguments @('config', 'options', 'natenabled', 'set', 'true') | Out-Null

$verificationResult = Invoke-SyncthingCli -ExecutablePath $syncthingExecutable -HomeDirectory $resolvedConfigDirectory `
    -CliArguments @('config', 'folders', $FolderId, 'dump-json')
try {
    $verifiedFolder = $verificationResult.Output | ConvertFrom-Json -ErrorAction Stop
}
catch {
    throw "Syncthing returned invalid verification data for folder '$FolderId'."
}

$verifiedPath = Resolve-ConfiguredFolderPath -Path ([string] $verifiedFolder.path)
$verifiedVersioning = $verifiedFolder.PSObject.Properties['versioning'].Value
$verifiedParams = $verifiedVersioning.PSObject.Properties['params'].Value
$verifiedMaxAge = [string] $verifiedParams.PSObject.Properties['maxAge'].Value
$verifiedCleanupInterval = [string] $verifiedVersioning.PSObject.Properties['cleanupIntervalS'].Value

if (-not [string]::Equals($verifiedPath, $resolvedFolderPath, [StringComparison]::OrdinalIgnoreCase) -or
    [string] $verifiedFolder.type -ne 'sendreceive' -or
    [string] $verifiedFolder.label -ne $FolderLabel -or
    [string] $verifiedVersioning.type -ne 'staggered' -or
    $verifiedMaxAge -ne $staggeredMaxAgeSeconds -or
    $verifiedCleanupInterval -ne $versionCleanupIntervalSeconds) {
    throw "Folder '$FolderId' did not pass post-configuration verification."
}

$autoStartMethod = if ($SkipAutoStart) {
    'Skipped'
}
else {
    Enable-SyncthingAutoStart -Installation $installation -ExecutablePath $syncthingExecutable `
        -HomeDirectory $resolvedConfigDirectory
}

[pscustomobject] @{
    DeviceId        = $deviceId
    FolderId        = $FolderId
    FolderLabel     = [string] $verifiedFolder.label
    FolderPath      = $resolvedFolderPath
    MachineFolder   = $machineDirectory
    FolderType      = 'sendreceive'
    VersioningType  = 'staggered'
    VersionMaxAgeS  = [int64] $staggeredMaxAgeSeconds
    ConfigDirectory = $resolvedConfigDirectory
    SyncthingVersion = $syncthingVersion
    WebGuiAddress    = $webGuiAddress
    InstalledBy     = if ($null -ne $installation) { $packageId } else { 'ExistingExecutable' }
    AutoStartMethod = $autoStartMethod
}

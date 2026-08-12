[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$FolderId = 'transporthub-data',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$FolderPath = (Join-Path -Path $env:USERPROFILE -ChildPath 'TransportHub'),

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$ConfigDirectory = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Syncthing'),

    [Parameter()]
    [string]$SyncthingPath,

    [Parameter()]
    [ValidateRange(1, 2147483647)]
    [int]$ExpectedMaxAgeSeconds = 7776000,

    [Parameter()]
    [ValidateRange(1, 2147483647)]
    [int]$ExpectedCleanupIntervalSeconds = 3600,

    [Parameter()]
    [switch]$RequireConnectedPeer
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:PassCount = 0
$script:WarnCount = 0
$script:FailCount = 0
$script:SyncthingExecutable = $null
$script:NormalizedConfigDirectory = $null

function Write-CheckResult {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('PASS', 'WARN', 'FAIL')]
        [string]$Level,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    switch ($Level) {
        'PASS' { $script:PassCount++ }
        'WARN' { $script:WarnCount++ }
        'FAIL' { $script:FailCount++ }
    }

    Write-Output ('[{0}] {1}' -f $Level, $Message)
}

function Write-VerificationSummary {
    Write-Output ''
    Write-Output ('Summary: {0} PASS, {1} WARN, {2} FAIL' -f $script:PassCount, $script:WarnCount, $script:FailCount)
}

function Get-PropertyValue {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$InputObject,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter()]
        [AllowNull()]
        [object]$DefaultValue = $null
    )

    if ($null -eq $InputObject) {
        return $DefaultValue
    }

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $DefaultValue
    }

    return $property.Value
}

function Get-NormalizedPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $expandedPath = [Environment]::ExpandEnvironmentVariables($Path)
    if ($expandedPath -eq '~') {
        $expandedPath = $env:USERPROFILE
    }
    elseif ($expandedPath.StartsWith('~\') -or $expandedPath.StartsWith('~/')) {
        $expandedPath = Join-Path -Path $env:USERPROFILE -ChildPath $expandedPath.Substring(2)
    }

    $fullPath = [IO.Path]::GetFullPath($expandedPath)
    $pathRoot = [IO.Path]::GetPathRoot($fullPath)
    while (($fullPath.Length -gt $pathRoot.Length) -and
           ($fullPath.EndsWith([IO.Path]::DirectorySeparatorChar.ToString()) -or
            $fullPath.EndsWith([IO.Path]::AltDirectorySeparatorChar.ToString()))) {
        $fullPath = $fullPath.Substring(0, $fullPath.Length - 1)
    }

    return $fullPath
}

function Find-SyncthingExecutable {
    if (-not [string]::IsNullOrWhiteSpace($SyncthingPath)) {
        try {
            $explicitPath = Get-NormalizedPath -Path $SyncthingPath
        }
        catch {
            return $null
        }

        if (Test-Path -LiteralPath $explicitPath -PathType Leaf) {
            return $explicitPath
        }

        return $null
    }

    $command = Get-Command -Name 'syncthing.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) {
        return $command.Source
    }

    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidates.Add((Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Programs\Syncthing\syncthing.exe'))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $candidates.Add((Join-Path -Path $env:ProgramFiles -ChildPath 'Syncthing\syncthing.exe'))
    }
    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $candidates.Add((Join-Path -Path $programFilesX86 -ChildPath 'Syncthing\syncthing.exe'))
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    return $null
}

function Invoke-SyncthingCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $output = & $script:SyncthingExecutable @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $text = (@($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine).Trim()

    return [PSCustomObject]@{
        Success  = ($exitCode -eq 0)
        ExitCode = $exitCode
        Text     = $text
    }
}

function Invoke-SyncthingCliJson {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $allArguments = @('cli', ('--home={0}' -f $script:NormalizedConfigDirectory)) + $Arguments
    try {
        $commandResult = Invoke-SyncthingCommand -Arguments $allArguments
        if (-not $commandResult.Success -or [string]::IsNullOrWhiteSpace($commandResult.Text)) {
            return [PSCustomObject]@{
                Success = $false
                Data    = $null
            }
        }

        $data = $commandResult.Text | ConvertFrom-Json
        return [PSCustomObject]@{
            Success = $true
            Data    = $data
        }
    }
    catch {
        return [PSCustomObject]@{
            Success = $false
            Data    = $null
        }
    }
}

function Test-EnabledOption {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Options,

        [Parameter(Mandatory = $true)]
        [string]$PropertyName,

        [Parameter(Mandatory = $true)]
        [string]$DisplayName
    )

    $value = Get-PropertyValue -InputObject $Options -Name $PropertyName
    if ($value -eq $true) {
        Write-CheckResult -Level PASS -Message ('{0} is enabled.' -f $DisplayName)
    }
    elseif ($value -eq $false) {
        Write-CheckResult -Level WARN -Message ('{0} is disabled.' -f $DisplayName)
    }
    else {
        Write-CheckResult -Level WARN -Message ('Unable to determine whether {0} is enabled.' -f $DisplayName)
    }
}

function Test-LogonTask {
    $scheduledTaskCommand = Get-Command -Name 'Get-ScheduledTask' -ErrorAction SilentlyContinue
    if ($null -eq $scheduledTaskCommand) {
        Write-CheckResult -Level WARN -Message 'The ScheduledTasks module is unavailable; logon startup was not verified.'
        return
    }

    try {
        $tasks = @(Get-ScheduledTask -ErrorAction Stop | Where-Object {
            $_.TaskName -like 'Start Syncthing at logon*'
        })

        if ($tasks.Count -eq 0) {
            Write-CheckResult -Level WARN -Message 'No Syncthing logon scheduled task was found.'
            return
        }

        $enabledTasks = @($tasks | Where-Object { $_.State.ToString() -ne 'Disabled' })
        if ($enabledTasks.Count -gt 0) {
            $taskNames = @($enabledTasks | ForEach-Object { $_.TaskName }) -join ', '
            Write-CheckResult -Level PASS -Message ('Syncthing logon task is enabled: {0}' -f $taskNames)
        }
        else {
            Write-CheckResult -Level WARN -Message 'Syncthing logon scheduled task exists but is disabled.'
        }
    }
    catch {
        Write-CheckResult -Level WARN -Message 'Unable to inspect Syncthing logon scheduled tasks.'
    }
}

function Test-FirewallRule {
    $firewallCommand = Get-Command -Name 'Get-NetFirewallRule' -ErrorAction SilentlyContinue
    if ($null -eq $firewallCommand) {
        Write-CheckResult -Level WARN -Message 'The NetSecurity module is unavailable; the firewall rule was not verified.'
        return
    }

    try {
        $rules = @(Get-NetFirewallRule -ErrorAction Stop | Where-Object {
            $_.DisplayName -like '*Syncthing*'
        })
        $allowRules = @($rules | Where-Object {
            ($_.Enabled.ToString() -eq 'True') -and
            ($_.Direction.ToString() -eq 'Inbound') -and
            ($_.Action.ToString() -eq 'Allow')
        })

        if ($allowRules.Count -gt 0) {
            $ruleNames = @($allowRules | ForEach-Object { $_.DisplayName } | Select-Object -Unique) -join ', '
            Write-CheckResult -Level PASS -Message ('Enabled inbound Syncthing firewall rule found: {0}' -f $ruleNames)
        }
        elseif ($rules.Count -gt 0) {
            Write-CheckResult -Level WARN -Message 'Syncthing firewall rule exists, but no enabled inbound allow rule was found.'
        }
        else {
            Write-CheckResult -Level WARN -Message 'No Syncthing firewall rule was found.'
        }
    }
    catch {
        Write-CheckResult -Level WARN -Message 'Unable to inspect Windows Firewall rules.'
    }
}

Write-Output ('TransportHub Syncthing verification - {0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    Write-CheckResult -Level FAIL -Message 'This verification script supports Windows only.'
    Write-VerificationSummary
    exit 1
}

try {
    $normalizedFolderPath = Get-NormalizedPath -Path $FolderPath
    $script:NormalizedConfigDirectory = Get-NormalizedPath -Path $ConfigDirectory
}
catch {
    Write-CheckResult -Level FAIL -Message 'FolderPath or ConfigDirectory is not a valid Windows path.'
    Write-VerificationSummary
    exit 1
}

$script:SyncthingExecutable = Find-SyncthingExecutable
if ([string]::IsNullOrWhiteSpace($script:SyncthingExecutable)) {
    if ([string]::IsNullOrWhiteSpace($SyncthingPath)) {
        Write-CheckResult -Level FAIL -Message 'syncthing.exe was not found in PATH or a standard installation directory.'
    }
    else {
        Write-CheckResult -Level FAIL -Message 'The path supplied with -SyncthingPath does not point to syncthing.exe.'
    }
    Write-VerificationSummary
    exit 1
}
Write-CheckResult -Level PASS -Message ('Syncthing executable found: {0}' -f $script:SyncthingExecutable)

try {
    $versionResult = Invoke-SyncthingCommand -Arguments @('version')
    if ($versionResult.Success -and -not [string]::IsNullOrWhiteSpace($versionResult.Text)) {
        $versionLine = ($versionResult.Text -split '[\r\n]+' | Select-Object -First 1).Trim()
        Write-CheckResult -Level PASS -Message ('Syncthing version command succeeded: {0}' -f $versionLine)
    }
    else {
        Write-CheckResult -Level FAIL -Message 'Syncthing version command failed.'
    }
}
catch {
    Write-CheckResult -Level FAIL -Message 'Syncthing version command could not be executed.'
}

if (Test-Path -LiteralPath $script:NormalizedConfigDirectory -PathType Container) {
    Write-CheckResult -Level PASS -Message ('Configuration directory exists: {0}' -f $script:NormalizedConfigDirectory)
}
else {
    Write-CheckResult -Level FAIL -Message ('Configuration directory is missing: {0}' -f $script:NormalizedConfigDirectory)
}

$systemResult = Invoke-SyncthingCliJson -Arguments @('show', 'system')
if (-not $systemResult.Success) {
    Write-CheckResult -Level FAIL -Message 'Syncthing CLI could not reach the running local API.'
    Test-LogonTask
    Test-FirewallRule
    Write-VerificationSummary
    exit 1
}
Write-CheckResult -Level PASS -Message 'Syncthing CLI reached the running local API.'

$deviceId = [string](Get-PropertyValue -InputObject $systemResult.Data -Name 'myID' -DefaultValue '')
if ($deviceId -match '^[A-Z2-7]{7}(?:-[A-Z2-7]{7}){7}$') {
    Write-CheckResult -Level PASS -Message ('Local Device ID: {0}' -f $deviceId)
}
elseif (-not [string]::IsNullOrWhiteSpace($deviceId)) {
    Write-CheckResult -Level WARN -Message ('A non-standard local Device ID was returned: {0}' -f $deviceId)
}
else {
    Write-CheckResult -Level FAIL -Message 'The local Device ID is missing from Syncthing status.'
}

$folderResult = Invoke-SyncthingCliJson -Arguments @('config', 'folders', $FolderId, 'dump-json')
if (-not $folderResult.Success) {
    Write-CheckResult -Level FAIL -Message ('Syncthing folder "{0}" was not found or could not be read.' -f $FolderId)
}
else {
    $folder = $folderResult.Data
    Write-CheckResult -Level PASS -Message ('Syncthing folder configuration exists: {0}' -f $FolderId)

    $configuredPath = [string](Get-PropertyValue -InputObject $folder -Name 'path' -DefaultValue '')
    try {
        $normalizedConfiguredPath = Get-NormalizedPath -Path $configuredPath
        if ([string]::Equals($normalizedConfiguredPath, $normalizedFolderPath, [StringComparison]::OrdinalIgnoreCase)) {
            Write-CheckResult -Level PASS -Message ('Folder path matches: {0}' -f $normalizedFolderPath)
        }
        else {
            Write-CheckResult -Level FAIL -Message ('Folder path mismatch. Expected "{0}" but found "{1}".' -f $normalizedFolderPath, $normalizedConfiguredPath)
        }
    }
    catch {
        Write-CheckResult -Level FAIL -Message ('Syncthing returned an invalid folder path for "{0}".' -f $FolderId)
    }

    $folderType = [string](Get-PropertyValue -InputObject $folder -Name 'type' -DefaultValue '')
    if ($folderType -eq 'sendreceive') {
        Write-CheckResult -Level PASS -Message 'Folder type is sendreceive.'
    }
    else {
        Write-CheckResult -Level FAIL -Message ('Folder type is "{0}"; expected "sendreceive".' -f $folderType)
    }

    $paused = Get-PropertyValue -InputObject $folder -Name 'paused'
    if ($paused -eq $false) {
        Write-CheckResult -Level PASS -Message 'Folder is not paused.'
    }
    elseif ($paused -eq $true) {
        Write-CheckResult -Level FAIL -Message 'Folder is paused.'
    }
    else {
        Write-CheckResult -Level FAIL -Message 'Folder paused state could not be determined.'
    }

    $versioning = Get-PropertyValue -InputObject $folder -Name 'versioning'
    $versioningType = [string](Get-PropertyValue -InputObject $versioning -Name 'type' -DefaultValue '')
    if ($versioningType -eq 'staggered') {
        Write-CheckResult -Level PASS -Message 'Staggered file versioning is enabled.'
    }
    else {
        Write-CheckResult -Level FAIL -Message ('Versioning type is "{0}"; expected "staggered".' -f $versioningType)
    }

    $versioningParams = Get-PropertyValue -InputObject $versioning -Name 'params'
    $maxAgeText = [string](Get-PropertyValue -InputObject $versioningParams -Name 'maxAge' -DefaultValue '')
    $maxAgeValue = [long]0
    if ([long]::TryParse($maxAgeText, [ref]$maxAgeValue) -and $maxAgeValue -eq $ExpectedMaxAgeSeconds) {
        Write-CheckResult -Level PASS -Message ('Version retention maxAge is {0} seconds.' -f $maxAgeValue)
    }
    else {
        Write-CheckResult -Level FAIL -Message ('Version retention maxAge is "{0}"; expected {1} seconds.' -f $maxAgeText, $ExpectedMaxAgeSeconds)
    }

    $cleanupText = [string](Get-PropertyValue -InputObject $versioning -Name 'cleanupIntervalS' -DefaultValue '')
    $cleanupValue = [long]0
    if ([long]::TryParse($cleanupText, [ref]$cleanupValue) -and $cleanupValue -eq $ExpectedCleanupIntervalSeconds) {
        Write-CheckResult -Level PASS -Message ('Version cleanup interval is {0} seconds.' -f $cleanupValue)
    }
    else {
        Write-CheckResult -Level FAIL -Message ('Version cleanup interval is "{0}"; expected {1} seconds.' -f $cleanupText, $ExpectedCleanupIntervalSeconds)
    }
}

if (Test-Path -LiteralPath $normalizedFolderPath -PathType Container) {
    Write-CheckResult -Level PASS -Message ('Shared directory exists: {0}' -f $normalizedFolderPath)

    $markerPath = Join-Path -Path $normalizedFolderPath -ChildPath '.stfolder'
    if (Test-Path -LiteralPath $markerPath) {
        Write-CheckResult -Level PASS -Message ('Syncthing folder marker exists: {0}' -f $markerPath)
    }
    else {
        Write-CheckResult -Level FAIL -Message ('Syncthing folder marker is missing: {0}' -f $markerPath)
    }

    if (-not [string]::IsNullOrWhiteSpace($env:COMPUTERNAME)) {
        $deviceDropDirectory = Join-Path -Path $normalizedFolderPath -ChildPath $env:COMPUTERNAME
        if (Test-Path -LiteralPath $deviceDropDirectory -PathType Container) {
            Write-CheckResult -Level PASS -Message ('Per-computer drop directory exists: {0}' -f $deviceDropDirectory)
        }
        else {
            Write-CheckResult -Level WARN -Message ('Per-computer drop directory is missing: {0}' -f $deviceDropDirectory)
        }
    }
}
else {
    Write-CheckResult -Level FAIL -Message ('Shared directory is missing: {0}' -f $normalizedFolderPath)
}

$optionsResult = Invoke-SyncthingCliJson -Arguments @('config', 'options', 'dump-json')
if (-not $optionsResult.Success) {
    Write-CheckResult -Level FAIL -Message 'Syncthing network options could not be read.'
}
else {
    Test-EnabledOption -Options $optionsResult.Data -PropertyName 'globalAnnounceEnabled' -DisplayName 'Global discovery'
    Test-EnabledOption -Options $optionsResult.Data -PropertyName 'localAnnounceEnabled' -DisplayName 'Local discovery'
    Test-EnabledOption -Options $optionsResult.Data -PropertyName 'natEnabled' -DisplayName 'NAT traversal'
    Test-EnabledOption -Options $optionsResult.Data -PropertyName 'relaysEnabled' -DisplayName 'Relay fallback'
}

$connectionsResult = Invoke-SyncthingCliJson -Arguments @('show', 'connections')
if (-not $connectionsResult.Success) {
    Write-CheckResult -Level FAIL -Message 'Remote device connections could not be read.'
}
else {
    $connectionMap = Get-PropertyValue -InputObject $connectionsResult.Data -Name 'connections'
    $connectionProperties = @()
    if ($null -ne $connectionMap) {
        $connectionProperties = @($connectionMap.PSObject.Properties)
    }

    if ($connectionProperties.Count -gt 0) {
        Write-CheckResult -Level PASS -Message ('Configured remote devices visible to the connection API: {0}' -f $connectionProperties.Count)
    }
    else {
        Write-CheckResult -Level WARN -Message 'No configured remote devices are visible to the connection API.'
    }

    $connectedProperties = @($connectionProperties | Where-Object {
        (Get-PropertyValue -InputObject $_.Value -Name 'connected' -DefaultValue $false) -eq $true
    })

    if ($connectedProperties.Count -gt 0) {
        $relayCount = @($connectedProperties | Where-Object {
            ([string](Get-PropertyValue -InputObject $_.Value -Name 'type' -DefaultValue '')) -match 'relay'
        }).Count
        $directCount = $connectedProperties.Count - $relayCount
        $connectionTypes = @($connectedProperties | ForEach-Object {
            [string](Get-PropertyValue -InputObject $_.Value -Name 'type' -DefaultValue 'unknown')
        } | Sort-Object -Unique) -join ', '

        Write-CheckResult -Level PASS -Message ('Connected peers: {0} ({1} direct, {2} relay; types: {3})' -f $connectedProperties.Count, $directCount, $relayCount, $connectionTypes)
    }
    elseif ($RequireConnectedPeer) {
        Write-CheckResult -Level FAIL -Message 'No remote peer is connected, and -RequireConnectedPeer was specified.'
    }
    else {
        Write-CheckResult -Level WARN -Message 'No remote peer is currently connected.'
    }
}

Test-LogonTask
Test-FirewallRule

Write-VerificationSummary
if ($script:FailCount -gt 0) {
    exit 1
}

exit 0

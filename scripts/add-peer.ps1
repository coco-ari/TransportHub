[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DeviceId,

    [string]$DeviceName = "",

    [string]$FolderId = "transporthub-data",

    [string]$ConfigDirectory = (Join-Path $env:LOCALAPPDATA "Syncthing"),

    [string]$SyncthingPath = ""
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

function Resolve-SyncthingExecutable {
    param([string]$RequestedPath)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidates += $RequestedPath
    }
    $candidates += (Join-Path $env:LOCALAPPDATA "Programs\Syncthing\syncthing.exe")

    $command = Get-Command syncthing.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        $candidates += $command.Source
    }

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "Syncthing was not found. Run scripts\bootstrap.ps1 first."
}

function Invoke-SyncthingCli {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $allArguments = @("cli", "--home=$ConfigDirectory") + $Arguments
    $output = & $script:SyncthingExecutable @allArguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $details = ($output | Out-String).Trim()
        throw "Syncthing CLI failed ($LASTEXITCODE): $details"
    }
    return $output
}

function Convert-ToLineList {
    param([object[]]$InputObject)

    return @($InputObject | ForEach-Object { $_.ToString().Trim() } | Where-Object { $_ -ne "" })
}

$DeviceId = $DeviceId.Trim().ToUpperInvariant()
if ($DeviceId -notmatch "^(?:[A-Z2-7]{7}-){7}[A-Z2-7]{7}$") {
    throw "DeviceId is not a valid full Syncthing device ID."
}
if ([string]::IsNullOrWhiteSpace($FolderId)) {
    throw "FolderId must not be empty."
}
if (-not (Test-Path -LiteralPath $ConfigDirectory -PathType Container)) {
    throw "Syncthing config directory does not exist: $ConfigDirectory"
}

$script:SyncthingExecutable = Resolve-SyncthingExecutable -RequestedPath $SyncthingPath

$system = (Invoke-SyncthingCli -Arguments @("show", "system") | Out-String | ConvertFrom-Json)
if ($system.myID -eq $DeviceId) {
    throw "The supplied DeviceId belongs to this computer. Supply the other computer's ID."
}

$folders = Convert-ToLineList -InputObject @(Invoke-SyncthingCli -Arguments @("config", "folders", "list"))
if ($folders -notcontains $FolderId) {
    throw "Folder '$FolderId' does not exist. Run scripts\bootstrap.ps1 first."
}

$devices = Convert-ToLineList -InputObject @(Invoke-SyncthingCli -Arguments @("config", "devices", "list"))
if ($devices -notcontains $DeviceId) {
    $addDeviceArguments = @("config", "devices", "add", "--device-id", $DeviceId)
    if (-not [string]::IsNullOrWhiteSpace($DeviceName)) {
        $addDeviceArguments += @("--name", $DeviceName.Trim())
    }
    Invoke-SyncthingCli -Arguments $addDeviceArguments | Out-Null
    Write-Host "[ADDED] Remote device $DeviceId"
} else {
    Write-Host "[OK] Remote device already exists: $DeviceId"
}

$folderDevices = Convert-ToLineList -InputObject @(Invoke-SyncthingCli -Arguments @("config", "folders", $FolderId, "devices", "list"))
if ($folderDevices -notcontains $DeviceId) {
    Invoke-SyncthingCli -Arguments @(
        "config", "folders", $FolderId, "devices", "add", "--device-id", $DeviceId
    ) | Out-Null
    Write-Host "[SHARED] Folder '$FolderId' is now shared with $DeviceId"
} else {
    Write-Host "[OK] Folder '$FolderId' is already shared with $DeviceId"
}

$finalDevices = Convert-ToLineList -InputObject @(Invoke-SyncthingCli -Arguments @("config", "folders", $FolderId, "devices", "list"))
if ($finalDevices -notcontains $DeviceId) {
    throw "Verification failed: the remote device was not added to folder '$FolderId'."
}

Write-Host ""
Write-Host "Pair the reverse direction on the other computer if it has not accepted the device and folder yet."
Write-Host "Local device ID: $($system.myID)"
Write-Host "Remote device ID: $DeviceId"

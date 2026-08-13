[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ExpectedExecutable
)

$ErrorActionPreference = 'Stop'

function Resolve-NormalizedPath {
    param([Parameter(Mandatory)][string] $Path)
    return [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}

$expected = Resolve-NormalizedPath -Path $ExpectedExecutable
foreach ($process in @(Get-Process -Name 'TransportHub' -ErrorAction SilentlyContinue)) {
    try {
        $actual = Resolve-NormalizedPath -Path $process.Path
        if (-not [string]::Equals($actual, $expected, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
        [void] $process.WaitForExit(5000)
    }
    catch [System.ComponentModel.Win32Exception] {
        throw "无法检查或停止 TransportHub 进程 $($process.Id)：$($_.Exception.Message)"
    }
}

if (Get-Process -Name 'TransportHub' -ErrorAction SilentlyContinue | Where-Object {
        try {
            [string]::Equals((Resolve-NormalizedPath -Path $_.Path), $expected, [StringComparison]::OrdinalIgnoreCase)
        }
        catch { $false }
    }) {
    throw 'TransportHub 仍在运行，已中止卸载以避免留下不完整安装。'
}

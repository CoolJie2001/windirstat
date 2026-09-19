[CmdletBinding()]
param(
    [string] $ExecutablePath,
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'

$menuKey = 'HKCU:\Software\Classes\Directory\shell\DiskScope'

if ($Uninstall) {
    if (Test-Path -LiteralPath $menuKey) {
        Remove-Item -LiteralPath $menuKey -Recurse -Force
    }
    Write-Host 'DiskScope folder context menu removed for the current user.'
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $PSScriptRoot '..\src\WdsShell.App\bin\Release\net10.0\DiskScope.exe'
}

$resolvedPath = (Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop).Path
if ([IO.Path]::GetExtension($resolvedPath) -ine '.exe') {
    throw "ExecutablePath must point to DiskScope.exe: $resolvedPath"
}

New-Item -Path $menuKey -Force | Out-Null
Set-ItemProperty -LiteralPath $menuKey -Name '(Default)' -Value '使用 DiskScope 分析空间'
Set-ItemProperty -LiteralPath $menuKey -Name 'MUIVerb' -Value '使用 DiskScope 分析空间'
Set-ItemProperty -LiteralPath $menuKey -Name 'Icon' -Value $resolvedPath

$commandKey = Join-Path $menuKey 'command'
New-Item -Path $commandKey -Force | Out-Null
$command = '"{0}" --scan-folder "%1"' -f $resolvedPath
Set-ItemProperty -LiteralPath $commandKey -Name '(Default)' -Value $command

Write-Host "DiskScope folder context menu registered for the current user: $resolvedPath"

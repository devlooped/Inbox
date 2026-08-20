#Requires -Version 7
<#
.SYNOPSIS
  Pack the local WhatsBox 42.42.42 pointer + current-RID packages, publish the
  Debug demo against them, and start it.

.DESCRIPTION
  Debug WhatsDemo restores WhatsBox 42.42.42 from repo bin/. Packs both
  pointer + current-RID packages (WhatsBox and the wd tool), then installs
  `wd` to .tools and runs `wd` with the repo as cwd (.store).
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$Configuration = 'Debug'
$Version = '42.42.42'
$WhatsBox = Join-Path $Root 'src/WhatsBox/WhatsBox.csproj'
$Demo = Join-Path $Root 'src/WhatsDemo/WhatsDemo.csproj'

function Get-PackRid {
    $os = if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Windows)) { 'win' }
    elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Linux)) { 'linux' }
    elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::OSX)) { 'osx' }
    else { throw 'Unsupported OS for WhatsBox RID packages.' }

    $cpu = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        'X64' { 'x64' }
        'Arm64' { 'arm64' }
        default { throw "Unsupported architecture '$_' for WhatsBox RID packages." }
    }

    "$os-$cpu"
}

function Invoke-Dotnet {
    param(
        [Parameter(Mandatory, ValueFromRemainingArguments)]
        [string[]] $Arguments
    )
    Write-Host "dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Remove-CachedPackage {
    param([string] $PackageId, [string] $PackageVersion)
    $listed = dotnet nuget locals global-packages --list
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet nuget locals global-packages --list failed.'
    }
    $prefix = 'global-packages: '
    $line = @($listed) | Where-Object { $_ -like "$prefix*" } | Select-Object -First 1
    if (-not $line) {
        return
    }

    $globalPackages = $line.Substring($prefix.Length).Trim()
    $dir = Join-Path $globalPackages ($PackageId.ToLowerInvariant()) $PackageVersion
    if (Test-Path $dir) {
        Write-Host "Removing cached $PackageId $PackageVersion" -ForegroundColor DarkGray
        Remove-Item -Recurse -Force $dir
    }
}

$rid = Get-PackRid
$toolPath = Join-Path $Root '.tools'
$nugetConfig = Join-Path $Root 'src/WhatsDemo/nuget.config'

Write-Host "Packing WhatsBox and wd $Version (pointer + $rid)" -ForegroundColor Cyan
Invoke-Dotnet pack $WhatsBox -c $Configuration --nologo
Invoke-Dotnet pack $WhatsBox -c $Configuration -r $rid --nologo
Invoke-Dotnet pack $Demo -c $Configuration --nologo
Invoke-Dotnet pack $Demo -c $Configuration -r $rid --nologo

foreach ($id in @('WhatsBox', "WhatsBox.$rid", 'wd', "wd.$rid")) {
    Remove-CachedPackage -PackageId $id -PackageVersion $Version
}

Write-Host "Installing wd $Version → $toolPath" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $toolPath | Out-Null
$install = @(
    'tool', 'install', 'wd',
    '--tool-path', $toolPath,
    '--configfile', $nugetConfig,
    '--version', $Version
)
& dotnet @install
if ($LASTEXITCODE -ne 0) {
    Invoke-Dotnet tool update wd --tool-path $toolPath --configfile $nugetConfig --version $Version
}

$wd = @('wd.exe', 'wd.cmd', 'wd') |
    ForEach-Object { Join-Path $toolPath $_ } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1
if (-not $wd) {
    throw "Tool command not found in '$toolPath'."
}

Write-Host "Starting wd (cwd $Root)" -ForegroundColor Cyan
Set-Location $Root
& $wd
if ($LASTEXITCODE -ne 0) {
    throw "wd exited with code $LASTEXITCODE."
}

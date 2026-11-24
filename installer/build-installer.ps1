# Builds and tests the Windows installer.
#   pwsh installer/build-installer.ps1 -Version 0.3.1 [-SkipTest]
# Needs the .NET 8 SDK and Inno Setup 6 (choco install innosetup, or winget install JRSoftware.InnoSetup).
param(
    [string]$Version = "0.0.0",
    [string]$Rid = "win-x64",
    [switch]$SkipTest
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "out\overseer-$Rid"
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null

Write-Host "Publishing $Rid $Version"
dotnet publish (Join-Path $root 'src\Overseer.App\Overseer.App.csproj') -c Release -r $Rid --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: choco install innosetup  (or winget install JRSoftware.InnoSetup)" }

Write-Host "Building installer with $iscc"
& $iscc "/DAppVersion=$Version" "/DSourceDir=$publish" "/DOutputDir=$dist" (Join-Path $PSScriptRoot 'overseer.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
$setup = Join-Path $dist "Overseer-Setup-$Version-x64.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
"$hash  $(Split-Path -Leaf $setup)" | Out-File -Encoding ascii "$setup.sha256"
Write-Host "Built $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"

if ($SkipTest) { return }

# install silently for the current user, check it runs, uninstall silently
Write-Host "Testing install"
$p = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER' -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Installer exited with $($p.ExitCode)" }
$app = Join-Path $env:LOCALAPPDATA 'Programs\Overseer\Overseer.exe'
if (-not (Test-Path $app)) { throw "Overseer.exe not found after install" }
$outFile = Join-Path $env:TEMP 'overseer-version.txt'
$v = Start-Process $app -ArgumentList '--version' -Wait -PassThru -NoNewWindow -RedirectStandardOutput $outFile
$printed = (Get-Content $outFile -Raw).Trim()
Write-Host "Installed app says: $printed"
if ($v.ExitCode -ne 0 -or $printed -notlike "Overseer $Version*") { throw "Unexpected --version output: $printed" }

Write-Host "Testing an in-app update (same arguments as Updater.InstallerStart)"
$log = Join-Path $env:TEMP 'overseer-update-test.log'
Remove-Item $log -ErrorAction SilentlyContinue
$p = Start-Process $setup -ArgumentList '/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CLOSEAPPLICATIONS', '/RELAUNCH', '/CURRENTUSER', "/LOG=$log" -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Update install exited with $($p.ExitCode)" }
$text = Get-Content $log -Raw
if ($text -notmatch '-- Run entry --[\s\S]*?Filename: .*Overseer\.exe') { throw "The update install didn't start Overseer again (no run entry in $log)" }
Write-Host "Update install restarted Overseer"
Get-Process Overseer -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1

Write-Host "Testing uninstall"
$uninstaller = Join-Path $env:LOCALAPPDATA 'Programs\Overseer\unins000.exe'
$u = Start-Process $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
if ($u.ExitCode -ne 0) { throw "Uninstaller exited with $($u.ExitCode)" }
Start-Sleep 2
if (Test-Path $app) { throw "Overseer.exe still present after uninstall" }
Write-Host "Installer OK"

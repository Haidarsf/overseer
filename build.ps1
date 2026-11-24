# Builds ClaudeMonitor.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No installs needed.
$ErrorActionPreference = 'Stop'
$fw  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not (Test-Path "$here\assets\app.ico")) { & "$here\assets\make-icon.ps1" }
& "$fw\csc.exe" /nologo /target:winexe /optimize+ "/out:$here\ClaudeMonitor.exe" `
  "/win32icon:$here\assets\app.ico" "/resource:$here\assets\app.ico,ClaudeMonitor.app.ico" `
  "/r:$wpf\PresentationFramework.dll" "/r:$wpf\PresentationCore.dll" "/r:$wpf\WindowsBase.dll" `
  "/r:$fw\System.Xaml.dll" "/r:$fw\System.Web.Extensions.dll" "/r:$fw\System.Windows.Forms.dll" "/r:$fw\System.Drawing.dll" `
  "$here\src\Data.cs" "$here\src\Actions.cs" "$here\src\MainWindow.cs" "$here\src\ControlView.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $here\ClaudeMonitor.exe"

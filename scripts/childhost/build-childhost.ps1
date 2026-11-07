# dsh-rdp-wrapper : compile ChildHost.cs -> rdp-child-host.exe (.NET Framework 4.x, C# 5).
# Needed after editing ChildHost.cs; start-childhost.ps1 requires the exe.
param(
  [string]$OutFile = (Join-Path $PSScriptRoot 'rdp-child-host.exe')
)
$ErrorActionPreference = 'Stop'
$csc = @(
  "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
  "$env:SystemRoot\Microsoft.NET\Framework\v4.0.30319\csc.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw 'csc.exe (.NET Framework 4.x) not found' }

& $csc -nologo -target:winexe -platform:anycpu `
  -r:System.dll -r:System.Core.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:Microsoft.CSharp.dll `
  "-out:$OutFile" (Join-Path $PSScriptRoot 'ChildHost.cs')
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
Get-Item $OutFile | Select-Object FullName, Length, LastWriteTime

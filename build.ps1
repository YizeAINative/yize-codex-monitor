param([string]$OutputDirectory = 'dist')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$outDir = Join-Path $projectRoot $OutputDirectory
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$refs = @('System.dll','System.Core.dll','System.Xaml.dll','System.Management.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkRoot $_) }
$refs += @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path (Join-Path $frameworkRoot 'WPF') $_) }
$arguments = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/utf8output','/codepage:65001',('/out:' + (Join-Path $outDir 'CodexFloat.exe')),('/win32manifest:' + (Join-Path $projectRoot 'app.manifest')))
if (Test-Path -LiteralPath (Join-Path $projectRoot 'app.ico')) { $arguments += '/win32icon:' + (Join-Path $projectRoot 'app.ico') }
& $compiler @arguments @refs (Join-Path $projectRoot 'Core.cs') (Join-Path $projectRoot 'WorkBuddy.cs') (Join-Path $projectRoot 'Claude.cs') (Join-Path $projectRoot 'App.cs')
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
if (Test-Path -LiteralPath (Join-Path $projectRoot 'Tests.cs')) {
  & $compiler /nologo /target:exe /utf8output /codepage:65001 ('/out:' + (Join-Path $outDir 'CoreTests.exe')) @refs (Join-Path $projectRoot 'Core.cs') (Join-Path $projectRoot 'WorkBuddy.cs') (Join-Path $projectRoot 'Claude.cs') (Join-Path $projectRoot 'Tests.cs')
  if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
}
Get-Item -LiteralPath (Join-Path $outDir 'CodexFloat.exe') | Select-Object FullName,Length,LastWriteTime

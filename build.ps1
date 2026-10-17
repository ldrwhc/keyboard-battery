param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$outputFile = Join-Path $OutputDirectory 'V98Battery.exe'
& $compiler /nologo /target:winexe /optimize+ /platform:anycpu /out:$outputFile /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "$PSScriptRoot\Program.cs" "$PSScriptRoot\Hid.cs" "$PSScriptRoot\DeviceNotifications.cs" "$PSScriptRoot\AssemblyInfo.cs"
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
Write-Host "已生成 $outputFile"

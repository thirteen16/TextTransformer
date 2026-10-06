param([string]$OutputPath = (Join-Path $PSScriptRoot 'TextTransformer.exe'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
if (-not (Test-Path -LiteralPath $framework)) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\WPF'
}
& $compiler /nologo /target:winexe /optimize+ /platform:anycpu "/win32icon:$PSScriptRoot\TextTransformer.ico" "/resource:$PSScriptRoot\TextTransformer.ico,TextTransformer.AppIcon" "/out:$OutputPath" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:"$framework\UIAutomationClient.dll" /reference:"$framework\UIAutomationTypes.dll" "$PSScriptRoot\TextTransformer.cs"
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
Write-Host "已生成 $OutputPath"

# FrontSwitcher 配布用ビルド（自己完結・単一exe）
# インターネット接続が必要（初回に .NET ランタイムを取得するため）。
# 実行: PowerShell で  .\publish.ps1
$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'FrontSwitcher.csproj'

dotnet publish $proj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true

$exe = Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\win-x64\publish\FrontSwitcher.exe'
if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host ""
    Write-Host "完成: $exe  ($size MB)" -ForegroundColor Green
    Write-Host "この exe 1個を配布すれば、ランタイム未導入のPCでも動きます。"
} else {
    Write-Host "publish に失敗しました。出力を確認してください。" -ForegroundColor Red
}

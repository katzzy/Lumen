param(
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\Lumen\Lumen.csproj"
$out = Join-Path $root "dist\Lumen"

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $candidate = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
    if (Test-Path $candidate) { $dotnet = $candidate }
}
if (-not $dotnet) {
    throw "未找到 dotnet，请先安装 .NET 10 SDK"
}

if (Test-Path $out) {
    Remove-Item $out -Recurse -Force
}

& $dotnet publish $project -c Release -r win-x64 --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Remove-Item (Join-Path $out "Lumen.pdb") -Force -ErrorAction SilentlyContinue

New-Item -ItemType File -Path (Join-Path $out "portable.flag") -Force | Out-Null
Copy-Item (Join-Path $root "README.md") (Join-Path $out "README.md") -Force
if (Test-Path (Join-Path $root "screenshot.png")) {
    Copy-Item (Join-Path $root "screenshot.png") (Join-Path $out "screenshot.png") -Force
}

if (-not $NoZip) {
    $zip = Join-Path $root "dist\Lumen-1.1.2-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "Package: $zip"
}

Write-Host "Built: $out"

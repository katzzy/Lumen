param(
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\Lumen\Lumen.csproj"
$out = Join-Path $root "dist\Lumen"
$version = ([xml](Get-Content $project -Raw -Encoding UTF8)).Project.PropertyGroup.Version

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
if (Test-Path (Join-Path $root "LICENSE")) {
    Copy-Item (Join-Path $root "LICENSE") (Join-Path $out "LICENSE") -Force
}
if (Test-Path (Join-Path $root "screenshot.png")) {
    Copy-Item (Join-Path $root "screenshot.png") (Join-Path $out "screenshot.png") -Force
}

if (-not $NoZip) {
    $zip = Join-Path $root "dist\Lumen-$version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -CompressionLevel Optimal

    $zipName = Split-Path -Leaf $zip
    $zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $nl = "`r`n"
    [System.IO.File]::WriteAllText(
        "$zip.sha256",
        ("{0}  {1}{2}" -f $zipHash, $zipName, $nl),
        [System.Text.Encoding]::ASCII)

    $sums = @()
    $shot = Join-Path $root "screenshot.png"
    if (Test-Path $shot) {
        $shotHash = (Get-FileHash $shot -Algorithm SHA256).Hash.ToLowerInvariant()
        $sums += "{0}  screenshot.png" -f $shotHash
    }
    $sums += "{0}  {1}" -f $zipHash, $zipName
    [System.IO.File]::WriteAllText(
        (Join-Path $root "dist\SHA256SUMS.txt"),
        (($sums -join $nl) + $nl),
        [System.Text.Encoding]::ASCII)

    Write-Host "Package: $zip"
    Write-Host "SHA256:  $zipHash"
}

Write-Host "Built: $out"

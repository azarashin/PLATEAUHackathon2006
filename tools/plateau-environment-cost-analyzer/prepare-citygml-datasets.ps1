[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('kyoto', 'maizuru', 'fujisawa', 'saitama')]
    [string] $AreaId
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$manifestPath = Join-Path $repoRoot "data\plateau-citygml-manifests\$AreaId.json"
$manifest = Get-Content -Raw -Encoding UTF8 $manifestPath | ConvertFrom-Json
$archiveDirectory = Join-Path $repoRoot 'data\raw\plateau-zips'
New-Item -ItemType Directory -Force -Path $archiveDirectory | Out-Null

foreach ($dataset in $manifest.datasets) {
    $archivePath = Join-Path $archiveDirectory $dataset.archiveFile
    $extractPath = Join-Path $repoRoot $dataset.extractPath
    if (-not (Test-Path -LiteralPath $archivePath)) {
        Write-Host "Downloading $($dataset.id) ($($dataset.municipality), $($dataset.year))..."
        & curl.exe --fail --location --continue-at - --output $archivePath $dataset.url
        if ($LASTEXITCODE -ne 0) { throw "CityGML download failed: $($dataset.id)" }
    }

    $actualBytes = (Get-Item -LiteralPath $archivePath).Length
    $catalogBytes = [long]$dataset.catalogZipBytes
    if ($actualBytes -lt [long]($catalogBytes * 0.99) -or $actualBytes -gt [long]($catalogBytes * 1.01)) {
        throw "Archive size is outside the permitted 1% catalog tolerance for $($dataset.id): catalog $catalogBytes, actual $actualBytes. Delete the incomplete archive and run again."
    }
    & tar.exe -tf $archivePath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "CityGML archive integrity check failed: $($dataset.id)" }

    $udxPath = Join-Path $extractPath 'udx'
    if (-not (Test-Path -LiteralPath $udxPath)) {
        if ((Test-Path -LiteralPath $extractPath) -and (Get-ChildItem -Force -LiteralPath $extractPath | Select-Object -First 1)) {
            throw "Extraction destination is not empty: $extractPath. Resolve it before extracting to avoid mixing datasets."
        }
        New-Item -ItemType Directory -Force -Path $extractPath | Out-Null
        Write-Host "Extracting $($dataset.id) to $extractPath..."
        & tar.exe -xf $archivePath -C $extractPath
        if ($LASTEXITCODE -ne 0) { throw "CityGML extraction failed: $($dataset.id)" }
    }
    if (-not (Test-Path -LiteralPath $udxPath)) { throw "The extracted CityGML has no udx directory: $extractPath" }

    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
    Write-Host "CITYGML_READY id=$($dataset.id) bytes=$actualBytes sha256=$hash root=$($dataset.extractPath)"
}

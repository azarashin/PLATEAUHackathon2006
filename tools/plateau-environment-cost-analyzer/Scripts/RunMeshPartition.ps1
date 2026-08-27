param(
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.18f1\Editor\Unity.exe',
    [string]$ProjectPath = 'tools\plateau-environment-cost-analyzer',
    [string]$AnalysisConfig = 'data\analysis-configs\ichigaya-venue.json',
    [switch]$SkipComparison,
    [switch]$ForceRecalculate
)

$ErrorActionPreference = 'Stop'

function Invoke-UnityBatch {
    param([string[]]$Arguments)
    $existing = @(Get-Process Unity -ErrorAction SilentlyContinue | ForEach-Object Id)
    & $Unity @Arguments
    $children = @(Get-Process Unity -ErrorAction SilentlyContinue | Where-Object { $_.Id -notin $existing })
    foreach ($child in $children) { Wait-Process -Id $child.Id }
}

function Wait-ForCompletedUnit {
    param([string]$UnitId, [string]$OutputPath, [string]$StatePath)
    $deadline = (Get-Date).AddMinutes(45)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $OutputPath) {
            try {
                $result = Get-Content $OutputPath -Raw -Encoding utf8 | ConvertFrom-Json
                if ($result.status -eq 'completed' -and $result.meshPartition.unitId -eq $UnitId) { return }
            }
            catch { }
        }
        if (Test-Path -LiteralPath $StatePath) {
            try {
                $state = Get-Content $StatePath -Raw -Encoding utf8 | ConvertFrom-Json
                if ($state.status -eq 'failed' -or $state.status -eq 'cancelled') {
                    throw "Mesh unit $UnitId ended as $($state.status): $($state.message)"
                }
            }
            catch [System.Management.Automation.RuntimeException] { throw }
            catch { }
        }
        Start-Sleep -Seconds 5
    }
    throw "Mesh unit $UnitId did not complete within 45 minutes."
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$configPath = Join-Path $repositoryRoot $AnalysisConfig
$config = Get-Content $configPath -Raw | ConvertFrom-Json
$planPath = Join-Path $repositoryRoot $config.meshPartition.planOutputPath
if (-not (Test-Path -LiteralPath $planPath)) {
    Invoke-UnityBatch @('-batchmode', '-nographics', '-projectPath', (Join-Path $repositoryRoot $ProjectPath),
        '-executeMethod', 'MeshPartitionPlanner.Run', '-analysisConfig', $AnalysisConfig)
    if ($LASTEXITCODE -ne 0) { throw "Mesh plan generation failed with exit code $LASTEXITCODE." }
}

$plan = Get-Content $planPath -Raw | ConvertFrom-Json
$backupPath = Join-Path $repositoryRoot 'data\generated\ichigaya-venue-environment-cost.monolithic.json'
$finalPath = Join-Path $repositoryRoot $config.environmentCostOutputPath
if (-not $SkipComparison -and -not (Test-Path -LiteralPath $backupPath)) {
    Copy-Item -LiteralPath $finalPath -Destination $backupPath
}

foreach ($unit in $plan.units) {
    $unitOutput = Join-Path $repositoryRoot $unit.outputPath
    if (-not $ForceRecalculate -and (Test-Path -LiteralPath $unitOutput)) {
        $existing = Get-Content $unitOutput -Raw -Encoding utf8 | ConvertFrom-Json
        if ($existing.status -eq 'completed' -and $existing.meshPartition.unitId -eq $unit.id) {
            Write-Host "ENVIRONMENT_COST_MESH_UNIT_SKIPPED unit=$($unit.id)"
            continue
        }
    }
    Write-Host "ENVIRONMENT_COST_MESH_UNIT_START unit=$($unit.id)"
    $unityArguments = @('-batchmode', '-nographics', '-projectPath', (Join-Path $repositoryRoot $ProjectPath),
        '-executeMethod', 'EnvironmentCostAnalyzer.Run', '-analysisConfig', $AnalysisConfig, '-meshUnit', $unit.id)
    if ($ForceRecalculate) { $unityArguments += '-forceRecalculate' }
    Invoke-UnityBatch $unityArguments
    $statePath = Join-Path $repositoryRoot $unit.statePath
    Wait-ForCompletedUnit $unit.id $unitOutput $statePath
    $completed = Get-Content $unitOutput -Raw -Encoding utf8 | ConvertFrom-Json
    if ($completed.status -ne 'completed' -or $completed.meshPartition.unitId -ne $unit.id) {
        throw "Mesh unit $($unit.id) did not complete successfully."
    }
    Write-Host "ENVIRONMENT_COST_MESH_UNIT_COMPLETE unit=$($unit.id)"
}

& node (Join-Path $repositoryRoot 'tools\hourly-environment-cost\merge-mesh-partition-results.mjs') `
    --plan $config.meshPartition.planOutputPath --output $config.environmentCostOutputPath --root $repositoryRoot
if ($LASTEXITCODE -ne 0) { throw "Mesh result merge failed with exit code $LASTEXITCODE." }

if (-not $SkipComparison) {
    & node (Join-Path $repositoryRoot 'tools\hourly-environment-cost\verify-mesh-partition-result.mjs') `
        --monolithic $backupPath --partitioned $finalPath
    if ($LASTEXITCODE -ne 0) { throw "Mesh result comparison failed with exit code $LASTEXITCODE." }
}

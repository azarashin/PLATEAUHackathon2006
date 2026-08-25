param(
    [string[]] $AreaIds = @('kyoto', 'maizuru', 'fujisawa', 'saitama'),
    [int] $WaitForUnityProcessId = 0
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSCommandPath
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $projectRoot)
$unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.18f1\Editor\Unity.exe'
$sceneDirectory = Join-Path $projectRoot 'Assets\Scenes\EnvironmentCostInspection'

if ($WaitForUnityProcessId -gt 0) {
    $existingUnity = Get-Process -Id $WaitForUnityProcessId -ErrorAction SilentlyContinue
    if ($null -ne $existingUnity) {
        Write-Host "ENVIRONMENT_COST_INSPECTION_SCENE_WAITING_FOR_UNITY pid=$WaitForUnityProcessId"
        $existingUnity.WaitForExit()
    }
}

if (-not (Test-Path -LiteralPath $unity)) {
    throw "Unity editor was not found: $unity"
}

if (Get-Process -Name 'Unity Hub', 'Unity.Licensing.Client' -ErrorAction SilentlyContinue) {
    throw 'Close Unity Hub and Unity.Licensing.Client before generating inspection scenes. Their older licensing client can make Unity exit with a non-zero code after a successful batch operation.'
}

foreach ($areaId in $AreaIds) {
    if ($areaId -notmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$') {
        throw "Unsupported area id: $areaId"
    }

    $scenePath = Join-Path $sceneDirectory "$areaId.unity"
    if (Test-Path -LiteralPath $scenePath) {
        Write-Host "ENVIRONMENT_COST_INSPECTION_SCENE_EXISTS area=$areaId scene=$scenePath"
        continue
    }

    $config = Join-Path $repositoryRoot "data\analysis-configs\$areaId.json"
    $log = Join-Path $repositoryRoot "data\raw\$areaId-inspection-scene.log"
    if (-not (Test-Path -LiteralPath $config)) {
        throw "Analysis config was not found: $config"
    }

    Write-Host "ENVIRONMENT_COST_INSPECTION_SCENE_START area=$areaId"
    $unityProcess = Start-Process -FilePath $unity -ArgumentList @(
        '-batchmode',
        '-projectPath', $projectRoot,
        '-executeMethod', 'EnvironmentCostInspectionSceneBuilder.Run',
        '-analysisConfig', $config,
        '-logFile', $log
    ) -Wait -PassThru

    $ready = (Test-Path -LiteralPath $log -PathType Leaf) -and
        (Select-String -Path $log -SimpleMatch 'ENVIRONMENT_COST_INSPECTION_SCENE_READY' -Quiet)
    if ($unityProcess.ExitCode -ne 0 -or -not $ready -or -not (Test-Path -LiteralPath $scenePath)) {
        throw "Inspection scene generation failed for $areaId (exit=$($unityProcess.ExitCode)). See $log"
    }

    Write-Host "ENVIRONMENT_COST_INSPECTION_SCENE_CONFIRMED area=$areaId scene=$scenePath"
}

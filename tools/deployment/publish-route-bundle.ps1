[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ConfigFile = 'deploy/route-bundle-upload.env'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$resolvedConfigFile = if ([System.IO.Path]::IsPathRooted($ConfigFile)) {
    $ConfigFile
} else {
    Join-Path $repositoryRoot $ConfigFile
}

if (-not (Test-Path -LiteralPath $resolvedConfigFile -PathType Leaf)) {
    throw "Deployment configuration was not found: $resolvedConfigFile. Copy deploy/route-bundle-upload.env.example first."
}

$fileSettings = @{}
foreach ($line in Get-Content -LiteralPath $resolvedConfigFile -Encoding UTF8) {
    $trimmed = $line.Trim()
    if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#')) { continue }
    $separator = $trimmed.IndexOf('=')
    if ($separator -le 0) { throw "Invalid configuration line: $trimmed" }
    $name = $trimmed.Substring(0, $separator).Trim()
    $value = $trimmed.Substring($separator + 1).Trim()
    if ($name -notmatch '^ROUTE_DEPLOY_[A-Z0-9_]+$') { throw "Unsupported configuration key: $name" }
    $fileSettings[$name] = $value
}

function Get-DeploymentSetting {
    param([string]$Name, [string]$DefaultValue = '')
    $environmentValue = [Environment]::GetEnvironmentVariable($Name)
    if (-not [string]::IsNullOrWhiteSpace($environmentValue)) { return $environmentValue.Trim() }
    if ($fileSettings.ContainsKey($Name) -and -not [string]::IsNullOrWhiteSpace($fileSettings[$Name])) { return $fileSettings[$Name] }
    return $DefaultValue
}

$serverHost = Get-DeploymentSetting 'ROUTE_DEPLOY_HOST'
$serverUser = Get-DeploymentSetting 'ROUTE_DEPLOY_USER' 'azarashin'
$sshPortText = Get-DeploymentSetting 'ROUTE_DEPLOY_SSH_PORT' '22'
$remoteRoot = (Get-DeploymentSetting 'ROUTE_DEPLOY_ROOT').TrimEnd('/')
$bundleName = Get-DeploymentSetting 'ROUTE_DEPLOY_BUNDLE_NAME'
$localBundleSetting = Get-DeploymentSetting 'ROUTE_DEPLOY_LOCAL_BUNDLE'

if ($serverHost -notmatch '^[A-Za-z0-9.-]+$') { throw 'ROUTE_DEPLOY_HOST must be an IP address or hostname without a protocol or path.' }
if ($serverUser -notmatch '^[A-Za-z0-9._-]+$') { throw 'ROUTE_DEPLOY_USER contains unsupported characters.' }
$sshPort = 0
if (-not [int]::TryParse($sshPortText, [ref]$sshPort) -or $sshPort -lt 1 -or $sshPort -gt 65535) { throw 'ROUTE_DEPLOY_SSH_PORT must be between 1 and 65535.' }
if ($remoteRoot -notmatch '^/[A-Za-z0-9._/-]+$') { throw 'ROUTE_DEPLOY_ROOT must be an absolute Linux path without spaces or shell characters.' }
if ([string]::IsNullOrWhiteSpace($bundleName) -or $bundleName -notmatch '^[A-Za-z0-9._-]+$') { throw 'ROUTE_DEPLOY_BUNDLE_NAME is required and may contain only letters, digits, dot, underscore, and hyphen.' }
if ([string]::IsNullOrWhiteSpace($localBundleSetting)) { throw 'ROUTE_DEPLOY_LOCAL_BUNDLE is required.' }

$localBundle = if ([System.IO.Path]::IsPathRooted($localBundleSetting)) {
    $localBundleSetting
} else {
    Join-Path $repositoryRoot $localBundleSetting
}
$localBundle = [System.IO.Path]::GetFullPath($localBundle)
if (-not (Test-Path -LiteralPath $localBundle -PathType Container)) { throw "Local route bundle was not found: $localBundle" }

$manifestPath = Join-Path $localBundle 'manifest.json'
$topologyPath = Join-Path $localBundle 'topology.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "manifest.json was not found: $manifestPath" }
if (-not (Test-Path -LiteralPath $topologyPath -PathType Leaf)) { throw "topology.json was not found: $topologyPath" }

$nodeCommand = Get-Command node -ErrorAction Stop
$sshCommand = Get-Command ssh -ErrorAction Stop
$scpCommand = Get-Command scp -ErrorAction Stop
$validator = Join-Path $repositoryRoot 'viewer/scripts/validate-environment-cost-server-bundle.mjs'
& $nodeCommand.Source '--max-old-space-size=4096' $validator $manifestPath
if ($LASTEXITCODE -ne 0) { throw 'Local route bundle validation failed.' }

$remoteParent = "$remoteRoot/data/generated"
$stamp = Get-Date -Format 'yyyyMMddHHmmss'
$remoteTarget = "$remoteParent/$bundleName"
$remoteStaging = "$remoteParent/.$bundleName.incoming-$stamp"
$remoteBackup = "$remoteParent/.$bundleName.backup-$stamp"
$remoteLogin = "$serverUser@$serverHost"
$localFiles = @(Get-ChildItem -LiteralPath $localBundle -File | Select-Object -ExpandProperty FullName)
if ($localFiles.Count -lt 3) { throw 'The route bundle must contain manifest, topology, and at least one cost slice.' }

if (-not $PSCmdlet.ShouldProcess("$remoteLogin`:$remoteTarget", "Upload $($localFiles.Count) validated route bundle files")) { return }

& $sshCommand.Source '-p' "$sshPort" $remoteLogin "mkdir -p '$remoteParent' && test ! -e '$remoteStaging' && mkdir '$remoteStaging'"
if ($LASTEXITCODE -ne 0) { throw 'Could not create the remote staging directory.' }

$scpArguments = @('-P', "$sshPort") + $localFiles + @("$remoteLogin`:$remoteStaging/")
& $scpCommand.Source @scpArguments
if ($LASTEXITCODE -ne 0) { throw "Upload failed. The incomplete staging directory remains at $remoteStaging." }

& $sshCommand.Source '-p' "$sshPort" $remoteLogin "cd '$remoteRoot' && node --max-old-space-size=4096 viewer/scripts/validate-environment-cost-server-bundle.mjs '$remoteStaging/manifest.json'"
if ($LASTEXITCODE -ne 0) { throw "Remote validation failed. The current bundle was not changed; inspect $remoteStaging." }

$promoteCommand = "if test -e '$remoteTarget'; then mv '$remoteTarget' '$remoteBackup'; fi; if mv '$remoteStaging' '$remoteTarget'; then exit 0; fi; if test -e '$remoteBackup'; then mv '$remoteBackup' '$remoteTarget'; fi; exit 1"
& $sshCommand.Source '-p' "$sshPort" $remoteLogin $promoteCommand
if ($LASTEXITCODE -ne 0) { throw 'Remote bundle promotion failed. The previous bundle was restored when available.' }

Write-Host "ROUTE_BUNDLE_UPLOAD_COMPLETE target=$remoteLogin`:$remoteTarget files=$($localFiles.Count)"
Write-Host "Next: sudo systemctl restart environment-cost-route-server.service"

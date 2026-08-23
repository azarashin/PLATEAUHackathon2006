# ノードID付きOSMで市ヶ谷のUnity解析を再実行する

## 目的

初回の市ヶ谷パイロット解析は、OSMノードIDを含まない`out tags geom`の応答を使用しました。Issue #5で生成した道路グラフは、立体交差等を正しく区別するため、`out body geom`で取得したOSMノードIDを接続関係の正本としています。

この手順では、道路グラフと同じOSMスナップショットを使ってUnity環境コスト解析を再実行し、環境コスト辺の`fromNodeId`・`toNodeId`と道路グラフを結合できる出力を生成します。

## 生成・更新するファイル

| 種別 | パス | Git管理 |
|---|---|---|
| OSMスナップショット | `data/raw/ichigaya-osm-highways-with-nodes.json` | しない |
| スナップショット台帳 | `data/osm-snapshot-manifests/ichigaya-venue.json` | する |
| 地域設定 | `data/analysis-configs/ichigaya-venue.json` | する |
| メッシュ対応表 | `data/raw/ichigaya-venue-mesh-coverage.json` | しない |
| Unity環境コスト | `data/generated/ichigaya-venue-environment-cost.json` | しない |
| Unity解析サマリー | `data/raw/ichigaya-venue-analysis-summary.json` | しない |
| Unity解析状態 | `data/raw/ichigaya-venue-analysis-state.json` | しない |
| 時刻別キャッシュ | `data/raw/environment-cost-cache/ichigaya-venue/` | しない |
| Unityログ | `data/raw/ichigaya-venue-environment-cost.log` | しない |
| 道路グラフ | `data/generated/ichigaya-pedestrian-road-network.json` | しない |

旧`ichigaya-pilot-*`出力は初回実行の証跡として残し、上書きしません。

## 前提条件

- リポジトリルートでPowerShellを実行する。
- Unity 6000.3.18f1と、`tools/plateau-environment-cost-analyzer`の依存パッケージが利用できる。
- `data/raw/plateau/<自治体ID>-2025/`に対象7区のCityGMLが展開済みである。
- Unity Editorで分析プロジェクトを開いていない。バッチ実行中も同じプロジェクトを別プロセスで起動しない。
- 現行の時間別出力の実績では初回ピークメモリが約12 GiB、処理時間が約10分、結果とキャッシュの合計が約612 MiBだった。再実行時は余裕のあるメモリとディスクを確保する。

## 1. OSMスナップショットを確認する

現在の道路グラフで使用したファイルは次の値です。

```text
OSM基準時刻: 2026-08-23T03:03:03Z
サイズ:       31,491,836 bytes
SHA-256:      424fac87b6ed446f75613ed7298cd3f12574e3298f5446b94f2a37ae2bb95fbd
```

ローカルファイルを台帳と照合します。

```powershell
$osmPath = 'data/raw/ichigaya-osm-highways-with-nodes.json'
$manifestPath = 'data/osm-snapshot-manifests/ichigaya-venue.json'
$manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$osmFile = Get-Item $osmPath
$osmHash = (Get-FileHash $osmPath -Algorithm SHA256).Hash.ToLowerInvariant()

if ($osmFile.Length -ne $manifest.sizeBytes) { throw 'OSM snapshot size does not match the manifest.' }
if ($osmHash -ne $manifest.sha256) { throw 'OSM snapshot SHA-256 does not match the manifest.' }
```

wayにノードIDと形状があり、配列長が一致することも確認します。

```powershell
node -e "const fs=require('fs');const p='data/raw/ichigaya-osm-highways-with-nodes.json';const j=JSON.parse(fs.readFileSync(p,'utf8'));const w=j.elements.filter(x=>x.type==='way');const bad=w.filter(x=>!Array.isArray(x.nodes)||!Array.isArray(x.geometry)||x.nodes.length!==x.geometry.length||x.nodes.length<2);if(bad.length)throw new Error('invalid ways: '+bad.length);console.log('OSM_NODE_IDS_OK ways='+w.length+' timestamp='+j.osm3s.timestamp_osm_base)"
```

期待値は`OSM_NODE_IDS_OK ways=48570 timestamp=2026-08-23T03:03:03Z`です。

### ファイルがない、またはハッシュが一致しない場合

Overpass APIを再照会すると、その時点の最新OSMが返り、上記スナップショットと同一にはなりません。元ファイルを復元できない場合は[取得クエリ](../data/ichigaya-highways.overpassql)で新しいスナップショットを取得し、次をすべて同じ入力でやり直します。

1. スナップショット台帳の基準時刻・取得日時・サイズ・SHA-256を更新する。
2. Issue #5の道路グラフを再生成して品質値とハッシュを更新する。
3. 本資料のUnity解析を実行する。
4. #9の環境コスト結合結果を再生成する。

取得例は次のとおりです。

```powershell
curl.exe --fail --show-error `
  --user-agent 'PLATEAUHackathon2006-road-network/1.0' `
  --data-urlencode 'data@data/ichigaya-highways.overpassql' `
  --output 'data/raw/ichigaya-osm-highways-with-nodes.json' `
  'https://overpass-api.de/api/interpreter'
```

## 2. 地域設定と入力データを確認する

`data/analysis-configs/ichigaya-venue.json`の`osmInputPath`が次になっていることを確認します。

```json
"osmInputPath": "data/raw/ichigaya-osm-highways-with-nodes.json"
```

CityGMLとメッシュ対応表を確認します。

```powershell
$config = Get-Content 'data/analysis-configs/ichigaya-venue.json' -Raw -Encoding UTF8 | ConvertFrom-Json

foreach ($datasetId in $config.candidateDatasetIds) {
  $datasetPath = $config.datasetRoots.$datasetId
  if (-not (Test-Path $datasetPath)) { throw "CityGML directory is missing: $datasetPath" }
}

if (-not (Test-Path $config.osmInputPath)) { throw "OSM input is missing: $($config.osmInputPath)" }
```

対象範囲・PLATEAU年度・候補自治体を変更していなければ、既存の`data/raw/ichigaya-venue-mesh-coverage.json`を再利用できます。存在しない場合、または対象条件を変更した場合だけ、次節のメッシュ抽出を実行します。

## 3. 必要な場合だけメッシュ対応表を再生成する

この処理はPLATEAUのデータセット情報へアクセスします。

```powershell
$repoRoot = (Resolve-Path '.').Path
$unityExe = 'C:\Program Files\Unity\Hub\Editor\6000.3.18f1\Editor\Unity.exe'
$analyzerProject = Join-Path $repoRoot 'tools\plateau-environment-cost-analyzer'
$analysisConfig = 'data/analysis-configs/ichigaya-venue.json'
$coverageLog = Join-Path $repoRoot 'data\raw\ichigaya-venue-mesh-coverage.log'

& $unityExe `
  -batchmode `
  -projectPath $analyzerProject `
  -executeMethod MeshCoverageAnalyzer.Run `
  -analysisConfig $analysisConfig `
  -logFile $coverageLog

$coverageExitCode = $LASTEXITCODE
if ($coverageExitCode -ne 0) { throw "Mesh coverage failed with exit code $coverageExitCode. See $coverageLog" }
if (-not (Select-String -Path $coverageLog -Pattern 'ENVIRONMENT_COST_COVERAGE_COMPLETE' -Quiet)) { throw 'Coverage completion marker was not found.' }
```

## 4. Unity環境コスト解析を再実行する

```powershell
$repoRoot = (Resolve-Path '.').Path
$unityExe = 'C:\Program Files\Unity\Hub\Editor\6000.3.18f1\Editor\Unity.exe'
$analyzerProject = Join-Path $repoRoot 'tools\plateau-environment-cost-analyzer'
$analysisConfig = 'data/analysis-configs/ichigaya-venue.json'
$analysisLog = Join-Path $repoRoot 'data\raw\ichigaya-venue-environment-cost.log'

& $unityExe `
  -batchmode `
  -projectPath $analyzerProject `
  -executeMethod EnvironmentCostAnalyzer.Run `
  -analysisConfig $analysisConfig `
  -forceRecalculate `
  -logFile $analysisLog

$analysisExitCode = $LASTEXITCODE
if ($analysisExitCode -ne 0) { throw "Environment-cost analysis failed with exit code $analysisExitCode. See $analysisLog" }
if (-not (Select-String -Path $analysisLog -Pattern 'ENVIRONMENT_COST_ANALYSIS_COMPLETE' -Quiet)) { throw 'Analysis completion marker was not found.' }
```

`-forceRecalculate`は既存キャッシュを読まず、全10時刻を再計算する指定です。入力・設定が同じ結果を再構成するだけでよい場合はこの指定を外し、時刻別キャッシュを再利用します。

出力先は地域設定から決まり、`data/generated/ichigaya-venue-environment-cost.json`、`data/raw/ichigaya-venue-analysis-summary.json`、`data/raw/ichigaya-venue-analysis-state.json`になります。

## 5. Unity出力を検証する

まず出力・サマリー・ログが存在することを確認します。

```powershell
$environmentPath = 'data/generated/ichigaya-venue-environment-cost.json'
$summaryPath = 'data/raw/ichigaya-venue-analysis-summary.json'
$statePath = 'data/raw/ichigaya-venue-analysis-state.json'
$analysisLog = 'data/raw/ichigaya-venue-environment-cost.log'

foreach ($path in @($environmentPath, $summaryPath, $statePath, $analysisLog)) {
  if (-not (Test-Path $path)) { throw "Expected output is missing: $path" }
}
```

次に、全環境コスト辺にOSMノードIDがあり、道路グラフの入力辺と対応することを確認します。

```powershell
node --max-old-space-size=4096 tools/hourly-environment-cost/validate-hourly-output.mjs `
  data/generated/ichigaya-venue-environment-cost.json

node --max-old-space-size=4096 -e "const fs=require('fs');const env=JSON.parse(fs.readFileSync('data/generated/ichigaya-venue-environment-cost.json','utf8'));const graph=JSON.parse(fs.readFileSync('data/generated/ichigaya-pedestrian-road-network.json','utf8'));const source=new Map();for(const e of graph.edges)for(const id of e.sourceEdgeIds)source.set(id,e);const missingIds=env.edges.filter(e=>!Number.isInteger(e.fromNodeId)||!Number.isInteger(e.toNodeId));const missingGraph=env.edges.filter(e=>!source.has(e.id));const badTopology=env.edges.filter(e=>{const g=source.get(e.id);return g&&!new Set([g.fromNodeId,g.toNodeId]).has('osm-node-'+e.fromNodeId)||g&&!new Set([g.fromNodeId,g.toNodeId]).has('osm-node-'+e.toNodeId)});const badHours=env.edges.filter(e=>!Array.isArray(e.hourly)||e.hourly.length!==10);if(missingIds.length||missingGraph.length||badTopology.length||badHours.length)throw new Error(JSON.stringify({missingNodeIds:missingIds.length,missingGraphEdges:missingGraph.length,topologyMismatch:badTopology.length,badHourlySlices:badHours.length}));console.log('UNITY_OSM_GRAPH_ALIGNMENT_OK edges='+env.edges.length)"
```

この確認が失敗した場合は#9へ進まず、次を確認します。

- Unity解析と道路グラフが同じOSMファイルを参照しているか。
- `osmInputPath`が旧`ichigaya-osm-highways.json`へ戻っていないか。
- OSM取得後に道路グラフだけ、またはUnity解析だけを再生成していないか。
- UnityログにCityGML読込、道路面レイキャスト、メモリ不足等の例外がないか。

最後に、再実行したファイルの監査値を取得して実行記録へ追記します。

```powershell
Get-FileHash `
  'data/raw/ichigaya-osm-highways-with-nodes.json', `
  'data/generated/ichigaya-venue-environment-cost.json', `
  'data/generated/ichigaya-pedestrian-road-network.json' `
  -Algorithm SHA256

Get-Content 'data/raw/ichigaya-venue-analysis-summary.json' -Raw -Encoding UTF8 | ConvertFrom-Json
```

記録する項目は、実行日時、Unity・PLATEAU SDK版、OSM基準時刻とSHA-256、解析辺数、サンプル数、有効・欠測数、処理時間、ピークメモリ、各出力のサイズとSHA-256です。

## 6. #9へ引き渡す

検証済みの次の2ファイルを#9の入力とします。

- `data/generated/ichigaya-pedestrian-road-network.json`
- `data/generated/ichigaya-venue-environment-cost.json`

道路グラフ辺の`sourceEdgeIds`と、環境コスト辺の`id`を対応させます。物理重複を統合した道路辺は複数の`sourceEdgeIds`を持つため、#9で時刻別コストの統合規則を明記します。欠測値`shadeRatio=null`は0として扱わず、正式データ契約の欠測状態として保持します。

実装済みの結合コマンド、物理重複辺の加重集約、円境界112辺の扱い、正式契約の検証結果は[環境コスト道路ネットワークの生成](environment-cost-road-network-generation.md)を参照してください。

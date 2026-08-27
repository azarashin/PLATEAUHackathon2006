# PLATEAU Environment Cost Analyzer

## Reproducible inspection Scene

The analysis batch run does not save its temporary CityGML objects as a Unity Scene. To inspect an existing result without rerunning the analysis, open this project in Unity and choose **PLATEAU > Environment Cost > Create Inspection Scene**. Select the same `data/analysis-configs/<areaId>.json` that was used for analysis.

## 4km範囲をメッシュ単位で実行する

`meshPartition`を持つ設定では、まず一度だけメッシュ実行計画を生成する。計画のIDは常に`mesh-<8桁メッシュコード>`で、同じ計画を使えば任意の単位を再実行できる。各単位は道路サンプルを緯度経度の半開矩形で一意に所有する。建物は周辺の`shadowBufferMeters`（市ヶ谷は750m）だけを読み込む一方、地表判定用の道路は一括実行と同じ全対象範囲から読み込む。

道路 CityGML のポリゴンは対応する3次メッシュ境界をまたぐことがある。そのため道路まで局所バッファへ制限すると、境界付近の下向きレイキャストが地表を見失い、`validSampleCount` と時刻別結果が一括実行と不一致になる。建物だけを局所化し、道路を全対象範囲から読み込むことで、メモリ負荷の大きい建物Colliderを分割しつつ、地表有効／欠測判定は一括実行と同一に保つ。

Unity Hub と対象プロジェクトを開いている Unity Editor を完全終了してから、プロジェクトのルートで実行する。

### Unity ライセンスと起動コンテキスト

Unity バッチは、**Unity Hub／Editor にサインインしている同じ対話ログオンユーザーの通常の PowerShell** から起動する。Codex・CI・管理者昇格など、別のサンドボックスまたは別セッションから起動すると、ユーザーごとの `Unity.Licensing.Client` 名前付きチャネルへ接続できないことがある。この場合はライセンスが有効でも解析を開始してはならない。

実行前に **Unity Hub と対象プロジェクトを開いている Unity Editor を必ず完全終了する**。同じプロジェクトを開いたままでは、`Multiple Unity instances cannot open the same project` でバッチが停止する。Hubで再認証した場合も、Editorを一度通常起動してライセンスを読み込ませた後、HubとEditorを終了してからバッチを開始する。

次のメッセージを見た場合の扱いを固定する。

| 症状 | 原因・対処 |
| --- | --- |
| `Connection to channel LicenseClient-... refused`、`Timed-out ... Licensing` | 起動元がライセンスサービスへ到達できていない。Hub/Editorを完全終了したうえで、サインインした同じ通常ユーザーのPowerShellから再実行する。サンドボックス・別アカウント・別セッションからの実行を避ける。 |
| `No valid Unity Editor license found` | 上記の起動コンテキストを確認したうえで、Hubでサインインし、Editorを一度通常起動してライセンスを読み込ませる。Personal ライセンス自体は利用可能であり、この表示だけで有償ライセンスへ変更しない。 |
| `Multiple Unity instances cannot open the same project` | 対象プロジェクトを開いている Unity Editor を終了してから再実行する。 |
| `CreateDirectory ... AppData/Local/Unity/Caches ... already exists` | 本件では既存の正常な `Caches` ディレクトリに対する Unity 側の警告であり、ライセンス障害の原因ではなかった。ディレクトリを削除・置換しない。 |

実行ログに `HOURLY_ENVIRONMENT_COST_SELF_TEST_PASSED` が出ることを、長時間解析の開始条件にする。`-batchmode` 実行の終了コードだけでは判定せず、必ずこのログを確認する。

```powershell
$unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.18f1\Editor\Unity.exe'
$project = 'tools\plateau-environment-cost-analyzer'
$config = 'data\analysis-configs\ichigaya-venue.json'

& $unity -batchmode -projectPath $project `
  -executeMethod MeshPartitionPlanner.Run -analysisConfig $config
```

計画ファイルの`units[].id`から対象を選び、その単位だけを実行する。完了済みの時間スライスは単位別キャッシュから再利用するため、同じコマンドで中断後も再開できる。停止したい単位だけは、対応する`data/raw/ichigaya-venue-mesh-unit-state/<unit>.cancel`を作成する。

```powershell
$unit = 'mesh-53394611' # data/raw/ichigaya-venue-mesh-partition-plan.json の units[].id
& $unity -batchmode -projectPath $project `
  -executeMethod EnvironmentCostAnalyzer.Run -analysisConfig $config -meshUnit $unit
```

全単位が`completed`になったら、Nodeで単位結果を通常の単一`areaId`成果物へ結合する。結合時には、同じOSMエッジの`sampleCount`・`validSampleCount`・`noGroundSampleCount`・遮蔽サンプル数を加算し、`shadeRatio`と`solarExposureSeconds`を再計算する。従来の道路ネットワーク・サーバ生成は、この結合済みJSONだけを入力にする。

```powershell
node tools/hourly-environment-cost/merge-mesh-partition-results.mjs `
  --plan data/raw/ichigaya-venue-mesh-partition-plan.json `
  --output data/generated/ichigaya-venue-environment-cost.json
```

分割の判定は、一括実行がメモリ不足・非ゼロ終了・手動キャンセルになった場合、またはピークワーキングセットが搭載RAMの70%を超えるか30分を超える場合に行う。結合後は、退避した一括結果と辺・サンプル数・時刻別値を比較する。

```powershell
node tools/hourly-environment-cost/verify-mesh-partition-result.mjs `
  --monolithic data/generated/ichigaya-venue-environment-cost.monolithic.json `
  --partitioned data/generated/ichigaya-venue-environment-cost.json
```

The command validates the config and its coverage report, imports `bldg`, `tran`, and `dem` at LOD1, adds MeshColliders, assigns `Building` (layer 8), `Road` (layer 9), and `Terrain` (layer 10), and saves the local generated Scene to `Assets/Scenes/EnvironmentCostInspection/<areaId>.unity`. The `areaId` must use lowercase ASCII letters, digits, and single hyphens. Each area has its own Scene, so regenerating one does not replace another; only an existing Scene for the same area prompts for replacement. These Scenes and their meta files are ignored by Git because CityGML input is large and locally licensed. It first offers Unity's normal save confirmation for any modified current Scene, then switches to the generated inspection Scene; an unsaved empty Scene does not need to be created manually. The command is cancellable between datasets; a cancelled or failed partial Scene is closed without saving.

To create a city Scene non-interactively, close Unity Editor and Unity Hub, then run the following. The batch command waits for CityGML import through Unity's Editor event loop and exits `0` only after `ENVIRONMENT_COST_INSPECTION_SCENE_READY` is logged. It never shows save or replacement dialogs; if `Assets/Scenes/EnvironmentCostInspection/<areaId>.unity` already exists, it exits `1` without changing that Scene.

```powershell
$unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.18f1\Editor\Unity.exe'
$project = 'H:\MyDevelopment\PLATEAUHackathon2006\tools\plateau-environment-cost-analyzer'
$config = 'data/analysis-configs/kyoto.json'
$log = 'H:\MyDevelopment\PLATEAUHackathon2006\data\raw\kyoto-inspection-scene.log'

$process = Start-Process -FilePath $unity -ArgumentList @(
  '-batchmode', '-projectPath', $project,
  '-executeMethod', 'EnvironmentCostInspectionSceneBuilder.Run',
  '-analysisConfig', $config, '-logFile', $log
) -Wait -PassThru
if ($process.ExitCode -ne 0) { exit $process.ExitCode }
Select-String -Path $log -Pattern 'ENVIRONMENT_COST_INSPECTION_SCENE_READY'
```

Do not add `-nographics`: PLATEAU SDK may need the graphics device while converting relief textures. To generate several areas sequentially without overwriting existing Scenes, run `./generate-city-inspection-scenes.ps1` after closing Unity Editor and Unity Hub.

After the `ENVIRONMENT_COST_INSPECTION_SCENE_READY` log confirms both collider counts are greater than zero, open **PLATEAU > Environment Cost > Hourly Heatmap**, load the completed environment-cost JSON, select 12:00, and select one road edge. In the Scene view, green markers are shaded samples, orange markers are sunlit samples, red markers could not find a Road collider, and the purple arrow is the calculated sun direction. Nonzero collider counts demonstrate that the inspection data is present; they do not by themselves prove complete CityGML coverage.

PLATEAU CityGMLとOpenStreetMap道路を入力に、指定地域の道路エッジごとの日陰率・日射曝露時間を出力する汎用Unityバッチツールである。地域固有の値は`data/analysis-configs/<areaId>.json`に置き、ツールのC#コードやUnityプロジェクト名には含めない。

## 前提条件

- Unity 6000.3.10f1以降（市ヶ谷実行では6000.3.18f1を使用）
- Git経由で取得するPLATEAU SDK for Unity 4.3.0
- 設定ファイルに記載された展開済みCityGMLとOSM JSON

Unityがパッケージを解決した後、次の順にバッチ実行する。

```powershell
$unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.18f1\Editor\Unity.exe'
$project = 'H:\MyDevelopment\PLATEAUHackathon2006\tools\plateau-environment-cost-analyzer'
$config = 'data/analysis-configs/ichigaya-venue.json'

& $unity -batchmode -projectPath $project -executeMethod DatasetCatalogProbe.Run -analysisConfig $config -logFile data/raw/dataset-catalog.log
& $unity -batchmode -projectPath $project -executeMethod MeshCoverageAnalyzer.Run -analysisConfig $config -logFile data/raw/mesh-coverage.log
& $unity -batchmode -projectPath $project -executeMethod EnvironmentCostAnalyzer.Run -analysisConfig $config -logFile data/raw/environment-cost.log
```

`DatasetCatalogProbe`は候補データセットの確認、`MeshCoverageAnalyzer`は対象円と重なるメッシュの生成、`EnvironmentCostAnalyzer`はCityGMLのインポートと道路ごとの日陰解析を担当する。

解析結果を無視して全時刻を再計算する場合は、最後のコマンドへ`-forceRecalculate`を追加する。通常実行では設定と入力が同じ時間別キャッシュを再利用する。

## 入出力と実行状態

設定ファイルでは次を明示する。

- `areaId`、中心座標、半径、平面直角座標系、日時・サンプリング条件
- 候補PLATEAUデータセットIDとローカル展開先
- OSM入力、メッシュ対応表、環境コスト、実行サマリーのパス
- 時刻別キャッシュ、実行状態、中断要求ファイルのパス

CityGML、OSM応答、メッシュ対応表、環境コストJSON、Unityの`Library`は大容量または生成物のためGit管理外である。設定・C#スクリプト・手順はGit管理する。

`stateOutputPath`には`running`、`completed`、`failed`、`cancelled`のいずれかと進捗を原子的に書き出す。正常終了時だけ環境コストJSONの`status`が`completed`になる。実行中に空の`cancellationRequestPath`を作ると安全な区切りで中断でき、Unity Editorから実行した場合は進捗ダイアログの「Cancel」も利用できる。

```powershell
New-Item -ItemType File data/raw/ichigaya-venue-analysis.cancel
```

バッチ終了コードは成功`0`、失敗`1`、中断`2`である。出力は`.partial`へ書いて検証後に置換するため、不完全なJSONを完了結果として公開しない。

## 検証と可視化

小規模な規則テストはUnityバッチで実行できる。

> **重要: Unity Hub と対象プロジェクトを開く Unity Editor を完全に終了してから実行する。**
> ライセンス障害が出た場合は、上記「Unity ライセンスと起動コンテキスト」の起動ユーザー・
> セッション・`LicenseClient` 接続条件を確認する。自己テストが成功してもプロセス終了コードが
> `1`になる環境があるため、完了マーカーも確認する。

```powershell
$log = 'H:\MyDevelopment\PLATEAUHackathon2006\data\raw\hourly-cost-self-test.log'
$process = Start-Process -FilePath $unity -ArgumentList @(
  '-batchmode', '-nographics', '-projectPath', $project,
  '-executeMethod', 'HourlyEnvironmentCostSelfTests.Run', '-logFile', $log
) -Wait -PassThru
if ($process.ExitCode -ne 0) { exit $process.ExitCode }
Select-String -Path $log -Pattern 'HOURLY_ENVIRONMENT_COST_SELF_TEST_PASSED'
```

大規模な結果JSONは次の検証スクリプトで全エッジ・全時刻、欠測理由、計算式を確認する。市ヶ谷の約300 MiBのJSONではNode.jsのヒープ上限を明示する。

```powershell
node tools/hourly-environment-cost/test-validate-hourly-output.mjs
node --max-old-space-size=4096 tools/hourly-environment-cost/validate-hourly-output.mjs data/generated/ichigaya-venue-environment-cost.json
```

Unity Editorで`PLATEAU > Environment Cost > Hourly Heatmap`を開き、完了済み結果JSONを指定する。時刻を切り替えると、日陰率を橙（0）から緑（1）、欠測を灰色でSceneビューへ描画する。詳細な仕様と実測値は[時間別環境コストの解析・検証・可視化](../../docs/hourly-environment-cost-analysis.md)を参照する。

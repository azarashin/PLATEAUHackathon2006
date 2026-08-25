# PLATEAU Environment Cost Analyzer

## Reproducible inspection Scene

The analysis batch run does not save its temporary CityGML objects as a Unity Scene. To inspect an existing result without rerunning the analysis, open this project in Unity and choose **PLATEAU > Environment Cost > Create Inspection Scene**. Select the same `data/analysis-configs/<areaId>.json` that was used for analysis.

The command validates the config and its coverage report, imports only `bldg` and `tran` at LOD1, adds MeshColliders, assigns `Building` (layer 8) and `Road` (layer 9), and saves the local generated Scene to `Assets/Scenes/EnvironmentCostInspection.unity`. That Scene and its meta file are ignored by Git because CityGML input is large and locally licensed. It first offers Unity's normal save confirmation for any modified current Scene, then switches to the generated inspection Scene; an unsaved empty Scene does not need to be created manually. The command is cancellable between datasets; a cancelled or failed partial Scene is closed without saving.

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

> **重要: Unity Hubを完全に終了してから実行する。** Hub が常駐させる
> `Unity.Licensing.Client` と Unity 6000.3.18f1 同梱のライセンスクライアントでは
> プロトコル差により、自己テストが成功してもプロセス終了コードが `1` になることがある。
> Editor と Hub を閉じた後、タスクマネージャーで `Unity Hub` と
> `Unity.Licensing.Client` が残っていないことを確認する。テスト完了後は Hub を再起動してよい。

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

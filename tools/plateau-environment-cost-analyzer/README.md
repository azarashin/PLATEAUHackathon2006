# PLATEAU Environment Cost Analyzer

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

## 入出力

設定ファイルでは次を明示する。

- `areaId`、中心座標、半径、平面直角座標系、日時・サンプリング条件
- 候補PLATEAUデータセットIDとローカル展開先
- OSM入力、メッシュ対応表、環境コスト、実行サマリーのパス

CityGML、OSM応答、メッシュ対応表、環境コストJSON、Unityの`Library`は大容量または生成物のためGit管理外である。設定・C#スクリプト・手順はGit管理する。

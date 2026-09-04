# 環境コスト経路マップビューア

PLATEAU CityGML と OSM 歩行ネットワークから道路・歩道の日陰率を時刻別に計算し、ブラウザで経路と道路別ヒートマップを閲覧するシステムです。

- **Unity解析ツール**: CityGML、地表、建物、植生・施策遮蔽物から日陰を計算します。
- **経路サーバー**: 解析済み server bundle を検証・読込し、経路・KPI・道路別値をAPIで返します。
- **Viewer**: 地域、時刻、起終点を選び、経路と日陰率を地図へ表示します。

Viewer は CityGML や Unity Scene を直接読み込みません。ブラウザへ配信するのは経路サーバーのAPI応答だけです。

> 日陰率は指定時刻の道路サンプルが直射日光を受けるかを示す解析指標です。体感温度や安全性を保証するものではありません。

## 現在利用できるデータ

市ヶ谷周辺、京都市、舞鶴市、藤沢市、さいたま市の5地域について、歩道対応v2ネットワークと`2025-08-01`の0時〜23時の日陰解析結果を利用できます。通常の閲覧は各地域の`baseline`（現状）です。

旧市ヶ谷の`ichigaya-demo-shade` A/Bデータは旧ネットワーク用です。v2互換の施策後バンドルを生成するまでは、5地域v2閲覧に混在させません。

## ディレクトリ構成

```text
tools/plateau-environment-cost-analyzer/  Unity解析・Inspection Scene生成
tools/road-network/                       OSM取得・歩道対応グラフ生成
tools/environment-cost-network/           server bundle生成・検証
server/                                   Node.js経路API
viewer/                                   ブラウザ版Viewer
data/raw/                                 CityGML、OSM、解析中間成果物（Git管理外）
data/generated/                           グラフ・server bundle（Git管理外）
docs/                                     手順、仕様、設計判断
```

## 前提条件

- Windows / PowerShell
- Node.js **22.18.0**、npm **11.5.2**
- Unity Hub と Unity **6000.3.18f1**（解析ツール用）
- PLATEAU SDK for Unity と、CityGMLを取得できるネットワーク接続

リポジトリ直下で依存関係を導入します。

```powershell
npm --prefix server ci
npm --prefix viewer ci
```

Unityプロジェクトは `tools/plateau-environment-cost-analyzer/` を Unity Hub から開きます。Hub と Editor を閉じる必要があるバッチ処理は、各手順書の指示に従ってください。

## 最短手順: 完成済み5地域bundleを閲覧する

すでに `data/generated/*-environment-cost-server-bundle-v2/` を配置済みなら、CityGML・Unity解析を再実行せずに以下だけで閲覧できます。実データはGit管理外のため、サーバー環境には各地域の完成済みbundleディレクトリを読み取り可能な場所へ配置してください。

### 1. 経路サーバーを起動する（12時のみ）

初回確認では、5地域の12時だけを読み込みます。入力は約196.8 MiBで、Node.jsには2 GiBヒープを指定します。

```powershell
$env:HOST = '127.0.0.1'
$env:PORT = '3102'
$env:ROUTE_CORS_ORIGIN = 'http://localhost:5173'
$env:NODE_OPTIONS = '--max-old-space-size=2048'
$env:ROUTE_TIMESTAMPS = '2025-08-01T12:00:00+09:00'
$env:ROUTE_BUNDLE_MANIFESTS = @(
  (Resolve-Path 'data/generated/ichigaya-venue-environment-cost-server-bundle-v2/manifest.json').Path,
  (Resolve-Path 'data/generated/kyoto-environment-cost-server-bundle-v2/manifest.json').Path,
  (Resolve-Path 'data/generated/maizuru-environment-cost-server-bundle-v2/manifest.json').Path,
  (Resolve-Path 'data/generated/fujisawa-environment-cost-server-bundle-v2/manifest.json').Path,
  (Resolve-Path 'data/generated/saitama-environment-cost-server-bundle-v2/manifest.json').Path
) -join ','
Remove-Item Env:ROUTE_SCENARIO_BUNDLES -ErrorAction SilentlyContinue
npm --prefix server start
```

`ROUTE_SERVER_READY` を確認します。ポート3000で権限エラーが出る端末があるため、ここでは3102を使用します。

### 2. Viewerを起動する

別のPowerShellで実行します。

```powershell
$env:VITE_ROUTE_API_URL = 'http://127.0.0.1:3102/api/v1/routes'
$env:VITE_ROAD_EDGE_API_URL = 'http://127.0.0.1:3102/api/v1/road-edges'
npm --prefix viewer run dev -- --host localhost --port 5173 --strictPort
```

`http://localhost:5173/` を開き、地域、日時、起点、終点を選びます。道路別日陰率は地図を十分に拡大して確認してください。

### 3. 全24時刻を読み込む場合

5地域の入力ファイル合計は約410.5 MiBです。JSON解析・索引作成のオーバーヘッドを見込み、4 GiBヒープを推奨します。上の設定を維持して次を実行し、サーバーを再起動します。

```powershell
$env:NODE_OPTIONS = '--max-old-space-size=4096'
Remove-Item Env:ROUTE_TIMESTAMPS -ErrorAction SilentlyContinue
npm --prefix server start
```

全時刻読み込みの内訳と詳細な手順は[Viewerの地域・経路操作](docs/viewer-location-and-route-controls.md#5地域v2バンドルの全時間帯読み込みとメモリ目安)を参照してください。

## CityGMLから再生成する手順

完成済みbundleがない、または入力・施策・解析条件を更新した場合は、以下の順で再生成します。`data/raw/` と `data/generated/` は大容量ローカル成果物であり、Gitへ追加しません。

### 1. CityGMLを取得・配置する

国土交通省 PLATEAUデータカタログから取得します。京都・舞鶴・藤沢・さいたまは、取得URL、ZIPサイズ、展開先を固定したスクリプトを用意しています。

```powershell
.\tools\plateau-environment-cost-analyzer\prepare-citygml-datasets.ps1 -AreaId kyoto
.\tools\plateau-environment-cost-analyzer\prepare-citygml-datasets.ps1 -AreaId maizuru
.\tools\plateau-environment-cost-analyzer\prepare-citygml-datasets.ps1 -AreaId fujisawa
.\tools\plateau-environment-cost-analyzer\prepare-citygml-datasets.ps1 -AreaId saitama
```

ZIPと展開済みCityGMLは `data/raw/plateau-zips/`、`data/raw/plateau/` 配下に置きます。対象自治体・隣接地域・検査方法は[CityGML取得・Unity読込手順](docs/citygml-acquisition-and-unity-import.md)を参照してください。

### 2. Inspection Sceneを生成する

地域設定は `data/analysis-configs/<areaId>.json` です。カタログ照合、メッシュ範囲検査、CityGML読込、Collider・座標系確認を実施します。Unityの手動操作は **PLATEAU > Environment Cost > Create Inspection Scene**、バッチ生成は専用のUnity `-executeMethod` を使います。詳しくは[CityGML取得・Unity読込手順](docs/citygml-acquisition-and-unity-import.md)と[Inspection Sceneの実行時確認](docs/environment-cost-inspection-runtime.md)を参照してください。

### 3. 歩道対応v2グラフを生成する

CityGMLとは別に、OSMの歩行可能道路・歩道・横断接続からv2歩行グラフを生成します。OSM snapshot取得、品質契約、地域別コマンドは[歩道ネットワークv2生成](docs/sidewalk-pedestrian-network-v2-generation.md)に従ってください。

### 4. Unityで時刻別日陰解析を実行する

Inspection Sceneとv2歩行グラフを使い、道路・歩道サンプルごとに日陰率、欠測、Raycast条件を計算します。実行、キャッシュ、再実行、自己テストは[時刻別環境コスト解析](docs/hourly-environment-cost-analysis.md)を参照してください。

### 5. server bundleを生成・検証・配備する

解析結果とv2歩行グラフから、地域ごとに `data/generated/<areaId>-environment-cost-server-bundle-v2/` を生成します。バンドルには `manifest.json`、`topology.json`、時刻別 `cost-*.json` が含まれます。

生成CLI、ストリーミング入力、大容量時の注意、SHA-256検証は[道路ネットワーク生成](docs/environment-cost-road-network-generation.md)と[経路サーバーAPI](docs/route-server.md)を参照してください。サーバー環境では生成ディレクトリを配置し、`ROUTE_BUNDLE_MANIFESTS` に各 `manifest.json` を指定します。

## 開発・検証

```powershell
node tools/ci/verify.mjs
npm --prefix viewer test
npm --prefix viewer run build
```

## 資料一覧

| 資料 | 概要 |
|---|---|
| [資料索引](docs/README.md) | 資料の分類と主要な入口。 |
| [開発ガイド](docs/development.md) | ローカル開発、テスト、ブランチ運用。 |
| [バージョン方針](docs/versions.md) | Unity、Node.js、依存関係の採用方針。 |
| [自動テストとCI](docs/continuous-integration.md) | CIの検証範囲とローカル再現方法。 |
| [対象地区と入力データ](docs/target-area-and-input-data.md) | 対象地域、座標系、日時、入力データの前提。 |
| [CityGML取得・Unity読込](docs/citygml-acquisition-and-unity-import.md) | CityGML取得、範囲選定、Inspection Scene生成。 |
| [CityGML取得の検証](docs/citygml-acquisition-verification.md) | データセット取得・展開の証跡。 |
| [CityGML植生利用可否](docs/citygml-vegetation-availability.md) | 植生メッシュの収録状況と解析上の扱い。 |
| [太陽位置と3D日陰](docs/solar-position-and-3d-shadows.md) | 太陽位置、Raycast、影の基本設計。 |
| [時刻別環境コスト解析](docs/hourly-environment-cost-analysis.md) | 時間別解析、キャッシュ、性能、自己テスト。 |
| [環境コストデータ契約v1](docs/environment-cost-data-contract-v1.md) | JSON/GeoJSON形式、単位、欠測、互換規則。 |
| [道路ネットワーク生成](docs/environment-cost-road-network-generation.md) | 解析結果からserver bundleを作る手順。 |
| [市ヶ谷歩行道路グラフ](docs/ichigaya-pedestrian-road-network.md) | 市ヶ谷の旧世代ネットワーク生成・品質記録。 |
| [4地域歩行道路ネットワーク](docs/four-region-pedestrian-road-networks.md) | 4地域のネットワーク生成・確認記録。 |
| [歩道ネットワーク仕様](docs/sidewalk-pedestrian-network-specification.md) | 歩道、横断、フォールバック、安全契約の仕様。 |
| [歩道ネットワークv2生成](docs/sidewalk-pedestrian-network-v2-generation.md) | OSM取得からv2グラフ・品質レポート生成まで。 |
| [歩道日陰解析統合](docs/sidewalk-environment-cost-integration.md) | 歩道座標の日陰解析、bundle、Viewer統合。 |
| [市ヶ谷パイロット解析](docs/ichigaya-pilot-analysis.md) | 市ヶ谷の解析条件、結果、性能の記録。 |
| [Node ID付きOSM再解析](docs/reanalyze-unity-with-node-id-osm.md) | OSMノード対応を維持した再解析手順。 |
| [Inspection Scene実行時確認](docs/environment-cost-inspection-runtime.md) | Runtime表示、遮蔽物、地表、座標系の確認。 |
| [Runtime都市データパッケージ](docs/runtime-city-data-package.md) | Runtime配布用の入力都市パッケージとデータフロー。 |
| [Runtimeローカル再計算](docs/runtime-local-recalculation.md) | Player内の日陰再計算と出力の扱い。 |
| [Runtime施策シナリオ編集](docs/runtime-policy-scenario-editor.md) | 植樹・日よけ・障害物を編集するUIと保存形式。 |
| [Runtime経路比較](docs/runtime-route-comparison.md) | 施策前後の経路・KPI比較と証跡。 |
| [Runtime道路別ヒートマップ](docs/runtime-road-heatmap-comparison.md) | 道路別の現状・施策後差分表示。 |
| [Runtime UI設計](docs/runtime-ui-design.md) | UI Toolkitの構造、配色、レイアウト方針。 |
| [Runtime UI入力フォーカス](docs/runtime-ui-input-focus.md) | カメラ操作とUI入力の競合対策。 |
| [Runtime俯瞰地図](docs/runtime-overview-map.md) | 北上固定の俯瞰地図、カメラ追従、描画負荷と入力境界。 |
| [施策シナリオ](docs/policy-scenarios.md) | 施策データ、樹冠・日よけ・障害物の定義。 |
| [施策A/B比較](docs/policy-scenario-ab-comparison.md) | A/B用bundle、比較条件、指紋一致の要件。 |
| [地域・経路操作](docs/viewer-location-and-route-controls.md) | Viewerの地域、GPS、起終点、時刻、5地域v2起動。 |
| [経路サーバーAPI](docs/route-server.md) | API、bundle読込、時刻指定、複数シナリオ。 |
| [サーバー環境構築](docs/server-deployment.md) | Linux/Nginx/systemdを含む配備構成。 |
| [サーバー運用ランブック](docs/server-operation-runbook.md) | 更新、検証、再起動、障害時の復旧。 |
| [E2Eデモ運用手順](docs/e2e-demo-runbook.md) | デモ実行、性能確認、失敗時の切り分け。 |
| [Issue #12受入確認](docs/issue-12-acceptance-verification.md) | Viewerの地域・GPS・経路操作の受入証跡。 |

## ライセンス

[MIT License](LICENSE)

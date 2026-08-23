# 市ヶ谷1地域分の実解析結果

Issue #2 の入力データ検証として、市ヶ谷を中心とする半径4 kmを一括で解析した。解析は完了しており、この環境ではメッシュ分割なしで1地域分を処理できた。

## 解析条件

| 項目 | 設定 |
|---|---|
| 中心 | 市ヶ谷会場付近 `139.736043, 35.690470` |
| 対象範囲 | 中心から半径4,000 m |
| PLATEAU | 2025年版、LOD1、建物 `bldg` と道路 `tran` |
| 対象自治体データ | 千代田区、中央区、港区、新宿区、文京区、台東区、渋谷区 |
| 座標系 | JGD2011 / 平面直角座標系第IX系 |
| 道路ネットワーク | OpenStreetMap `highway`（歩行禁止・私道等を除外） |
| サンプル間隔 | 25 m |
| 歩行者高さ | 道路面から1.5 m |
| 代表日 | 2025-08-01 |
| 時間帯 | JST 08:00–17:00、1時間間隔 |
| 日陰判定 | PLATEAU建物LOD1に向けた太陽方向レイキャスト |
| 実行環境 | Unity 6000.3.18f1、PLATEAU SDK for Unity 4.3.0 |

半径4 kmは7区にまたがるため、千代田区データだけでは範囲を満たさない。公式PLATEAUカタログから7区分のCityGML ZIP（合計約9.3 GiB）を取得し、66個の3次メッシュを対象にした。

## 結果

| 指標 | 結果 |
|---|---:|
| 解析したOSM way | 45,668 |
| 入力道路セグメント | 167,074 |
| 半径内の出力エッジ | 130,521 |
| サンプル地点 | 305,118 |
| PLATEAU道路面を取得できた地点 | 229,652（75.27%） |
| 道路面を取得できなかった地点 | 75,466（24.73%） |
| 建物コライダー | 183 |
| 道路コライダー | 238 |
| CityGML取込を含む総所要時間 | 391.35秒 |
| レイキャスト解析時間 | 11.25秒 |
| 観測ピークメモリ | 約10.0 GiB |
| 結果JSON | 177,674,221 bytes（約169.4 MiB） |

有効地点で重み付けした時間帯別の日陰率は次のとおりだった。値は解析パイプラインが時刻に応じて異なる遮蔽結果を生成できていることを確認するための集計値であり、地域全体の快適性評価値として直接使用しない。

| 時刻（JST） | 日陰率 |
|---:|---:|
| 08:00 | 0.6268 |
| 09:00 | 0.5083 |
| 10:00 | 0.4174 |
| 11:00 | 0.3361 |
| 12:00 | 0.2555 |
| 13:00 | 0.3774 |
| 14:00 | 0.4933 |
| 15:00 | 0.5904 |
| 16:00 | 0.6787 |
| 17:00 | 0.7748 |

## 成果物と検証

実データはGit管理対象外で、ローカルでは次に生成される。

- `data/generated/ichigaya-pilot-environment-cost.json`
- `data/raw/ichigaya-pilot-analysis-summary.json`

結果JSONのSHA-256は `E96F68D6D3F2AB11EEB5BDA6282191A4DD6F11BA5478F8406C8CABEA0D109BDD`。全130,521エッジに08～17時の10スロットがあり、日陰率はすべて0以上1以下であることを検証した。

## 実行時の取得・解析手順

以下は2026-08-23に実行した手順である。CityGML ZIP、展開済みデータ、OSMレスポンス、結果JSONは容量のためGit管理しない。一方、取得クエリと小さい集計結果はリポジトリに記録する。

### 1. 対象CityGMLを取得する

公式PLATEAUデータカタログで市ヶ谷中心・半径4 kmに重なる自治体を確認し、次の2025年版CityGML ZIPを直接取得した。ファイル名はローカルでの管理用である。

| ID | 自治体 | ローカルファイル名 | 取得URL |
|---|---|---|---|
| `13101` | 千代田区 | `13101-2025.zip` | `https://assets.cms.plateau.reearth.io/assets/5d/dc07c5-ace7-465a-9c99-53f6d78f6164/13101_chiyoda-ku_pref_2025_citygml_1_op.zip` |
| `13102` | 中央区 | `13102-2025.zip` | `https://assets.cms.plateau.reearth.io/assets/93/1f058b-e06b-445c-ae62-2e02cdf72849/13102_chuo-ku_pref_2025_citygml_1_op.zip` |
| `13103` | 港区 | `13103-2025.zip` | `https://assets.cms.plateau.reearth.io/assets/ea/d75459-6d62-4a1f-8081-317603bd5f8d/13103_minato-ku_pref_2025_citygml_1_op.zip` |
| `13104` | 新宿区 | `13104-2025.zip` | `https://assets.cms.plateau.reearth.io/assets/84/48ebed-93d8-4196-bdda-e1db9590d3d1/13104_shinjuku-ku_pref_2025_citygml_1_op.zip` |
| `13105` | 文京区 | `13105-2025.zip` | `https://assets.cms.plateau.reearth.io/assets/4b/ac4a9f-1bdf-4978-bbfa-8ba8a80d12a0/13105_bunkyo-ku_pref_2025_citygml_1_op.zip` |
| `13106` | 台東区 | `13106-2025.zip` | `https://assets.cms.plateau.reearth.io/assets/80/45b2b1-5a88-40ab-9877-70b540b8707e/13106_taito-ku_city_2025_citygml_1_op.zip` |
| `13113` | 渋谷区 | `13113-2025.zip` | `https://assets.cms.plateau.reearth.io/assets/fd/21cbf7-ee57-42bb-a445-ea9c46f8c1b3/13113_shibuya-ku_pref_2025_citygml_1_op.zip` |

ZIPは `data/raw/plateau-zips/`、展開先は `data/raw/plateau/<ID>-2025/` とする。展開後、各ディレクトリに `udx/` と `codelists/` があることを確認する。

```powershell
tar -xf data/raw/plateau-zips/13101-2025.zip -C data/raw/plateau/13101-2025
```

### 2. OSM道路を取得する

[取得クエリ](../data/ichigaya-highways.overpassql)をOverpass APIへ送信し、結果を `data/raw/ichigaya-osm-highways-with-nodes.json` に保存する。対象bboxは `35.654497,139.692046,35.726443,139.780040` である。`body`を指定してOSMノードIDを保持し、道路グラフで座標一致ではなくOSMの接続関係を使用する。

```overpass
[out:json][timeout:180];
way["highway"](35.654497,139.692046,35.726443,139.780040);
out body geom;
```

2026-08-22に実行した初回パイロット解析は、旧クエリ`out tags geom`による`data/raw/ichigaya-osm-highways.json`を使用した。Issue #5でノードID欠落を検出したため、以後は上記のノードID付き入力へ統一する。#9で環境コストと道路グラフを結合する前に、同じOSMスナップショットを使ってUnity解析を再実行する。

具体的な再実行と検証は[ノードID付きOSMで市ヶ谷のUnity解析を再実行する](reanalyze-unity-with-node-id-osm.md)に記録する。

### 3. UnityとPLATEAU SDKを用意する

- Unity 6000.3.18f1を使用した（プロジェクトの標準版は変更しない）。
- 恒久的な解析プロジェクトは [`tools/plateau-environment-cost-analyzer/`](../tools/plateau-environment-cost-analyzer/) に置く。PLATEAU SDK for Unity 4.3.0は`manifest.json`から公式Gitリポジトリのタグ`v4.3.0`を参照する。
- 実解析時に使った `data/raw/ichigaya-unity/` と `.utmp/PLATEAU-SDK-v4.3.0/` は移行前の一時環境であり、証跡としてGit管理外に残す。以後の解析は汎用ツールを使用する。

### 4. 実行したスクリプト

現行の汎用プロジェクトの`Assets/Editor/`配下には、次のC#スクリプトを置く。市ヶ谷固有の定数は持たず、`-analysisConfig`で指定する地域設定から条件を読む。

| スクリプト | 役割 |
|---|---|
| `DatasetCatalogProbe.cs` | 設定の候補IDを使い、公式SDKサーバーからデータセットを照会する。 |
| `MeshCoverageAnalyzer.cs` | 設定の対象円と重なる3次メッシュを求め、地域別の対応表を出力する。 |
| `EnvironmentCostAnalyzer.cs` | ローカルCityGMLを`DatasetSourceConfigLocal`でインポートし、OSM道路のサンプルごとに建物遮蔽を計算してJSONを出力する。 |

主処理の`EnvironmentCostAnalyzer.cs`は、`bldg`と`tran`だけをLOD1・テクスチャなし・MeshColliderありでインポートする。OSMの歩行禁止道路等を除外し、設定した間隔ごとの地点について、まず`tran`コライダーへ下向きレイキャストして道路面を求め、道路面+設定した歩行者高さから各時刻の太陽方向へ`bldg`コライダーをレイキャストする。遮蔽物に当たれば日陰とする。

市ヶ谷は [`data/analysis-configs/ichigaya-venue.json`](../data/analysis-configs/ichigaya-venue.json) として実行条件を保持する。別地域は同じスキーマで設定ファイルを追加する。

### 5. バッチ解析を実行する

Unityのライセンス情報へアクセス可能な環境で、次の形で実行する。

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.3.18f1\Editor\Unity.exe' `
  -batchmode `
  -projectPath 'H:\MyDevelopment\PLATEAUHackathon2006\tools\plateau-environment-cost-analyzer' `
  -executeMethod MeshCoverageAnalyzer.Run `
  -analysisConfig 'data/analysis-configs/ichigaya-venue.json' `
  -logFile 'H:\MyDevelopment\PLATEAUHackathon2006\data\raw\ichigaya-venue-mesh-coverage.log'

& 'C:\Program Files\Unity\Hub\Editor\6000.3.18f1\Editor\Unity.exe' `
  -batchmode `
  -projectPath 'H:\MyDevelopment\PLATEAUHackathon2006\tools\plateau-environment-cost-analyzer' `
  -executeMethod EnvironmentCostAnalyzer.Run `
  -analysisConfig 'data/analysis-configs/ichigaya-venue.json' `
  -logFile 'H:\MyDevelopment\PLATEAUHackathon2006\data\raw\ichigaya-venue-environment-cost.log'
```

完了ログは`ENVIRONMENT_COST_ANALYSIS_COMPLETE`を含む。入力・設定に変更がない場合、出力のSHA-256が前掲の値と一致することを確認する。今回の実行済み出力は旧一時環境で生成した`ichigaya-pilot`名を保管しており、汎用ツールで再実行した場合は設定の`ichigaya-venue`名の出力先を使用する。

### 6. 出力を検証する

`data/generated/ichigaya-pilot-environment-cost.json`を読み取り、次を確認する。

- エッジ数が130,521件である。
- 時刻スロットが08〜17時の10件である。
- `shadeRatio`が`null`でない値は0以上1以下である。
- `validSampleCount + noGroundSampleCount = sampleCount` が集計上成立する。
- 出力サイズとSHA-256を本資料の結果と照合する。

## 判断と残課題

- 1地域分の一括解析は成功したため、メッシュ分割はMVPの必須条件にしない。低メモリ環境、再実行時間短縮、部分更新が必要になった場合はIssue #26で導入する。
- 24.73%の地点はOSM道路とPLATEAU道路面を照合できなかった。出力エッジのうち30,847件は有効サンプルがなく、日陰率を`null`としている。経路探索へ組み込む際は、最近傍道路面へのスナップ、道路LODの収録範囲確認、欠測時の既定コストを実装する必要がある。
- 169.4 MiBのJSONをブラウザへそのまま配信するのは大きい。デモ経路に必要な範囲・時刻だけをAPIで返すか、タイル化・圧縮・集約を行う。
- 今回は建物遮蔽のみを計算した。植生データの地域差は既定どおり許容し、別条件としてメタデータに残す。
- デモの起点・終点は固定せず、地図UIでそれぞれ選択し、道路ネットワークへスナップしたうえでこのコストを経路計算に利用する。

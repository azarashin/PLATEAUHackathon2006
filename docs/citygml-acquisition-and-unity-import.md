# 4地域の CityGML 取得・Unity 読込手順

Issue #4 の前半（CityGML の取得、対象メッシュの選定、Unity 読込、Collider、座標系確認）を再現するための手順です。対象は京都市駅、東舞鶴駅、藤沢駅、大宮区天沼町2丁目の各中心点から半径4 kmです。

## 取得元と対象データ

取得元は国土交通省 PLATEAU の[データカタログ](https://api.plateauview.mlit.go.jp/datacatalog/plateau-datasets)です。URL、カタログ掲載ZIPサイズ、展開先は、追跡対象の取得マニフェストに固定しています。

| 地域 | 利用する自治体データ | マニフェスト |
| --- | --- | --- |
| 京都 | 京都市（2025） | `data/plateau-citygml-manifests/kyoto.json` |
| 舞鶴 | 舞鶴市（2025） | `data/plateau-citygml-manifests/maizuru.json` |
| 藤沢 | 藤沢市（2025）、鎌倉市（2024）、横浜市（2024） | `data/plateau-citygml-manifests/fujisawa.json` |
| さいたま | さいたま市（2025）、上尾市（2025）、川口市（2024） | `data/plateau-citygml-manifests/saitama.json` |

半径4 kmが市境をまたぐ藤沢・さいたまでは、境界側の隣接自治体データも使います。年次が混在しますが、これは今回のMVPで許容した前提です。データ本体（ZIPと展開済みCityGML）は大容量のため、`data/raw/`に置き、Gitには追加しません。

## ダウンロードと展開

リポジトリ直下で、対象地域ごとに次を実行します。

```powershell
.\tools\plateau-environment-cost-analyzer\prepare-citygml-datasets.ps1 -AreaId kyoto
.\tools\plateau-environment-cost-analyzer\prepare-citygml-datasets.ps1 -AreaId maizuru
.\tools\plateau-environment-cost-analyzer\prepare-citygml-datasets.ps1 -AreaId fujisawa
.\tools\plateau-environment-cost-analyzer\prepare-citygml-datasets.ps1 -AreaId saitama
```

スクリプトは、マニフェストの公式URLからZIPを取得し、カタログ掲載サイズの±1%以内であることとZIPの一覧読取を確認してから `data/raw/plateau/<dataset>-<municipality>-<year>/` に展開します。配信ファイル長がカタログ表示と数バイト異なる場合があるため、厳密一致ではなくこの許容幅を使います。既存ZIPは再利用します。途中で中断したZIPは、サイズ検査に通らなければ削除して同じコマンドを再実行します。展開先に不完全なファイルが残っている場合は、混在を避けるため停止します。

完了時の `CITYGML_READY` 行には、データセットID・バイト数・SHA-256・相対展開先が出力されます。これを実行記録に残します。

## メッシュ選定とカタログ確認

Unity 6000.3.18f1 で、各地域について以下を順に実行します。`$area` を `kyoto`、`maizuru`、`fujisawa`、`saitama` に変えて実行してください。

```powershell
$unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.18f1\Editor\Unity.exe'
$project = (Resolve-Path '.\tools\plateau-environment-cost-analyzer').Path
$area = 'kyoto'
$config = "data/analysis-configs/$area.json"

& $unity -batchmode -projectPath $project -executeMethod DatasetCatalogProbe.Run -analysisConfig $config -logFile "data/raw/$area-dataset-catalog.log"
& $unity -batchmode -projectPath $project -executeMethod MeshCoverageAnalyzer.Run -analysisConfig $config -logFile "data/raw/$area-mesh-coverage.log"
```

`DatasetCatalogProbe` は設定された全データセットIDが公式カタログで解決できない場合に失敗します。`MeshCoverageAnalyzer` は中心点・半径と交差する標準地域メッシュを `coverageOutputPath`（例: `data/raw/kyoto-mesh-coverage.json`）へ出力します。

メッシュコードは、8桁コードがある場所ではその子メッシュを優先し、8桁コードを持たないデータセットでは6桁コードを保持します。この正規化により、親子メッシュの二重読込を防ぎながら、上尾市・川口市のように6桁コードのみを公開するデータセットも対象から外しません。

## Unity 読込・Collider・座標系の確認

Unity Editorで `tools/plateau-environment-cost-analyzer` を開き、**PLATEAU > Environment Cost > Create Inspection Scene** を選び、対象の設定ファイルを指定します。生成される検証用Sceneは `Assets/Scenes/EnvironmentCostInspection/<areaId>.unity` です（ローカル生成物のためGit管理外）。地域ごとに別ファイルとなるため、京都を生成しても市ヶ谷など他地域のSceneは上書きされません。同じ地域を再生成する場合だけ置換確認が表示されます。

CityGMLの準備済み地域を自動生成する場合は、Unity Editor と Unity Hub を閉じてから `-executeMethod EnvironmentCostInspectionSceneBuilder.Run -analysisConfig data/analysis-configs/<areaId>.json` を指定してUnityをバッチ起動します。既存の同一地域Sceneは上書きせず終了コード `1` で停止するため、地域ごとに安全に1回ずつ実行できます。完全なPowerShellコマンドは解析ツールのREADMEを参照してください。

この処理はPLATEAU SDK for Unity 4.3.0の `CityImporter` APIを使い、選定済みメッシュから `bldg`、`tran`、`dem` を利用可能なLODのうちLOD1以下で読み込みます。建物には `Building`（layer 8）、道路には `Road`（layer 9）、地形には `Terrain`（layer 10）の `MeshCollider` を付与します。完了ログ `ENVIRONMENT_COST_INSPECTION_SCENE_READY` の建物・道路・地形Collider数がすべて0より大きいことを確認します。建物と地形は明示的に影を投影し、道路は影を受けます。実行時カメラとWindows Playerの確認は [環境コスト Inspection Scene のDEM・影・実行時確認](environment-cost-inspection-runtime.md) を参照してください。

座標系は設定ファイルの `coordinateZoneId` をPLATEAU SDKの `GeoReference` へ渡します。京都・舞鶴は平面直角座標系第VI系（zone 6、EPSG:6674）、藤沢・さいたまは第IX系（zone 9、EPSG:6677）です。Scene上で建物と道路が同一地点に重なり、極端に離れないことを確認します。

## このフェーズの成果物

| 成果物 | 用途 | Git管理 |
| --- | --- | --- |
| `data/plateau-citygml-manifests/*.json` | 取得元・カタログ掲載サイズ・展開先の再現 | 管理する |
| `data/raw/plateau-zips/` と `data/raw/plateau/` | ZIPと展開済みCityGML | 管理しない |
| `data/raw/<area>-dataset-catalog.log` | データセットIDのカタログ照合 | 管理しない |
| `data/raw/<area>-mesh-coverage.json` | 中心点・半径から選んだメッシュ | 管理しない |
| `Assets/Scenes/EnvironmentCostInspection/<areaId>.unity` | Unity読込・Colliderを目視確認する地域別Scene | 管理しない |

## Issue #4 の残作業

この文書は前半の基盤までを扱います。DEMの読込、影を落とすオブジェクトの明示設定、実行時カメラ、対象プラットフォームでのビルド確認はIssue #4に残します。日照・影・道路サンプルの実解析はIssue #6・#7、10時刻集計と品質・性能記録はIssue #8で扱います。

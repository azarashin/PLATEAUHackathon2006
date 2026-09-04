# Runtime 地名抽出

Issue #87では、Runtimeの俯瞰地図で後続の表示機能が利用する地名データを、Unity Sceneや結合済みMeshではなく、取得・配置済みの**CityGML原典**からEditor時に抽出する。

## 出力と失敗の扱い

`environment-cost-runtime-city-package-0.2` は次の2ファイルを必須として `StreamingAssets` の都市パッケージへ格納する。

| ファイル | 内容 |
| --- | --- |
| `place-labels.json` | `text`、常に `[longitude, latitude]` の `coordinate`、GML `id`、出典ファイル・要素・EPSG・優先度を持つラベル一覧 |
| `place-label-report.json` | 入力数、読取成功数、分類別件数・代表例、`reasonCodes`、個別の `parseErrors`、検出EPSG、出典版・取得日時・dataset ID |

入力CityGMLが未配置、地名が0件、または一部GMLが壊れている場合でも都市パッケージ生成は失敗させない。空の一覧と報告を出力し、`citygml-source-not-found`、`citygml-parse-errors`、`no-place-labels-extracted` を記録する。これは地名表示が日陰解析・施策編集の前提ではないためである。一方、manifest 0.2からこれらの出力自体が欠ける場合はRuntime loaderが不正パッケージとして拒否する。0.1 manifestは既存Playerとの互換のため従来どおり読み込める。

## 座標契約

座標の値（例えば139や35）から経度・緯度の順序を推測しない。各 `data/runtime-city-packages/*-sidewalk-v2.json` は `placeLabelCoordinateAxis` を明示し、現行5地域はURF CityGMLの `gml:pos` / `gml:posList` に合わせて `latitude-longitude` を指定する。抽出器はGMLごとにPLATEAU SDKの `GmlFile.Epsg` を取得し、`GeoReference` で投影・逆投影して `[longitude, latitude]` へ正規化する。これはScene importと同じ `coordinateZoneId` 契約を用いる。将来、平面直角座標を直接供給する場合だけ `northing-easting-up` を明示し、SDKのCRS変換を使う。

CityGMLの座標系・軸順がこの契約と異なる取得物を使う場合は、値から補正せず設定を変更し、少数のラベルを地図上で検証してからパッケージを再生成する。

ラベルIDは `<datasetId>:<CityGML原典への相対path>:<gml:id>` として決定的に生成する。名称はUnicode FormKCで正規化し、同名かつ30m以内の候補は分類別優先度（`CityObjectGroup`、`LandUse`、`GenericCityObject`、その他の順）が高いものへ統合する。`place-label-report.json`には分類別件数と代表例を残す。

取得planがある京都・藤沢・舞鶴・さいたまでは、`data/plateau-citygml-manifests/<areaId>.json` を機械読取し、plan SHA-256、PLATEAU provider、dataset year、URL、取得日時（planが未記録なら`unknown`）をreportと都市package manifestの`citygml-acquisition-plan` sourceへ記録する。市ヶ谷はこの取得manifestが未登録のため、`citygml-acquisition-manifest-missing`と`citygml-acquisition-plan-missing`を明記する。

## 生成

既存のv2都市パッケージ生成を実行する。`datasetRoots` にCityGMLが配置済みであれば、設定された全データセット根から `.gml` / `.xml` を探索する。

```powershell
$unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.18f1\Editor\Unity.exe'
& $unity -batchmode -quit -nographics `
  -projectPath 'tools\plateau-environment-cost-analyzer' `
  -executeMethod EnvironmentCostRuntimeCityPackageBuilder.Run `
  -runtimeCityPackageConfig 'data\runtime-city-packages\ichigaya-venue-sidewalk-v2.json' `
  -logFile 'tools\plateau-environment-cost-analyzer\Logs\runtime-city-package-ichigaya-v2.log'
```

5地域（市ヶ谷、京都、舞鶴、藤沢、さいたま）には同じ `placeLabelCoordinateAxis` 設定を追加している。CityGMLの実データは大容量かつGit管理外なので、各環境での抽出件数・解析不能ファイル・`sourceEpsgCodes` は `place-label-report.json` を成果証跡として確認する。

地名在庫を含むv0.2 manifestはSHA-256が変わるため、既存の0.1 package manifest SHAを保存した施策シナリオ・日陰解析結果・経路比較結果はv0.2 packageでは読み込めない。自動移行は行わず、対象都市packageを再生成した後、施策シナリオを手動で作り直し、日陰解析と経路比較を再実行する。既存の`cityPackageManifestSha256`は引き続き実manifestファイルそのもののSHA-256を意味する。

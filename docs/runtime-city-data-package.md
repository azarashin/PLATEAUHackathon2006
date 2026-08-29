# Runtime 都市データパッケージ

## 目的

Runtime 配布版では Unity Editor、AssetDatabase、PLATEAU SDK のインポータを使わない。都市を開くために必要なデータを、Player 本体と都市ごとのパッケージに分ける。

市ヶ谷の構成定義は [`data/runtime-city-packages/ichigaya-venue.json`](../data/runtime-city-packages/ichigaya-venue.json) である。生成物はローカルの `Assets/StreamingAssets/EnvironmentCostCities/<areaId>/` に置き、容量が大きいため Git には入れない。

| 配布物 | 内容 | 役割 |
| --- | --- | --- |
| Player | 市域の表示 Mesh、Raycast 用 Collider、Runtime 操作コード | 地形・建物・植生を表示し、日射・遮蔽判定の対象にする |
| 都市パッケージ | 道路トポロジー、時刻別道路コスト、基準環境コスト、完全性 manifest | 経路・基準値を Runtime で参照する |

この分離は、描画 Mesh と衝突判定を同じ CityGML 由来 Scene に保持しつつ、容量の大きい道路・コストデータを都市単位で差替え可能にするためのもの。市ヶ谷 Runtime Player が実際に利用するのは、この Player と同じ `areaId` のパッケージの組である。

## 生成

Unity Hub と Editor を完全に終了してから、プロジェクトのルートで次を実行する。

```powershell
$unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.18f1\Editor\Unity.exe'
& $unity -batchmode -quit -nographics `
  -projectPath 'tools\plateau-environment-cost-analyzer' `
  -executeMethod EnvironmentCostRuntimeCityPackageBuilder.Run `
  -runtimeCityPackageConfig 'data\runtime-city-packages\ichigaya-venue.json' `
  -logFile 'tools\plateau-environment-cost-analyzer\Logs\runtime-city-package-ichigaya.log'
```

成功時には `ENVIRONMENT_COST_RUNTIME_CITY_PACKAGE_READY` が出力される。ビルダーは一時ディレクトリに全ファイルをコピーし、SHA-256 とバイト数を検証してから既存パッケージを置き換える。

## manifest と検証

`manifest.json` には次を格納する。

- `areaId`、版、平面直角座標系の系番号、中心座標、対象半径
- 使用する検証 Scene の Asset パス
- 基準環境コストと道路バンドルの入力 SHA-256
- パッケージ全ファイルの相対パス、サイズ、SHA-256
- 必須の `Building`、`Road`、`Terrain` レイヤーと用途

Runtime 起動時の `EnvironmentCostRuntimeCityPackageLoader` は manifest、全ファイルのサイズ・SHA-256、Scene とパッケージの地域・座標系・範囲、必須 Collider レイヤーを検証する。欠落、改変、版・範囲不一致なら状態オーバーレイと Console に理由を表示し、以後の編集・再計算の開始点として利用しない。

## Player の作成と確認

1. 先に Inspection Scene を生成する。Scene Builder は `EnvironmentCostRuntimeCityPackageLoader` をシーンのルートへ付加する（既存のローカル Scene には Player 起動時に自動付加する）。
2. 上記の都市パッケージを生成する。
3. `Assets/Scenes/EnvironmentCostInspection/ichigaya-venue.unity` を開き、`PLATEAU > Environment Cost > Build Inspection Player (Windows)` を実行する。バッチでは `EnvironmentCostRuntimeCityPlayerBuild.Run` と同じ `-runtimeCityPackageConfig` を使う。
4. Player 起動後、ロード完了なら Console に `ENVIRONMENT_COST_RUNTIME_CITY_PACKAGE_READY` が出る。ファイルがない、壊れている、または別都市の Scene と組み合わせた場合は画面左上に失敗理由が表示される。

初期版では `StreamingAssets` に同梱する。配布後の都市追加・更新は、同一の manifest 検証を通るダウンロードキャッシュへ展開する方式を #61 以降で追加する。Addressables はこの初期パッケージに必須ではないため、Editor 専用の Addressables 設定を Runtime の前提にしない。

# 歩行用道路グラフ生成

`build-pedestrian-graph.mjs`は、OSMノードIDを含むOverpass JSONから、Viewer・Unity・経路探索で共有する歩行道路グラフを再生成します。環境コスト解析より先に実行でき、`sourceEdgeIds`を使って後から#9の環境コストを結合します。

## 実行

```powershell
node tools/road-network/build-pedestrian-graph.mjs `
  --config data/analysis-configs/ichigaya-venue.json `
  --osm data/raw/ichigaya-osm-highways-with-nodes.json `
  --overrides data/road-network-overrides.geojson `
  --output data/generated/ichigaya-pedestrian-road-network.json `
  --report data/raw/ichigaya-pedestrian-road-network-quality.json `
  --route-start 139.7349,35.6910 `
  --route-end 139.7669,35.6812
```

OSM取得クエリには`out body geom`を指定してください。`out tags geom`にはノードIDがなく、座標上は交差していても接続していない橋・トンネル等を区別できません。ノードIDがない入力は品質検査で失敗します。

## 変換規則

- OSM wayの隣接ノードごとに物理辺を作り、同一OSMノードIDだけを接続する。
- 中心点から半径4 kmの円と交差する辺だけを残す。
- `motorway`、`trunk`、工事中道路、`foot=no`等を除外する。
- 歩行者は原則双方向とし、`oneway:foot`または`foot=forward/backward`だけを歩行の一方向制約として扱う。車両用`oneway`は歩行者へ適用しない。
- 既定歩行速度は地域設定の`walkingSpeedMetersPerSecond`を使う。市ヶ谷は1.4 m/sである。
- 同じ両端OSMノードを持つ物理重複は1本へ統合し、全入力IDを`sourceEdgeIds`へ残す。
- ノードIDは`osm-node-<OSM node ID>`、入力辺IDは`osm-way-<OSM way ID>-<区間番号>`、有向辺IDは入力辺IDと方向から決定する。

出力契約は[pedestrian-road-network.schema.json](pedestrian-road-network.schema.json)です。WGS84（EPSG:4326）の経緯度と、地域設定の平面直角座標系・PLATEAU `GeoReference`原点との対応をメタデータに持ちます。

## 品質検査と補正

品質レポートはID一意性、参照整合性、ゼロ長辺、自己ループ、物理重複、連結成分、孤立ノード、行き止まり、手動補正を記録します。グラフの安定部分からSHA-256を計算するため、同じ入力から同じID・接続・距離が生成されたことを再実行で比較できます。

手動補正は`data/road-network-overrides.geojson`へ記録します。現在対応する操作は`remove-edge`です。各Featureには`id`、`areaId`、`operation`、`sourceEdgeId`、`reason`、`evidence`、`createdAt`、`reviewer`が必要です。raw OSMは直接編集しません。

## テスト

```powershell
node tools/road-network/test-build-pedestrian-graph.mjs
```

テストはOSMノード接続、立体交差相当の非接続、歩行一方向、重複統合、手動除外、最短経路、入力順を変えた場合の決定性、不正入力の検出を確認します。

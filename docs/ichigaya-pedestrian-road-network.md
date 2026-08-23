# 市ヶ谷周辺の歩行道路グラフ

Issue #5の実行記録です。OSMノードIDを含むOpenStreetMap Overpass JSONから、市ヶ谷周辺（中心点から半径4 km）の歩行道路グラフを生成しました。Unity環境コスト解析には依存せず、OSM way IDと区間番号で後続の環境コストを結合できる構造です。

## 入力

| 項目 | 値 |
|---|---|
| 地域設定 | `data/analysis-configs/ichigaya-venue.json` |
| OSM取得クエリ | `data/ichigaya-highways.overpassql`（`out body geom`） |
| OSMスナップショット | 2026-08-23T03:03:03Z |
| OSMローカル入力 | `data/raw/ichigaya-osm-highways-with-nodes.json` |
| 手動補正 | `data/road-network-overrides.geojson` |
| 既定歩行速度 | 1.4 m/s |

初回の環境コスト解析に使った旧OSM入力はノードIDを含まないため、#9で結合する前に同じノードID付きスナップショットでUnity解析を再実行します。

再実行コマンドと道路グラフとの整合性検証は[ノードID付きOSMで市ヶ谷のUnity解析を再実行する](reanalyze-unity-with-node-id-osm.md)を参照してください。

## 実行コマンド

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

生成グラフと品質レポートは再生成可能な大容量データであるためGit管理外です。ツール、JSON Schema、取得クエリ、地域設定、補正差分、この実行記録をGit管理します。

## 品質結果

| 項目 | 値 |
|---|---:|
| OSM way数 | 48,570 |
| 歩行対象way数 | 45,696 |
| ノード数 | 108,169 |
| 有向辺数 | 260,779 |
| 物理道路辺数 | 130,508 |
| 統合した物理重複 | 115 |
| 連結成分数 | 376 |
| 最大連結成分のノード数 | 105,990（98.0%） |
| 小規模連結成分（10ノード以下） | 343 |
| 孤立ノード数 | 0 |
| 行き止まりノード数 | 7,860 |
| 不正形状／ノード座標競合 | 0／0 |
| 重複ID／参照不整合／ゼロ長辺／自己ループ | 0／0／0／0 |
| 手動補正 | なし |
| グラフSHA-256 | `3bac211ccfba878ed6c0b4841ab9a36f52c47a1aed9fad223f9180397aedf3ed` |

同じ入力で2回生成し、グラフSHA-256が一致することを確認しました。`generatedAt`は実行ごとに変わりますが、ノードID、辺ID、接続、距離、歩行時間、入力辺対応は同一です。

## 最短経路確認

品質確認用の起点（139.7349, 35.6910）と終点（139.7669, 35.6812）をそれぞれ最近傍道路辺へスナップし、到達可能であることを確認しました。

| 項目 | 値 |
|---|---:|
| 起点から道路辺まで | 67.1 m |
| 終点から道路辺まで | 0.4 m |
| 道路グラフ上の最短距離 | 4,111.3 m |
| 歩行時間 | 2,936.6秒（約48.9分） |
| 通過有向辺数 | 192 |

この起終点はUIへ固定せず、再生成時の回帰確認だけに使用します。起点の67.1 mは市ヶ谷駅の代表座標と抽出した歩行道路中心線の差であり、UIでは許容スナップ距離を明示して遠すぎる地点を拒否する必要があります。その閾値と操作体験は#11・#12で決定します。

## 接続と立体交差

OSMノードIDをグラフノードの正本とします。道路形状が平面上で交差しても、OSMノードIDを共有しない橋・トンネル・非接続交差は接続しません。OSM wayは各ノード間で分割済みのため、通常の交差点では共有OSMノードを介して接続します。

座標はWGS84（EPSG:4326）です。Unityでは市ヶ谷の平面直角座標系IX系（EPSG:6677）、軸順EUN、地域中心点を`GeoReference`原点として同じ地点へ投影します。

## 手動補正

現時点では補正していません。補正が必要になった場合は[road-network-overrides.geojson](../data/road-network-overrides.geojson)へ根拠付きの`remove-edge`差分を記録し、品質レポートと最短経路を再検証します。追加道路やノード結合が必要になった場合は、推測で接続せず、根拠と追加操作の契約を先に拡張します。

## 後続Issueへの引き継ぎ

- #9: 同じOSMスナップショットでUnity解析を再実行し、`sourceEdgeIds`で時刻別環境コストを結合・軽量化する。
- #11: このグラフに環境コスト重みを載せ、通常最短・環境優先経路を比較する。
- #12: 地図クリックの道路辺スナップ、許容距離、到達不能地点の拒否をUIへ接続する。

# 4地域の歩行道路ネットワーク

Issue #36の実行記録です。市ヶ谷で検証済みの道路グラフ生成基盤を、京都・舞鶴・藤沢・さいたまの
課題検討地域へ展開しました。対象は各中心から半径4 kmで、CityGMLの取得・Unity環境コスト解析・
サーバーバンドル生成は含みません。それらは後続のIssue #4で、このグラフと同じOSMスナップショットを入力にします。

## 入力と再現性

各地域の設定は [`data/analysis-configs/`](../data/analysis-configs/) にあり、中心、半径、平面直角座標系、
Unity解析用の入力・出力パスを固定しています。OSMのraw JSONと生成グラフは大容量のためGit管理外です。
代わりに、取得クエリ、台帳、検証結果をGit管理します。

| 地域 | 中心 | 座標系 | OSMクエリ | スナップショット台帳 | 検証結果 |
|---|---|---|---|---|---|
| 京都 | 京都駅 | 第VI系 / EPSG:6674 | [`kyoto-highways.overpassql`](../data/osm-queries/kyoto-highways.overpassql) | [`kyoto.json`](../data/osm-snapshot-manifests/kyoto.json) | [`kyoto`](../data/kyoto-pedestrian-road-network-verification.json) |
| 舞鶴 | 東舞鶴駅 | 第VI系 / EPSG:6674 | [`maizuru-highways.overpassql`](../data/osm-queries/maizuru-highways.overpassql) | [`maizuru.json`](../data/osm-snapshot-manifests/maizuru.json) | [`maizuru`](../data/maizuru-pedestrian-road-network-verification.json) |
| 藤沢 | 藤沢駅 | 第IX系 / EPSG:6677 | [`fujisawa-highways.overpassql`](../data/osm-queries/fujisawa-highways.overpassql) | [`fujisawa.json`](../data/osm-snapshot-manifests/fujisawa.json) | [`fujisawa`](../data/fujisawa-pedestrian-road-network-verification.json) |
| さいたま | 大宮区・天沼町2丁目 | 第IX系 / EPSG:6677 | [`saitama-highways.overpassql`](../data/osm-queries/saitama-highways.overpassql) | [`saitama.json`](../data/osm-snapshot-manifests/saitama.json) | [`saitama`](../data/saitama-pedestrian-road-network-verification.json) |

すべて`way["highway"](...); out body geom;`で取得しています。`out body`によりOSMノードIDを保持するため、
立体交差など座標が交差しても接続しない道路を誤って接続しません。各台帳にはOverpassの基準時刻、取得日時、
サイズ、SHA-256、必要なwayフィールドを記録しています。

## 生成結果（2026-08-24）

| 地域 | raw OSM | OSM way | ノード | 物理辺 | 有向辺 | 連結成分 | 最大成分ノード | 孤立ノード | 手動補正 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 京都 | 16,786,578 B | 25,523 | 66,121 | 79,360 | 158,719 | 190 | 64,214 | 0 | 0 |
| 舞鶴 | 2,044,609 B | 2,901 | 9,983 | 11,538 | 23,076 | 16 | 9,550 | 0 | 0 |
| 藤沢 | 10,441,732 B | 14,764 | 60,160 | 66,572 | 133,144 | 272 | 57,627 | 0 | 0 |
| さいたま | 11,402,953 B | 17,798 | 46,924 | 56,293 | 112,586 | 111 | 46,161 | 0 | 0 |

4地域とも、同じ設定・OSMスナップショット・補正GeoJSONからグラフを2回生成し、
`graphFingerprintSha256`が一致することを確認しました。ID重複、参照切れ、ゼロ長辺、自己ループ、
不正なOSM区間、同一ノードIDの座標競合はすべて0件です。物理辺の重複統合は京都5件、舞鶴0件、
藤沢3件、さいたま4件で、入力の同一区間を統合した正常な診断値です。各地域の完全な値とフィンガープリントは
上表の検証JSONを正本とします。

連結成分と行き止まりはOSMの歩行可能な道路・歩道・施設内通路をそのまま反映します。推測で成分間を接続せず、
デモ地点は最大連結成分に到達する道路へスナップできる組合せを選びました。補正が必要になった場合だけ、
[`data/road-network-overrides.geojson`](../data/road-network-overrides.geojson)へ地域ID・根拠付きで追加します。

## デモ経路の確認

起終点は最終的に地図UIで任意に選択します。下表は3〜5 kmの経路が得られることを確認するための代表入力です。
「スナップ後」は道路グラフ上の実際の位置であり、UIや後続のサーバー検証で同じ地点を再利用できます。

| 地域 | 要求起点（経度, 緯度） | 要求終点（経度, 緯度） | 起点/終点スナップ距離 | 経路長 | 歩行時間 |
|---|---|---|---:|---:|---:|
| 京都 | `135.758770,34.985350` | `135.748000,35.014000` | 42.8 m / 19.7 m | 4,071.8 m | 2,908.4 s |
| 舞鶴 | `135.394695,35.468540` | `135.420861,35.488991` | 9.1 m / 0.0 m | 3,828.2 m | 2,734.4 s |
| 藤沢 | `139.487293,35.338882` | `139.483000,35.310000` | 11.1 m / 5.5 m | 3,691.8 m | 2,637.0 s |
| さいたま | `139.640025,35.900757` | `139.617000,35.877000` | 6.6 m / 7.3 m | 4,185.6 m | 2,989.7 s |

すべて到達可能で、スナップ距離はサーバーの既定上限250 m以内です。舞鶴の終点は、初期の候補座標が道路から334.8 m離れていたため、
同じ経路上の道路点`135.420861,35.488991`へ変更しました。道路から遠い施設中心点をそのまま固定しない判断を記録するものです。

現在のグラフ生成CLIは道路**辺**へ、経路サーバーは道路**ノード**へスナップします。4地域の環境コストと
サーバーバンドルが未生成の現段階ではCLIでの辺スナップを検証済みとし、Issue #4でバンドルを作成した時点で
同じ代表地点のサーバーAPIスナップ・到達性を再確認します。Viewerの4地域はすでに選択できますが、
`availableTimestamps`は環境コスト未生成を表す空配列のままです。

## 再生成手順

以下は京都の例です。`$areaId`を`maizuru`、`fujisawa`、`saitama`へ変えると同じ手順を使えます。

```powershell
$areaId = 'kyoto'

# OSMスナップショット（ネットワーク取得）
node tools/road-network/capture-osm-snapshot.mjs `
  --config "data/analysis-configs/$areaId.json" `
  --output "data/raw/osm/$areaId/highways-with-nodes.json" `
  --query "data/osm-queries/$areaId-highways.overpassql" `
  --manifest "data/osm-snapshot-manifests/$areaId.json"

# Node.jsがプロキシを利用できない環境では、生成済みクエリをcurlで送信する。
curl.exe --fail --data-urlencode "data@data/osm-queries/$areaId-highways.overpassql" `
  https://overpass-api.de/api/interpreter `
  --output "data/raw/osm/$areaId/highways-with-nodes.json"
node tools/road-network/capture-osm-snapshot.mjs `
  --config "data/analysis-configs/$areaId.json" `
  --output "data/raw/osm/$areaId/highways-with-nodes.json" `
  --query "data/osm-queries/$areaId-highways.overpassql" `
  --manifest "data/osm-snapshot-manifests/$areaId.json" `
  --existing-snapshot

# グラフと品質レポート（大容量のためローカル生成物）
node tools/road-network/build-pedestrian-graph.mjs `
  --config "data/analysis-configs/$areaId.json" `
  --osm "data/raw/osm/$areaId/highways-with-nodes.json" `
  --overrides data/road-network-overrides.geojson `
  --output "data/generated/$areaId-pedestrian-road-network.json" `
  --report "data/raw/$areaId-pedestrian-road-network-quality.json"

# 同一入力での決定性、品質、代表経路を記録する。
node tools/road-network/verify-regional-road-network.mjs `
  --config "data/analysis-configs/$areaId.json" `
  --osm "data/raw/osm/$areaId/highways-with-nodes.json" `
  --snapshot-manifest "data/osm-snapshot-manifests/$areaId.json" `
  --overrides data/road-network-overrides.geojson `
  --start 135.758770,34.985350 `
  --end 135.748000,35.014000 `
  --report "data/$areaId-pedestrian-road-network-verification.json"
```

スナップショットを再取得するとOSMの内容・基準時刻・SHA-256が変わり得ます。その場合は、グラフ、品質レポート、
検証JSON、後続のUnity環境コスト解析を必ず同じ新しい入力から再生成します。

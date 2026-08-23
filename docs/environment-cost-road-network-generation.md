# 環境コスト道路ネットワークの生成

Issue #9では、#5の歩行道路グラフを接続・方向・距離・歩行時間の正本とし、#8のUnity解析結果を`sourceEdgeIds`で結合して、#3の正式契約`environment-cost-road-network-1.0`を生成する。

## 入力と出力

| 種別 | パス | Git管理 |
|---|---|---|
| 歩行道路グラフ | `data/generated/ichigaya-pedestrian-road-network.json` | しない |
| Unity時間別解析 | `data/generated/ichigaya-venue-environment-cost.json` | しない |
| 正式契約JSON | `data/generated/ichigaya-environment-cost-road-network-v1.json` | しない |
| 結合レポート | `data/raw/ichigaya-environment-cost-road-network-integration-report.json` | しない |
| 小型fixture | `data/fixtures/environment-cost-road-network-integration-v1.json` | する |
| 実データ検証値 | `data/ichigaya-environment-cost-road-network-verification.json` | する |

正式契約にはWGS84のノード・道路形状、安定ID、接続、方向、距離、歩行時間、全時刻の状態・サンプル集計・日陰率・日射曝露時間だけを含める。CityGML、3Dメッシュ、Collider、Unityオブジェクト、ローカル入力パスは含めない。Unity座標系は地理座標との対応を説明するメタデータとしてだけ残す。

## 結合規則

道路グラフの1物理辺は、OSMで重複している複数の解析元辺を`sourceEdgeIds`に持つ場合がある。時刻ごとに次の規則で1つの値へ集約する。

1. `sampleCount`、`validSampleCount`、`noGroundSampleCount`は対応する解析元辺で合計する。
2. `shadeRatio`は`validSampleCount`による加重平均とする。
3. `solarExposureSeconds`は正式道路グラフの歩行時間を使い、`walkingSeconds * (1 - shadeRatio)`で再計算する。
4. 有効サンプル0は`missing`、有効・欠測混在は`partial`、全サンプル有効は`available`とする。
5. 方向違いの有向辺は同じ物理辺の解析値を持つが、形状の始終点とノード参照は進行方向へ合わせる。

欠測は0ではない。正式契約v1は解析側の詳細な`exclusionReason`フィールドを持たないため、`status`、`sampleCoverage`、`null`の組合せで欠測を表す。

## グラフ境界の不一致

市ヶ谷では130,508物理辺のうち130,396辺を解析結果へ完全結合できた。112辺（0.0858%）は、道路グラフが「線分と半径4 km円の交差」で採用する一方、Unity解析が25 m間隔のサンプル点を円内に持たず、解析元辺を出力しない境界差だった。

既定動作はこの112辺を不完全入力として失敗させる。実測済みの市ヶ谷生成では`--allow-unmatched-as-missing`を明示し、道路自体を落とさず、全時刻`missing`、サンプル数0で保持した。一部の`sourceEdgeIds`だけが結合できる場合は集約根拠が曖昧なため常に失敗する。道路グラフに存在しない解析元辺10件は、手動除外・正規化後のグラフを正本とするため出力しないが、件数を監査メタデータへ残す。

## 座標変換の検証

`japan-plane-rectangular.mjs`はGRS80/JGD2011、縮尺係数0.9999の平面直角座標系を19系すべて実装する。UnityのEUNは、解析中心の平面座標を原点として、`X=東向き`、`Y=上向き`、`Z=北向き`とする。

第IX系について、[国土地理院の測量計算サイト](https://vldb.gsi.go.jp/sokuchi/surveycalc/main.html)が返す次の既知値と比較した。

| 既知点 | 経度・緯度 | X（北、m） | Y（東、m） |
|---|---|---:|---:|
| 市ヶ谷解析中心 | `139.736043, 35.690470` | -34,336.4566 | -8,805.3267 |
| 東京駅付近 | `139.767125, 35.681236` | -35,363.2377 | -5,992.9196 |

両点とも公式値との差は1 mm未満だった。市ヶ谷をUnity原点とした東京駅付近は`[X=2812.4071, Y=0, Z=-1026.7811]`となる。実道路ノード3点の地理座標→Unity EUN→地理座標の最大誤差は、経度0度、緯度`1.4210854715202004e-14`度だった。

## 生成、検証、決定性

```powershell
npm --prefix viewer ci

node --max-old-space-size=12288 tools/environment-cost-network/build-environment-cost-road-network.mjs `
  --graph data/generated/ichigaya-pedestrian-road-network.json `
  --environment data/generated/ichigaya-venue-environment-cost.json `
  --output data/generated/ichigaya-environment-cost-road-network-v1.json `
  --report data/raw/ichigaya-environment-cost-road-network-integration-report.json `
  --allow-unmatched-as-missing
```

生成前に入力スキーマ、ID一意性、ノード参照、形状端点、全時刻、サンプル集計、値域、日射曝露式を検査する。生成後のオブジェクトも#3のJSON Schemaと意味検証へ通し、成功した場合だけストリーミングで`.partial`へ書いて最終パスへ置換する。不正値、時刻不足、部分的な元辺結合では出力しない。

同じ入力で3回実行し、安定内容フィンガープリント`7ab4a0a584b282193e723aa8c763466d20d3b2cf2734f63290e781e683c816cd`とファイルSHA-256`a224267bc504ab82236f84343e9289abcc3f6da4b11241914265424173381aa4`が一致した。入力配列順を逆転した単体テストでも、エッジ順と内容フィンガープリントは同一だった。

## 市ヶ谷の結果

| 指標 | 結果 |
|---|---:|
| ノード | 108,169 |
| 物理辺 | 130,508 |
| 有向辺 | 260,779 |
| 時刻 | 10 |
| 時刻スライス | 2,607,790 |
| 各時刻の`available` | 184,206 |
| 各時刻の`partial` | 15,066 |
| 各時刻の`missing` | 61,507 |
| 代表生成時間 | 13.17秒 |
| 出力サイズ | 642,332,567 bytes（約612.58 MiB） |

全域JSONは正式契約と経路計算入力の監査成果物としては完全だが、ブラウザへ一括配信するには大きい。Issue #9の対象外である配信用API、時刻・現在地周辺による範囲抽出、圧縮・分割は、E2E性能を扱うIssue #17までに設計する。現時点のViewer統合テストには次の生成fixtureを使用する。

| fixture指標 | 結果 |
|---|---:|
| ノード | 3 |
| 有向辺 | 3 |
| 時刻 | 2 |
| サイズ | 8,854 bytes（約8.65 KiB） |

fixtureは物理重複辺の加重集約、両方向辺、明示的欠測、全時刻、正式契約検証を含み、次で再生成できる。

```powershell
node tools/environment-cost-network/generate-viewer-fixture.mjs
npm --prefix viewer run validate:contract
npm --prefix viewer run test:contract
```

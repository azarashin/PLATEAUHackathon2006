# 環境コスト道路ネットワークの生成

Issue #9では、#5の歩行道路グラフを接続・方向・距離・歩行時間の正本とし、#8のUnity時間別解析結果を`sourceEdgeIds`で結合する。

当初生成した`environment-cost-road-network-1.0`の単一JSONは市ヶ谷で約612.58 MiBになった。これは契約監査には使えるが、ブラウザ配信には使用しない。標準成果物を経路サーバー専用の分割バンドルへ変更し、ブラウザには将来の経路APIが計算した経路形状とKPIだけを返す。

```text
歩行道路グラフ ─┐
                 ├─ #9 分割バンドル ─ 経路サーバー ─ 経路API ─ ブラウザ
Unity時間別結果 ─┘       （非公開）      （#11）      （経路のみ）
```

## 標準成果物

| 種別 | パス | Git管理 |
|---|---|---|
| 歩行道路グラフ | `data/generated/ichigaya-pedestrian-road-network.json` | しない |
| Unity時間別解析 | `data/generated/ichigaya-venue-environment-cost.json` | しない |
| サーバーバンドル | `data/generated/ichigaya-environment-cost-server-bundle-v1/` | しない |
| 生成レポート | `data/raw/ichigaya-environment-cost-server-bundle-report.json` | しない |
| サーバーバンドルfixture | `data/fixtures/environment-cost-server-bundle-v1/` | する |
| 正式契約fixture | `data/fixtures/environment-cost-road-network-integration-v1.json` | する |
| 実データ検証値 | `data/ichigaya-environment-cost-road-network-verification.json` | する |

サーバーバンドルは次のファイルで構成する。

- `manifest.json`: 完了状態、入力フィンガープリント、生成条件、時刻、件数、各ファイルのサイズとSHA-256
- `topology.json`: WGS84ノード、物理辺、接続、方向、距離、歩行時間を1回だけ格納
- `cost-HH.json`: 指定時刻の日陰率、日射曝露時間、サンプル集計、欠測状態を物理辺ごとに1回だけ格納

ノード・辺・コストは、manifestの`encoding`で意味を宣言した位置配列として保存する。方向が異なる有向辺で同じ環境コストを複製せず、10時刻も別ファイルへ分離する。CityGML、3Dメッシュ、Collider、Unityオブジェクト、ローカル入力パスは含めない。

## 結合規則

道路グラフの1物理辺は、OSMで重複している複数の解析元辺を`sourceEdgeIds`に持つ場合がある。時刻ごとに次の規則で1つの値へ集約する。

1. `sampleCount`、`validSampleCount`、`noGroundSampleCount`は対応する解析元辺で合計する。
2. `shadeRatio`は`validSampleCount`による加重平均とする。
3. `solarExposureSeconds`は正式道路グラフの歩行時間を使い、`walkingSeconds * (1 - shadeRatio)`で再計算する。
4. 有効サンプル0は`missing`、有効・欠測混在は`partial`、全サンプル有効は`available`とする。
5. 方向違いの有向辺は同じ物理辺のコストを参照し、接続と形状の向きだけを個別に持つ。

欠測は0ではない。`missing`は日陰率と日射曝露時間を`null`、サンプル数を0として保持する。

## グラフ境界の不一致

市ヶ谷では130,508物理辺のうち130,396辺を解析結果へ完全結合できた。112辺（0.0858%）は、道路グラフが「線分と半径4 km円の交差」で採用する一方、Unity解析が25 m間隔のサンプル点を円内に持たず、解析元辺を出力しない境界差だった。

既定動作はこの112辺を不完全入力として失敗させる。確認済みの市ヶ谷生成では`--allow-unmatched-as-missing`を明示し、道路自体を落とさず、全時刻`missing`で保持する。一部の`sourceEdgeIds`だけが結合できる場合は根拠が曖昧なため常に失敗する。道路グラフに存在しない解析元辺10件は出力せず、件数をmanifestの診断情報へ残す。

## 生成と検証

```powershell
npm --prefix viewer ci

$env:ROUTE_DEPLOY_LOCAL_BUNDLE = 'data/generated/ichigaya-environment-cost-server-bundle-v1'

node --max-old-space-size=8192 tools/environment-cost-network/build-environment-cost-server-bundle.mjs `
  --graph data/generated/ichigaya-pedestrian-road-network.json `
  --environment data/generated/ichigaya-venue-environment-cost.json `
  --bundle-directory $env:ROUTE_DEPLOY_LOCAL_BUNDLE `
  --report data/raw/ichigaya-environment-cost-server-bundle-report.json `
  --allow-unmatched-as-missing

node --max-old-space-size=4096 viewer/scripts/validate-environment-cost-server-bundle.mjs `
  "$env:ROUTE_DEPLOY_LOCAL_BUNDLE/manifest.json"
```

`ROUTE_DEPLOY_LOCAL_BUNDLE`を生成時の`--bundle-directory`と配信時の転送元に共用する。
`deploy/route-bundle-upload.env`の値は生成コマンドへ自動適用されないため、生成と配信を行うPowerShellでも
上記のように明示的に設定する。

生成時に入力スキーマ、ID一意性、ノード参照、全時刻、サンプル集計、値域、日射曝露式を検査する。各ファイルは一時ファイルから置換し、全ファイルの書込み後に`status: completed`のmanifestを最後に置く。サーバーローダーはパス逸脱、サイズ、SHA-256、内容フィンガープリント、参照、値域を再検証し、不完全・改変済みバンドルを公開しない。

ローダーは全時刻または必要時刻だけを型付き配列へ読み込める。

```javascript
import { loadEnvironmentCostServerBundle } from './tools/environment-cost-network/load-environment-cost-server-bundle.mjs'

const runtime = await loadEnvironmentCostServerBundle(
  'data/generated/ichigaya-environment-cost-server-bundle-v1/manifest.json',
  { timestamps: ['2025-08-01T12:00:00+09:00'] },
)
```

運用では生成・配置完了後に経路サーバーを再起動し、起動時検証に成功したバンドルだけを使用する。開始位置・終了位置の道路スナップ、最短経路計算、経路レスポンスAPIはIssue #11で実装する。

## 市ヶ谷の結果

| 指標 | 結果 |
|---|---:|
| ノード | 108,169 |
| 物理辺 | 130,508 |
| 有向辺 | 260,779 |
| 時刻 | 10 |
| `topology.json` | 25,548,565 bytes |
| 10個のコストファイル合計 | 31,358,040 bytes |
| manifest込みバンドル合計 | 56,914,693 bytes（約54.28 MiB） |
| 旧単一JSON比 | 8.86%（91.14%削減） |
| 代表生成時間 | 6.66秒 |
| 全10時刻の代表読込時間 | 1.59秒 |
| 1時刻の代表読込時間 | 0.73秒 |

同じ入力から2回生成し、全12ファイルのSHA-256とバンドルフィンガープリント`a5701a2fe10952c62f58a22fe25258b8939f45eb22419a5667e31016e90b752d`が一致した。これら約54.28 MiBはサーバー配置量であり、ブラウザの初回ダウンロード量ではない。

## 正式契約の監査出力

Issue #3の`environment-cost-road-network-1.0`を直接検査する必要がある場合だけ、従来の完全JSONを生成する。

```powershell
node --max-old-space-size=12288 tools/environment-cost-network/build-environment-cost-road-network.mjs `
  --graph data/generated/ichigaya-pedestrian-road-network.json `
  --environment data/generated/ichigaya-venue-environment-cost.json `
  --output data/generated/ichigaya-environment-cost-road-network-v1.json `
  --report data/raw/ichigaya-environment-cost-road-network-integration-report.json `
  --allow-unmatched-as-missing
```

この642,332,567 bytesの監査出力はブラウザにも経路APIにも配信しない。正式契約の小型fixtureはViewerの契約検証へ常時通す。

## 座標変換とfixtureの検証

`japan-plane-rectangular.mjs`はGRS80/JGD2011、縮尺係数0.9999の平面直角座標系を実装する。第IX系について国土地理院の既知点と比較し、市ヶ谷解析中心と東京駅付近の両点で差が1 mm未満であることを確認した。実道路ノード3点の地理座標→Unity EUN→地理座標の最大誤差は、経度0度、緯度`1.4210854715202004e-14`度だった。

```powershell
node tools/environment-cost-network/test-japan-plane-rectangular.mjs
node tools/environment-cost-network/test-build-environment-cost-road-network.mjs
node tools/environment-cost-network/test-environment-cost-server-bundle.mjs
node tools/environment-cost-network/generate-viewer-fixture.mjs
node tools/environment-cost-network/generate-server-bundle-fixture.mjs
npm --prefix viewer run validate:contract
npm --prefix viewer run validate:server-bundle
npm --prefix viewer run test:contract
```

サーバーバンドルfixtureは3ノード、2物理辺、3有向辺、2時刻、4,742 bytesで、物理重複辺の加重集約、両方向辺、明示的欠測、改変検知を検証する。

# 環境コスト道路ネットワークデータ契約 v1

Issue #3 フェーズBで、Unity解析結果とブラウザ版 Viewer の境界を
`environment-cost-road-network-1.0` として固定します。Viewer は CityGML や Unity アセットを
直接参照せず、この契約を満たす JSON だけを読み込みます。

正式な機械可読定義は
[`schemas/environment-cost-road-network-v1.schema.json`](../schemas/environment-cost-road-network-v1.schema.json)、
最小の正常例は
[`data/fixtures/environment-cost-road-network-v1.json`](../data/fixtures/environment-cost-road-network-v1.json)
です。

## ルート構造

| フィールド | 役割 |
| --- | --- |
| `schemaVersion` | 契約の互換性識別子。v1 は `environment-cost-road-network-1.0` 固定 |
| `dataset` | データセットID、由来、生成日時、利用上の注意 |
| `area` | 地域ID、中心点、対象半径、WGS 84 の bbox |
| `coordinateReferenceSystem` | 配信用座標系と Unity 解析座標系 |
| `scenario` | 基準日、タイムゾーン、利用可能時刻、既定時刻、時刻選択規則 |
| `costDefinitions` | コストごとの単位、値域、向き、経路集計、欠測、表示定義 |
| `nodes` / `edges` | 道路グラフのノードと有向辺 |
| `extensions` | v1利用者が無視できる追加メタデータ |

## IDと道路グラフ

- データセット内で `costDefinitions[].id`、`nodes[].id`、`edges[].id` はそれぞれ一意にします。
- `edges[].fromNodeId` と `toNodeId` は同じファイルの `nodes[].id` を参照します。
- `edges[].geometry` の始点・終点は、参照ノードの座標と一致させます。
- `sourceEdgeIds` は OSM・解析側の元辺との結合キーです。物理重複を統合した辺では複数指定できます。
- IDは再生成しても同じ入力要素に同じ値を割り当てます。表示順や配列添字をIDにしません。

## 座標系と単位

- `nodes[].coordinate`、`edges[].geometry`、`area.center`、`area.bbox` は EPSG:4326、軸順は
  `[longitude, latitude]` です。
- Unity解析の平面座標系は `coordinateReferenceSystem.unity` に EPSG、平面直角座標系番号、
  軸規約、地理座標の基準点を明記します。fixture は EPSG:6677（第9系）、EUN を使用します。
- 距離は `lengthMeters`（m）、歩行時間は `walkingSeconds`（s）です。
- コスト値の単位と有効範囲は `costDefinitions` に定義し、表示用変換だけを
  `presentation.displayScale` と `displayUnit` で行います。例えば `shadeRatio=0.75` は保存値を
  変えず、Viewer では `75%` と表示します。

## 時刻スライス

- 各辺は `scenario.availableTimestamps` の全時刻を、重複なく1件ずつ持ちます。
- タイムスタンプはタイムゾーンオフセット付き ISO 8601 とし、基準日と一致させます。
- 指定時刻がない場合は同じ基準日の最も近い時刻を選び、同距離なら早い時刻を選びます
  （`nearest-on-reference-date-ties-earlier`）。基準日外へ補間・外挿しません。
- 初期表示は `defaultTimestamp` を使います。

## 欠測値

`timeSlices[].status` と全コストの値を組み合わせ、次のように扱います。

| `status` | 規則 |
| --- | --- |
| `available` | 全コストが数値で、全サンプルが有効 |
| `partial` | 一部サンプルが無効だが、少なくとも1つのコストを算出可能 |
| `missing` | 全コストが `null`、有効サンプル数は0 |

`null` は「コスト0」ではありません。Viewer は欠測として灰色表示し、経路計算側も0へ変換せず、
経路除外・基礎移動コストのみ・利用者への警告などの方針を明示して扱います。

## 経路集計と丸め

各コストの `routeAggregation` に従います。

- `shadeRatio`: 有効値を持つ辺について `walkingSeconds` 加重平均
- `solarExposureSeconds`: 経路上の合計
- `inlandFloodDepthMeters`: 経路上の最大値

日射を考慮する経路重みの例は
`walkingSeconds + solarPenaltyCoefficient * solarExposureSeconds` です。
係数は探索条件であり、測定値である `shadeRatio` や `solarExposureSeconds` へ混ぜて保存しません。

v1の `solarExposureSeconds` は辺単位の合計値であり、辺内サンプルの通過順を保持しません。そのため、
複数辺にまたがる連続日向時間は厳密には算出できず、各辺の曝露時間合計を上限寄りの近似として扱います。
連続日向時間を経路評価へ使う場合は、順序付き微小区間または日向区間の始終端を将来の追加データとして定義します。

保存値と経路計算では丸めません。UI表示時だけ、距離は1 m、時間は1秒、日陰率は1ポイント、
浸水深は0.01 mを目安に丸めます。

## 将来のコスト追加と互換性

新しい環境コストは `costDefinitions` と各時刻の `values` に同じIDを追加します。
既存フィールドの意味・単位・値域は変更せず、既存利用者が無視できる追加はv1内の拡張として扱えます。
Viewerに直接表示する場合だけ `presentation.viewerMode=true` と色定義を指定します。

フィールド削除、必須化、型変更、座標・時刻・欠測規則の変更など、既存利用者が安全に無視できない変更は
`schemaVersion` を更新し、新しいスキーマを用意します。

## 検証

Node.js 22 と npm 11 を使用します。

```bash
cd viewer
npm ci
npm run validate:contract
npm run test:contract
npm run build
```

`validate:contract` は JSON Schema と参照・一意性・値域・時刻・欠測の意味制約を検証します。
`test:contract` は正常fixtureと、ID重複、参照切れ、範囲外、不正な日時形式、未登録時刻、欠測の0埋めを含む
異常fixture群が期待どおり拒否されることを確認します。

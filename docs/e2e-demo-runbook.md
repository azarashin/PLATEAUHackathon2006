# 市ヶ谷 E2E デモ運用手順

## Unity inspection Scene（再現可能なローカル準備）

解析バッチがCityGMLを読み込むのは一時的であり、Git管理する`.unity`シーンは残しません。デモの前に`tools/plateau-environment-cost-analyzer/`をUnityで開き、次の手順で確認用のローカルSceneを作成します。

1. **PLATEAU > Environment Cost > Create Inspection Scene** を開く。
2. `data/analysis-configs/ichigaya-venue.json`（またはデモ対象地域の設定ファイル）を選び、**Create inspection Scene** を実行する。現在のSceneに未保存変更があれば、Unityの保存確認で保存またはキャンセルする。未保存の空Sceneを手動で作る必要はない。
3. `ENVIRONMENT_COST_INSPECTION_SCENE_READY` を待つ。`buildingColliders` と `roadColliders` はともに0より大きくなければならない。このコマンドは`bldg`と`tran`のLOD1だけを読み込み、MeshColliderを追加し、`Building=8`と`Road=9`を検証する。
4. **PLATEAU > Environment Cost > Hourly Heatmap** を開き、完了済み環境コストJSONを読み込む。`12:00`と道路辺を選択する。Sceneビューでは緑が日陰、橙が日向、赤が道路面を取得できなかったサンプル、紫の矢印が太陽方向を示す。

生成先は`Assets/Scenes/EnvironmentCostInspection.unity`で、Git管理外です。**Request cancellation**は実行中のCityGMLデータセットの読込み完了後に停止し、未保存の部分Sceneを閉じます。失敗時は、選択した設定ファイル・coverage report・ローカルCityGMLパス・`ProjectSettings/TagManager.asset`の`Building`/`Road`レイヤーを確認します。Collider数は確認用入力が存在することを示すだけで、すべてのCityGMLメッシュが完全であることまでは保証しません。

Issue #17 の受け入れ確認用に、Unity 解析済みの市ヶ谷データを、経路 API と Viewer で実演するための手順を定義する。

## 前提と注意事項

- CityGML は Unity での解析・根拠確認にだけ使用する。発表時の Viewer は CityGML、Unity アセット、全時刻のコスト JSON をブラウザへ配信しない。
- 日陰率は、指定時刻に道路サンプルが建物で遮られる割合を示す指標であり、体感温度を推定しない。また、安全を保証するナビゲーションではない。
- #15 と #16 の比較施策は未実装であり、本デモの比較対象には含めない。

## 固定デモ条件

Viewer の **「市ヶ谷デモ条件を設定」** を押す。次の値が設定され、直ちに 3 経路の計算を要求する。

| 項目 | 値 |
|---|---|
| 地域 | `ichigaya-venue` |
| 時刻 | `2025-08-01T12:00:00+09:00` |
| 起点（経度, 緯度） | `139.736043, 35.690470` |
| 終点（経度, 緯度） | `139.700556, 35.689606` |
| 日陰回避係数 | `2` |

この条件は `server/scripts/verify-ichigaya-route.mjs` と `viewer/src/demo-route.ts` で管理する。どちらかを変更した場合は、両方と本書を同じ変更で更新する。

## 事前確認

### 1. バンドルと API を検証する

実バンドルをローカルへ配置した上で、次を実行する。バンドル本体は Git 管理しない。

```powershell
npm --prefix server run verify:ichigaya -- `
  --manifest data/generated/localHackathon2026Summer/manifest.json `
  --iterations 7
```

2026-08-24 に fingerprint `5850c4d5b8e0e7f8b3a8b0b5f3fda161873928aa0558eda871ec748382055771` で確認した基準値は次のとおりである。解析データを作り直した場合は、出力された fingerprint と KPI を採用し、本表を更新する。

| 経路 | 距離 | 歩行時間 | 日向時間 |
|---|---:|---:|---:|
| 最短 | 3,631 m | 2,594 秒 | 1,726 秒 |
| バランス | 3,777 m | 2,698 秒 | 780 秒 |
| 日陰優先 | 3,791 m | 2,708 秒 | 772 秒 |

検証では、3 経路の距離が 3〜5 km、同じ入力に対する結果が決定的であること、バンドル内部の topology や cost が API 応答に漏れないことも確認する。

### 2. ローカル実演時の起動

別々のターミナルで、まず経路 API、次に Viewer を起動する。`ROUTE_BUNDLE_MANIFESTS` は手元の実バンドルの `manifest.json` に合わせる。

```powershell
$env:HOST = '127.0.0.1'
$env:PORT = '3102'
$env:ROUTE_BUNDLE_MANIFESTS = (Resolve-Path 'data/generated/localHackathon2026Summer/manifest.json')
$env:ROUTE_TIMESTAMPS = '2025-08-01T12:00:00+09:00'
$env:ROUTE_CORS_ORIGIN = 'http://127.0.0.1:5174'
npm --prefix server run start
```

```powershell
$env:VITE_ROUTE_API_URL = 'http://127.0.0.1:3102/api/v1/routes'
$env:VITE_ROAD_EDGE_API_URL = 'http://127.0.0.1:3102/api/v1/road-edges'
npm --prefix viewer run dev -- --host 127.0.0.1 --port 5174 --strictPort
```

`http://127.0.0.1:3102/healthz` が 200 を返すことを確認してから、Viewer を開く。公開環境のサービス更新・Nginx 設定は [server-operation-runbook.md](server-operation-runbook.md) を使う。

## 発表手順（約 2 分）

1. Viewer を開き、常時表示される注意書き（「体感温度ではない」「安全を保証しない」）を説明する。
2. **「市ヶ谷デモ条件を設定」** を押す。
3. 「3経路を計算・描画しました」の状態、最短・バランス・日陰優先の 3 カード、上表と整合する KPI を確認する。
4. 地図をズーム 14.5 以上にして道路の環境コスト色を表示する。道路をクリックし、時刻・日陰率・欠測の有無を確認する。
5. 日陰優先経路が、最短経路より少ない日向時間を選ぶトレードオフを説明する。

Viewer は `VIEWER_PERFORMANCE` をブラウザコンソールへ記録する。実演前に DevTools の Console で次を 5 回ずつ採取し、中央値を発表メモへ転記する。

- `fixture-loaded` と `map-style-ready`：初期表示の構成要素
- `initial-render`：アプリ開始から初期地図描画まで
- `route-to-render`：経路要求開始から 3 経路・KPI 描画まで
- `road-edges-to-render`：道路辺要求開始から道路レイヤー更新まで

開発端末・localhost（Viewer 5174、API 3102、キャッシュを温めた状態）で 2026-08-24 に取得した 1 回の実測は、初期描画 181.4 ms、固定条件の経路描画 105.0 ms、道路辺描画 64.4 ms（続く同一範囲の再描画 19.5 ms）である。端末や背景地図の状態に依存するため、これは上限保証ではなく記録値である。

同じ市ヶ谷バンドルで、Unity 解析は初回 608.95 秒／キャッシュ利用 190.60 秒、解析 JSON は 296,190,573 bytes、バンドルの 1 時刻読込は 734.160 ms、3 経路比較 p50/p95 は 80.433/100.573 ms、HTTP 往復は 120.228 ms、経路応答は 34,838 bytes だった。道路辺 API の通信量は [viewer-location-and-route-controls.md](viewer-location-and-route-controls.md) に記録する表示範囲依存値を用いる。

## Unity での根拠確認

1. `tools/plateau-environment-cost-analyzer/` を Unity Editor で開く。既存のCityGML読込済みSceneは不要である。
2. **PLATEAU > Environment Cost > Create Inspection Scene** を開き、解析に使用した `data/analysis-configs/<areaId>.json`（市ヶ谷では `ichigaya-venue.json`）を選び、`Create inspection Scene` を実行する。
3. `ENVIRONMENT_COST_INSPECTION_SCENE_READY` ログで Building と Road のCollider件数がともに0より大きいことを確認する。生成先は `Assets/Scenes/EnvironmentCostInspection.unity` であり、CityGMLに由来するローカル生成物のためGit管理しない。
4. Unityを再起動しても同Sceneを開けることを確認し、**PLATEAU > Environment Cost > Hourly Heatmap** を開いて解析済みJSONを `Load` する。
5. 12:00を選び、道路辺を1本選ぶ。太陽方向矢印・方位・高度、緑（日陰）・橙（日向）・赤（道路面未照合）のサンプルがSceneビューに描画され、全件が道路面未照合ではないことを確認する。
6. 表示される `sampleCount`、`validSampleCount`、`noGroundSampleCount`、日陰率が、該当時刻の出力 JSON の値と矛盾しないことを確認する。Collider件数だけではCityGML全メッシュの完全性は保証しないため、必要に応じて複数の道路辺を確認する。

全道路・全サンプルを一度に描画しない。選択道路だけを再判定・描画し、発表中の Editor 負荷を抑える。

## 異常時の判断と復旧

| 症状または API コード | 画面上の案内・対応 |
|---|---|
| `SNAP_NOT_FOUND` | 道路の近くを起点・終点として選び直す。 |
| `OUTSIDE_COVERAGE` | 計算済み範囲内の地点を選び直す。 |
| `TIMESTAMP_NOT_AVAILABLE` | 計算済みの日時（市ヶ谷は 2025-08-01 12:00 JST）へ戻す。 |
| `ROUTE_NOT_FOUND` | 歩行可能な接続がある地点へ選び直す。 |
| API 通信失敗、`/healthz` が失敗 | `ROUTE_BUNDLE_MANIFESTS`、バンドル配置、経路サービスを確認し、サービスを再起動する。 |
| 道路色が出ない | ズーム 14.5 以上へ拡大する。API の `road-edges` location が経路 API より先に Nginx へ設定されているか確認する。 |
| 背景地図が失敗 | 経路と道路コストの描画は継続可能。背景地図は復旧後に再読み込みする。 |

公開サーバーに障害がある場合は、上記「ローカル実演時の起動」で、検証済みの実バンドルを使うローカル API と Viewer へ切り替える。実バンドルが利用できない場合、架空の fixture を実解析結果として表示してはならない。発表前に固定デモ条件で 60〜90 秒のバックアップ動画を録画し、動画であることを明示して再生する。動画には、固定条件、3 経路・KPI、注意書き、道路コストの順で収録する。

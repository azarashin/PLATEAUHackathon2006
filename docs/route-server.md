# 経路サーバーAPI

Issue #11では、道路ネットワークと全時刻コストをブラウザへ配信せず、サーバー側で道路スナップと経路探索を行う。ブラウザは利用者が指定した起終点、地域、時刻、係数を送り、経路GeoJSONとKPIだけを受け取る。

## コストとプロファイル

辺の探索コストは秒単位で統一する。

```text
routeCostSeconds = walkingSeconds
                 + solarExposureSeconds * solarAvoidanceFactor
```

| プロファイル | 係数 | 意味 |
|---|---:|---|
| `shortest` | 0 | 歩行時間だけを最小化 |
| `balanced` | 0.5 | 歩行時間と日射曝露を両方考慮 |
| `shade` | 2 | 日射曝露1秒を追加歩行2秒相当として回避 |

リクエストで1〜5個の任意プロファイルと0〜100の連続係数を指定できる。未指定時は上記3件を返す。すべての辺重みは非負で、Dijkstra法を使用する。同一コストでは安定したノード・エッジ順により決定論的に選ぶ。

## 欠測方針

`available`と`partial`は解析済みの日射曝露時間を使う。`missing`は安全・日陰とみなさず、探索時だけ「全区間が日向」と保守的に仮定する。

レスポンスは次を区別する。

- `solarExposureSeconds`: 欠測を全日向と仮定した探索・比較用の値
- `observedSolarExposureSeconds`: 解析値がある辺だけの実測値
- `unknownWalkingSeconds`: 欠測辺を歩く時間
- `coverageStatus`、`missingEdgeCount`、`partialEdgeCount`: データ充足状態
- `observedShadeRatio`: 解析値がある区間だけの日陰率。全区間欠測なら`null`

したがって欠測値を観測済みの日陰率0として表示してはならない。
利用者向け画面では`unknownWalkingSeconds`というAPIキーをそのまま表示せず、レスポンスの`presentation.kpiLabels.unknownWalkingSeconds`にある「不明な歩行時間」を使用する。

## API

### `POST /api/v1/routes`

リクエスト例:

```json
{
  "areaId": "route-server-fixture",
  "timestamp": "2025-08-01T12:00:00+09:00",
  "start": [139.735, 35.69],
  "end": [139.736, 35.69],
  "profiles": [
    { "id": "shortest", "solarAvoidanceFactor": 0 },
    { "id": "balanced", "solarAvoidanceFactor": 0.5 },
    { "id": "shade", "solarAvoidanceFactor": 2 }
  ]
}
```

成功時は`route-response-1.0`として次を返す。

- 入力座標、スナップ座標、距離、ノードID
- 各プロファイルの有向エッジID列とGeoJSON `LineString`
- 距離、歩行時間、日射曝露時間、日陰率、欠測時間、探索コスト
- スナップ・探索時間と入力バンドルのフィンガープリント
- HTTPごとのUUID `requestId`

正式な構造は[`route-request-v1.schema.json`](../schemas/route-request-v1.schema.json)と[`route-response-v1.schema.json`](../schemas/route-response-v1.schema.json)で定義する。レスポンスにtopology、全ノード、全辺、全時刻コストは含めない。

### `GET /api/v1/road-edges`

Issue #27の日陰可視化では、全道路ネットワークを配信せず、現在の地図表示範囲に交差する物理道路辺だけを取得する。

```http
GET /api/v1/road-edges?areaId=ichigaya-venue&timestamp=2025-08-01T12%3A00%3A00%2B09%3A00&bbox=139.73,35.68,139.74,35.70&solarAvoidanceFactor=2
```

`bbox`は`minLongitude,minLatitude,maxLongitude,maxLatitude`の順で指定する。応答は`road-edge-response-1.0`のGeoJSON `FeatureCollection`で、各物理道路辺について次を返す。

- 道路辺ID、2点の`LineString`、長さ、歩行時間
- 解析状態、日陰率、日射曝露時間、解析点数、道路面未照合点数
- 日射回避係数、探索に用いた日射曝露時間、環境コスト加算分、最終探索コスト
- 欠測理由と、全日向仮定を適用したかどうか

`available`と`partial`では#9の解析値を使用する。`missing`では`shadeRatio`と`solarExposureSeconds`を`null`のまま返し、探索用に限り歩行時間と同じ秒数を全日向として仮定する。したがって、欠測辺も次式の最終コストを説明できるが、解析済みの日向または日陰とは表示しない。

```text
environmentalCostSeconds = assumedSolarExposureSeconds * solarAvoidanceFactor
routeCostSeconds = walkingSeconds + environmentalCostSeconds
```

サーバーは起動時に物理道路辺の空間グリッド索引を構築する。1応答は既定10,000辺まで、bboxの緯度・経度幅は各0.2度までとし、超過時は地図を拡大するようHTTP 422を返す。上限辺数は`ROUTE_MAXIMUM_ROAD_EDGE_FEATURES`で変更できる。正式な応答構造は[`road-edge-response-v1.schema.json`](../schemas/road-edge-response-v1.schema.json)で定義する。

### エラー

| HTTP | code | 条件 |
|---:|---|---|
| 400 | `INVALID_JSON`、`INVALID_REQUEST`、`INVALID_COORDINATE`、`INVALID_BBOX`、`INVALID_PROFILE` | 構文・型・値が不正 |
| 404 | `AREA_NOT_FOUND`、`NOT_FOUND` | 地域またはエンドポイントがない |
| 405 | `METHOD_NOT_ALLOWED` | APIをPOST以外で呼んだ |
| 413 | `REQUEST_TOO_LARGE` | 本文が上限を超えた |
| 422 | `OUTSIDE_COVERAGE`、`SNAP_NOT_FOUND`、`TIMESTAMP_NOT_AVAILABLE`、`ROUTE_NOT_FOUND`、`BBOX_TOO_LARGE`、`TOO_MANY_ROAD_EDGES` | 正常な構文だが探索・道路辺取得不能 |
| 500 | `INTERNAL_ERROR` | 予期しない内部失敗 |

エラーには`requestId`と機械判別用`code`を含め、スタック、ローカルパス、入力バンドルの内容は返さない。

## 起動時ロード

`ROUTE_BUNDLE_MANIFESTS`に1件以上のmanifestを指定する。manifest、topology、cost sliceのサイズ・SHA-256・内容フィンガープリント・参照・値域を検証できた場合だけlistenを開始する。ファイルはリクエストごとに再読込しない。

`ROUTE_TIMESTAMPS`を指定すると必要時刻だけ、未指定なら全時刻を型付き配列へ読み込む。複数地域はmanifestパスをカンマ区切りで指定する。同じ`areaId`の重複は起動失敗とする。

本番ではViewerと別のsystemdサービス・環境変数ファイルで起動する。`PORT`は既定値3000から変更でき、
Nginxの`proxy_pass`と一致させる。環境ファイル、systemdユニット、同一サブパスでの公開例は
[Viewer サーバー環境構築](server-deployment.md#経路apiを同一サブパスで公開する)を参照する。

## Fixture

`data/fixtures/route-server-bundle-v1/`は3つの候補経路と到達不能な孤立ノードを持つ。

| 経路 | 歩行時間 | 日射曝露時間 | 選択される係数 |
|---|---:|---:|---:|
| 最短 | 200秒 | 180秒 | 0 |
| バランス | 230秒 | 115秒 | 0.5 |
| 日陰 | 300秒 | 15秒 | 2 |

テストでは係数増加に伴う日射曝露の単調非増加、逆方向、スナップ、未登録時刻、範囲外、到達不能、HTTP異常系、レスポンスサイズを確認する。

## Viewerとの境界

- #11: バンドル読込、スナップ、経路探索、KPI、HTTP API
- #12: 地域・GPS・地図クリック・日時指定とAPI呼出し
- #13: 返却された3経路とKPIの描画

GPSはMVPでは表示位置合わせだけに使用し、利用者が地図上で明示指定した起終点だけを経路APIへ送る。道路辺APIには地図の表示範囲、地域、選択日時、係数だけを送る。

## 市ヶ谷実データ検証

次のコマンドは市ヶ谷の12:00コストだけを読み込み、市ヶ谷中心付近から新宿駅付近までの3経路を複数回計算する。最短経路が3〜5 kmであること、係数0、日射曝露の単調性、エッジ列の決定性、HTTPレスポンスが全バンドルを含まないことを検証する。

```powershell
npm --prefix server run verify:ichigaya -- `
  --manifest data/generated/ichigaya-environment-cost-server-bundle-v1/manifest.json `
  --report data/raw/ichigaya-route-server-verification.json
```

追跡対象の代表値は`data/ichigaya-route-server-verification.json`へ記録する。実行ごとの詳細レポートと大規模バンドルはGit管理しない。

2026-08-23の代表実行結果は次のとおり。

| 指標 | 結果 |
|---|---:|
| 1時刻の起動読込 | 764.29 ms |
| 3経路比較 p50 / p95（7回） | 98.69 / 116.21 ms |
| HTTP往復 | 137.32 ms |
| HTTPレスポンス | 34,743 bytes |
| 起動読込後RSS増加 | 127,635,456 bytes |
| 最短経路 | 3,631 m、2,594秒、日射曝露1,726秒 |
| バランス経路 | 3,777 m、2,698秒、日射曝露780秒 |
| 日陰優先経路 | 3,791 m、2,708秒、日射曝露772秒 |

3経路とも一部に欠測辺がある。探索では欠測区間を全日向として扱い、「不明な歩行時間」を最短87.8秒、バランス37.9秒、日陰優先36.6秒として返した。UIは日陰率だけでなく、この不明区間を日本語表記で必ず併記する必要がある。

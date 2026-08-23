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

### エラー

| HTTP | code | 条件 |
|---:|---|---|
| 400 | `INVALID_JSON`、`INVALID_REQUEST`、`INVALID_COORDINATE`、`INVALID_PROFILE` | 構文・型・値が不正 |
| 404 | `AREA_NOT_FOUND`、`NOT_FOUND` | 地域またはエンドポイントがない |
| 405 | `METHOD_NOT_ALLOWED` | APIをPOST以外で呼んだ |
| 413 | `REQUEST_TOO_LARGE` | 本文が上限を超えた |
| 422 | `OUTSIDE_COVERAGE`、`SNAP_NOT_FOUND`、`TIMESTAMP_NOT_AVAILABLE`、`ROUTE_NOT_FOUND` | 正常な構文だが探索不能 |
| 500 | `INTERNAL_ERROR` | 予期しない内部失敗 |

エラーには`requestId`と機械判別用`code`を含め、スタック、ローカルパス、入力バンドルの内容は返さない。

## 起動時ロード

`ROUTE_BUNDLE_MANIFESTS`に1件以上のmanifestを指定する。manifest、topology、cost sliceのサイズ・SHA-256・内容フィンガープリント・参照・値域を検証できた場合だけlistenを開始する。ファイルはリクエストごとに再読込しない。

`ROUTE_TIMESTAMPS`を指定すると必要時刻だけ、未指定なら全時刻を型付き配列へ読み込む。複数地域はmanifestパスをカンマ区切りで指定する。同じ`areaId`の重複は起動失敗とする。

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

GPSはMVPでは表示位置合わせだけに使用し、利用者が地図上で明示指定した起終点だけをAPIへ送る。

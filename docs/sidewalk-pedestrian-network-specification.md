# 歩道を意識する歩行者ネットワーク仕様（v2）

Issue #71 は、道路中心線を使う既存の歩行者ネットワーク（v1）から、歩道・横断・立体交差を区別するネットワーク（v2）へ移行するための仕様を定める。実装は #72、日陰解析・bundle・地域検証への統合は #73 の責務とする。

## 1. v2 を新設し、v1 を並存させる理由

v1（`environment-cost-road-network-1.0`）は OSM の `highway=*` way の中心線を歩行可能な辺として扱う。`highway=footway` 等が明示されている箇所は歩道形状になるが、車道にある `sidewalk=left/right/both`、横断可能地点、橋・地下道の高さ関係は解釈しない。そのため、車道中心を通る経路が生じる。

v2 は歩道の線形・接続・品質根拠を追加するが、既存の Unity 時間別コスト、server bundle、Viewer は v1 を消費している。v1 のファイル、スキーマ、ID、bundle を変更・上書きせず、`environment-cost-pedestrian-network-2.0` を別成果物として発行する。これにより既存地域の再現性を維持し、v2 の入力品質が不足する地域は安全に v1 へロールバックできる。

## 2. 入力ソースと優先順位

生成器は同じ地物について次の高い順位の根拠を採用し、採用元を edge ごとに記録する。

1. CityGML の交通・歩道・横断・橋梁・地下道の明示形状（読込可能で座標系が確定したもの）
2. OSM の独立歩道・歩行者専用線形（`footway`、`path`、`pedestrian`、`steps`、`crossing`）
3. OSM の車道 way に付く `sidewalk=left/right/both/separate`、`foot=*`、`crossing=*`、`bridge`、`tunnel`、`layer`、`level` タグ
4. 管理された `road-network-overrides.geojson` の地域別補正
5. v1 中心線を明示したフォールバック（歩道としては表示しない）

現在の snapshot は `way` とその geometry を主に収録するだけで、独立した OSM node 要素のタグ数は **0** である。横断点・信号・段差等の node タグを根拠にできないため、#72 は `capture contract v0.2` を導入し、必要な node と node タグ、way タグ、relation の取得条件を固定する。既存の `out body geom` snapshot は v1 用として保持する。

## 3. v2 データモデル（設計契約）

```json
{
  "schemaVersion": "environment-cost-pedestrian-network-2.0",
  "coordinateReferenceSystem": { "geographic": "EPSG:4326", "projected": "EPSG:6677" },
  "nodes": [{
    "id": "ped:osm-node:123:left",
    "coordinate": [139.0, 35.0],
    "zLevel": 0,
    "kind": "sidewalk-junction",
    "source": { "kind": "osm-node", "id": "123", "confidence": "explicit" }
  }],
  "edges": [{
    "id": "ped:way:456:left:0",
    "fromNodeId": "ped:osm-node:123:left",
    "toNodeId": "ped:osm-node:124:left",
    "geometry": [[139.0, 35.0], [139.0001, 35.0001]],
    "lengthMeters": 12.3,
    "walkability": "walkable",
    "facility": "sidewalk",
    "side": "left",
    "level": 0,
    "crossing": null,
    "source": { "kind": "osm-way", "id": "456", "rule": "sidewalk=left", "confidence": "derived" },
    "fallback": false
  }]
}
```

必須項目は `id`、接続 node ID、EPSG:4326 の `[longitude, latitude]` geometry、`lengthMeters`、`walkability`、`facility`、`level`、`source`、`fallback` とする。`source.kind` は `citygml` / `osm-way` / `osm-node` / `override` / `v1-centerline`、`confidence` は `explicit` / `derived` / `fallback` を取る。v2 edge ID はソース ID・side・区間番号から決定的に生成し、入力 snapshot と override の SHA-256 を manifest に残す。

## 4. CRS とオフセット規則

- 外部・bundle の座標は EPSG:4326、順序は常に `[経度, 緯度]` とする。
- 左右歩道のオフセット計算は地域の平面直角座標系で行い、地域設定の `unity` CRS と同じ EPSG を使用する（京都・舞鶴: EPSG:6674、藤沢・さいたま・市ヶ谷: EPSG:6677）。計算後は EPSG:4326 に戻す。
- `left` / `right` は元の OSM way の進行方向に対する左右であり、双方向 edge を生成しても side の意味を反転しない。
- 明示幅がある場合は歩道中心へ、ない場合は既定オフセット 2.0 m を用いる。幅や道路幅から安全に算出できない場合は `derived` として採用せず、フォールバック候補へ送る。
- 端点は交差点境界で切り、異なる `level` を接続しない。橋・地下道は `bridge/tunnel/layer/level` が互換な場合だけ接続する。

## 5. 歩行可否と生成規則

| 入力・条件 | v2 の扱い | 品質・備考 |
|---|---|---|
| `foot=no`、高速道路、歩行禁止 | 辺を生成しない | `walkability=forbidden` を品質報告へ記録 |
| 独立 `footway/path/pedestrian/steps` | 元形状を歩行辺として生成 | `facility=footway/path/pedestrian/steps`、最優先の OSM 根拠 |
| `sidewalk=left/right/both` | 指定側ごとに左右歩道を生成 | 平面座標系でオフセット、`facility=sidewalk` |
| `sidewalk=separate` | 独立歩道 way を探索して接続 | 対応を確定できなければ車道からは生成しない |
| `crossing=*`、横断歩道 way/node | 互換 level の左右歩道を横断 edge で接続 | `facility=crossing`。node タグは capture contract v0.2 が前提 |
| 交差点 | 同一 level・合理的な近接距離だけ接続 | 車道を自由横断する完全グラフは禁止 |
| `bridge/tunnel/layer/level` が相違 | 接続しない | 誤った立体交差を防ぐ |
| 歩道根拠なし | v1 中心線を `fallback=true` で出力可 | 経路 UI では「中心線フォールバック」と明示 |

## 6. フォールバックの表示と利用制限

フォールバック edge は経路探索から除外しないが、通常の歩道 edge より低い信頼度として扱う。Viewer / Runtime は経路に `fallback=true` が含まれる場合、区間を破線または警告色にし、「歩道情報不足のため道路中心線を代用」と表示する。品質が `blocked` の地域では v2 を発行せず v1 を維持する。日陰率・環境コストは edge の geometry に対して計算するため、中心線フォールバックの結果を歩道上の精密な評価として扱ってはならない。

## 7. 5 地域の入力品質基準

| 地域 | 対象 | CRS | snapshot 現状 | v2 発行の最低条件 | 初期判定 |
|---|---|---|---|---|---|
| 市ヶ谷 | 市ヶ谷会場 | EPSG:6677 | way 中心線、node タグ 0 | capture contract v0.2、独立歩道又は `sidewalk` 根拠 80%以上、代表経路の横断確認 | 要再取得 |
| 京都 | 京都駅 | EPSG:6674 | way 中心線、node タグ 0 | 同上。徒歩主要動線 3本を目視照合 | 要再取得 |
| 舞鶴 | 東舞鶴駅 | EPSG:6674 | way 中心線、node タグ 0 | 同上。駅前横断の level 接続を確認 | 要再取得 |
| 藤沢 | 藤沢駅 | EPSG:6677 | way 中心線、node タグ 0 | 同上。駅前立体・地下接続を確認 | 要再取得 |
| さいたま | 大宮区・天沼町2丁目 | EPSG:6677 | way 中心線、node タグ 0 | 同上。幹線道路横断を確認 | 要再取得 |

数値基準は、主要歩行者ネットワークの edge 長に対する `explicit + derived` の比率 80%以上、`fallback` 比率 20%以下、代表経路 3本の到着可能率 100%、異なる level 間の誤接続 0件とする。対象範囲に該当データがないこと自体は欠陥ではないが、`fallback` 又は `blocked` として明示する。

品質報告には少なくとも、入力 snapshot / CityGML / override のハッシュと取得日時、edge 数・延長、facility・side・source・confidence 別集計、歩行禁止除外数、横断数、立体交差の接続拒否数、fallback 延長・比率、未接続 component 数、代表経路の結果、目視確認者・日時・既知の制約を記録する。

## 8. v1 からの移行とロールバック

1. v1 の raw snapshot、graph、analysis、bundle は変更しない。
2. capture contract v0.2 で地域ごとの新しい入力を固定し、v2 graph と品質報告を別パスへ出力する。
3. v2 を Unity 日陰解析・server bundle・Viewer へ接続するのは #73 の受入後だけとする。
4. 品質基準を満たさない、又は経路到達性・立体交差テストが失敗した場合、manifest の `recommendedVersion` を v1 とし、消費側は v1 を使用する。
5. ロールバックは v2 成果物を削除することではなく、v1 manifest を再選択する操作とする。既に出力した v2 のハッシュ付き成果物は監査用に残す。

## 9. #72 の受入テスト

- capture contract v0.2 が way・必要 node タグ・必要 relation を固定し、地域ごとの SHA-256 を出力する。
- `sidewalk=left/right/both` の fixture で正しい side・オフセット・双方向接続を生成する。
- 明示独立歩道を車道中心線より優先し、`sidewalk=separate` で重複生成しない。
- 横断タグがある箇所だけで左右歩道を接続し、無根拠の車道横断を生成しない。
- bridge / tunnel / layer / level が不一致の線形を接続しない。
- 歩行禁止の辺を探索結果から除外する。
- 根拠不足では `fallback=true` と source / confidence を記録し、品質閾値で `blocked` 判定できる。
- 同一入力から node / edge ID、geometry、manifest hash が決定的に再現される。
- 5地域すべてで品質報告を出し、代表経路3本の到達性を検証する。

## 10. #73 への受渡条件

#73 は、#72 が次を満たす v2 graph と品質報告を地域ごとに渡した場合のみ統合を開始する。

- `schemaVersion=environment-cost-pedestrian-network-2.0` と v2 manifest が存在する。
- CRS、入力ハッシュ、生成器版、fallback / blocked 判定が記録されている。
- #72 の受入テストが通り、地域品質表の最低条件を満たす。
- Unity は v2 geometry を Raycast のサンプル線として使用し、結果に v2 edge ID と graph fingerprint を残せる。
- server bundle と Viewer は v1 / v2 を混在させず、選択した graph version と品質状態を経路・KPI・ヒートマップに表示する。

v2 へ移行できない地域は、#73 でも v1 の既存成果物を維持し、歩道対応済みであるかのように扱わない。

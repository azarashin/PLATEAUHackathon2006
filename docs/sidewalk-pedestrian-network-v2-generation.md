# 歩道歩行者ネットワーク v2 の生成

Issue #72 の生成パイプラインは、既存の `pedestrian-road-network-1.0` を変更しない。v2 は `environment-cost-pedestrian-network-2.0` として別ファイルに出力する。

## 1. OSM capture contract 0.2

`capture-osm-snapshot-v2.mjs` は `way`、対応する `node`、関係する `relation` を同じOverpass queryで取得する。出力には `captureContractVersion: "0.2"` を記録し、manifest にも要素数・SHA-256 を記録する。relationが0件でもquery契約は維持される。way-only の既存 0.1 snapshot は v2 では拒否する。

```powershell
node tools/road-network/capture-osm-snapshot-v2.mjs `
  --config data/analysis-configs/ichigaya-venue.json `
  --output data/raw/osm/ichigaya-venue/sidewalk-contract-0.2.json `
  --query data/osm-queries/ichigaya-venue-sidewalk-contract-0.2.overpassql `
  --manifest data/osm-snapshot-manifests/ichigaya-venue-sidewalk-contract-0.2.json
```

## 2. v2 graph

```powershell
node tools/road-network/build-sidewalk-pedestrian-graph.mjs `
  --config data/analysis-configs/ichigaya-venue.json `
  --osm data/raw/osm/ichigaya-venue/sidewalk-contract-0.2.json `
  --output data/generated/ichigaya-venue-sidewalk-pedestrian-network-v2.json `
  --report data/raw/ichigaya-venue-sidewalk-pedestrian-network-v2-quality.json
```

v2 の `physicalEdges` は道路・歩道の実体 geometry を一度だけ保持します。`edges` は forward / backward の有向接続として `physicalEdgeId` と `walkingSeconds` を持ち、geometry を複製しません。Unity は physical edge ごとに日陰解析を一度だけ実施し、経路探索側は有向 edge を使って両方向の移動時間を扱います。

品質レポートには physical edge / directed edge 数、根拠別延長、`explicit + derived` 比率、fallback 延長・比率、代表OD経路の結果を出力します。`pedestrian-network-safety-1.0` は80/20を合否に用いません。歩行不能区間の除外、トポロジー、設定済み代表ODを検証し、問題があれば `rejected`、必須監査情報がなければ `unverified`、それ以外は `accepted` です。代表OD未設定は警告として残します。

独立した `footway` / `path` / `pedestrian` / `steps` を優先する。道路の `sidewalk=left/right/both` は平面直角近似で 2 m 横へオフセットする。`sidewalk=separate` は二重生成を避けるため道路から派生させず、独立wayを使う。`foot=no` および徒歩アクセス禁止は除外する。交差接続は `crossing=*` の node に限定し、同じ `level` / `layer` のedgeだけを結ぶ。情報不足時の中心線は `fallback=true` として明示する。

## 地域品質状態

現在コミット済みの市ヶ谷・京都・舞鶴・藤沢・さいたまのsnapshotは0.1（way-only）のため、各 `*-sidewalk-pedestrian-network-verification.json` は `status: "blocked"`、`reason: "capture-contract-0.2-missing"`、`recommendedVersion: "v1"` を出力する。これはv2へ不正に流用しないための意図的な保留であり、再取得は #73 で扱う。

## テスト

```powershell
node tools/road-network/test-capture-osm-snapshot-v2.mjs
node tools/road-network/test-build-sidewalk-pedestrian-graph.mjs
```

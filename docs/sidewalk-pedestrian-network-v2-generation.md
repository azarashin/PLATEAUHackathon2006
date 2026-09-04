# 歩道歩行者ネットワーク v2 の生成

Issue #72 の生成パイプラインは、既存の `pedestrian-road-network-1.0` を変更しない。v2 は `environment-cost-pedestrian-network-2.0` として別ファイルに出力する。

## 1. OSM capture contract 0.2

## 代表 OD と交差点接続の品質ゲート

v2 を `accepted` として発行するには、分析設定の `representativeOds` に、少なくとも1件の実用的な代表起終点を指定する。座標は WGS84 の `[longitude, latitude]` で指定し、起終点はグラフ上の最寄りノードへスナップされる。`maxDetourRatio` は、最短経路長をスナップ後の直線距離で除した上限である。未設定・到達不能・過大な迂回は品質ゲートで失敗する。

```json
"representativeOds": [{
  "id": "major-corridor",
  "start": [139.7349, 35.6910],
  "end": [139.7669, 35.6812],
  "maxSnapDistanceMeters": 75,
  "maxDetourRatio": 2.5
}]
```

`crossing=*` の明示的な横断接続は従来どおり優先する。横断タグがない場合でも、同じ raw OSM node・同じ明示 `level`・同じ `layer` にある、非平行な道路の近接した歩道・中心線フォールバック端点だけを `derived-intersection-corner` として接続する。`layer` は推定接続の互換性確認にだけ使い、共有raw nodeの明示トポロジを分断しない。異なる明示 `level` は接続しない。品質レポートには、明示横断数・推定交差点角接続数・level 分離候補数、および各代表 OD のスナップ距離・経路長・迂回率を記録する。

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

品質レポートには physical edge / directed edge 数、根拠別延長、`explicit + derived` 比率、fallback 延長・比率、代表OD経路の結果を出力します。`pedestrian-network-safety-1.1` は80/20を合否に用いません。歩行不能区間の除外、トポロジー、必須の代表ODの到達可能性・迂回率を検証し、問題があれば `rejected`、必須監査情報がなければ `unverified`、それ以外は `accepted` です。

独立した `footway` / `path` / `pedestrian` / `steps` を優先する。道路の `sidewalk=left/right/both` は平面直角近似で 2 m 横へオフセットする。`sidewalk=separate` は二重生成を避けるため道路から派生させず、独立wayを使う。`foot=no` および徒歩アクセス禁止は除外する。明示横断は `crossing=*` nodeで結び、推定cornerは同じ明示 `level` と `layer` に限る。共有raw nodeの `layer` 差だけでは接続を分断しない。情報不足時の中心線は `fallback=true` として明示する。

## 地域品質状態

市ヶ谷・京都・舞鶴・藤沢・さいたまの capture contract 0.2 snapshot はローカルの `data/raw/osm/<areaId>/sidewalk-contract-0.2.json` にある。新しい品質契約で再生成する際は、上記のv2 graph生成コマンドを各地域に対して実行し、後述の地域検証も行う。raw snapshot・生成graph・品質レポートは大容量のローカル成果物でありGit管理しない。

`pedestrian-network-safety-1.0` の Runtime Shade Result / server bundle は 1.1 のローダーでは意図的に拒否される。graph再生成後は、5地域それぞれについて Runtime Shade Analysis を再実行し、新しい graph fingerprint を持つ結果から server bundle を再構築してからサーバーへ配置する。旧bundleと新graphを混在させて起動してはならない。

## テスト

```powershell
node tools/road-network/test-capture-osm-snapshot-v2.mjs
node tools/road-network/test-build-sidewalk-pedestrian-graph.mjs
```

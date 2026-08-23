# Environment Cost Road Network Builder

#5の歩行道路グラフと#8のUnity時間別解析結果を結合し、#3の`environment-cost-road-network-1.0`契約を生成する。

## 市ヶ谷の生成

先に`viewer`の検証依存関係を準備する。

```powershell
npm --prefix viewer ci

node --max-old-space-size=12288 tools/environment-cost-network/build-environment-cost-road-network.mjs `
  --graph data/generated/ichigaya-pedestrian-road-network.json `
  --environment data/generated/ichigaya-venue-environment-cost.json `
  --output data/generated/ichigaya-environment-cost-road-network-v1.json `
  --report data/raw/ichigaya-environment-cost-road-network-integration-report.json `
  --allow-unmatched-as-missing
```

既定では、道路グラフの`sourceEdgeIds`に対応する解析辺が1件でもなければ失敗する。市ヶ谷では円境界の判定方法の差で112物理辺にサンプルがないため、確認済みの運用では`--allow-unmatched-as-missing`を指定し、その辺を全時刻`missing`、サンプル数0として保持する。一部の`sourceEdgeIds`だけが対応する曖昧な物理辺は、この指定の有無にかかわらず失敗する。

出力前に#3のJSON Schemaと意味検証を実行する。検証が成功した場合だけ`.partial`を最終パスへ置換する。大規模JSONはV8の単一文字列上限を超えるため、フィンガープリントと出力はエッジ単位でストリーミングする。

## 集約規則

物理重複辺の複数`sourceEdgeIds`は、時刻ごとに有効サンプル数で日陰率を加重平均する。サンプル数は合計し、正式道路グラフの`walkingSeconds`を用いて次を再計算する。

```text
solarExposureSeconds = walkingSeconds * (1 - shadeRatio)
```

有効サンプルが0なら`missing`、有効・欠測が混在すれば`partial`、全サンプル有効なら`available`とする。欠測を0へ変換しない。

## テストとfixture

```powershell
node tools/environment-cost-network/test-japan-plane-rectangular.mjs
node tools/environment-cost-network/test-build-environment-cost-road-network.mjs
node tools/environment-cost-network/generate-viewer-fixture.mjs
npm --prefix viewer run validate:contract
npm --prefix viewer run test:contract
```

座標変換テストはJGD2011平面直角座標系第IX系とUnity EUN相対座標を対象に、国土地理院の既知点と往復変換を確認する。生成fixtureは`data/fixtures/environment-cost-road-network-integration-v1.json`で、Viewerの正式契約テストから常時検証する。

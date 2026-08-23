# Environment Cost Road Network Builder

#5の歩行道路グラフと#8のUnity時間別解析結果を結合する。標準出力は経路サーバー用の分割バンドルであり、ブラウザへ直接配信しない。

## サーバーバンドルの生成

```powershell
node --max-old-space-size=8192 tools/environment-cost-network/build-environment-cost-server-bundle.mjs `
  --graph data/generated/ichigaya-pedestrian-road-network.json `
  --environment data/generated/ichigaya-venue-environment-cost.json `
  --bundle-directory data/generated/ichigaya-environment-cost-server-bundle-v1 `
  --report data/raw/ichigaya-environment-cost-server-bundle-report.json `
  --allow-unmatched-as-missing
```

`manifest.json`、接続情報を一度だけ持つ`topology.json`、物理辺ごとのコストを時刻別に持つ`cost-HH.json`を生成する。方向別・時刻別の重複を避け、manifestを最後に書く。市ヶ谷の実データは合計56,914,693 bytesである。

`load-environment-cost-server-bundle.mjs`はファイルサイズ、SHA-256、内容フィンガープリント、参照と値域を検証して型付き配列へ読み込む。`timestamps`を指定すると必要な時刻だけをロードできる。

既定では、道路グラフの`sourceEdgeIds`に対応する解析辺が1件もなければ失敗する。市ヶ谷では円境界の差で112物理辺にサンプルがないため、確認済みの運用では`--allow-unmatched-as-missing`を指定する。一部の`sourceEdgeIds`だけが対応する物理辺は常に失敗する。

## 正式契約の監査出力

Issue #3の`environment-cost-road-network-1.0`単一JSONが必要な監査・互換性確認に限り、`build-environment-cost-road-network.mjs`を使用する。この約612.58 MiBの出力はブラウザ配信用ではない。

## 集約規則

複数`sourceEdgeIds`は時刻ごとに有効サンプル数で日陰率を加重平均し、サンプル数を合計する。正式道路グラフの歩行時間から次を再計算する。

```text
solarExposureSeconds = walkingSeconds * (1 - shadeRatio)
```

有効サンプルが0なら`missing`、有効・欠測が混在すれば`partial`、全サンプル有効なら`available`とし、欠測を0へ変換しない。

## テスト

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

詳細は[`docs/environment-cost-road-network-generation.md`](../../docs/environment-cost-road-network-generation.md)を参照する。

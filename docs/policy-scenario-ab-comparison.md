# 都市施策 A/B 比較（#16）

Viewer は `POST /api/v1/scenario-comparisons` へ同じ地域・日時・起点・終点・日陰回避係数を一度だけ渡し、**現状 (`baseline`)** と施策後を同時に比較する。表示する KPI は選択プロファイルの日陰率、日向時間、歩行時間である。地図上では施策後の 3 経路に加え、選択プロファイルの現状経路を黄線で重ねる。

サーバーは、同じ `roadGraphFingerprintSha256`、中心・半径、基準日・タイムゾーン・時刻集合を持つバンドルだけを同一地域の比較対象として起動時に受け入れる。従って、施策差以外の道路網・日時条件が混入した比較は起動時に失敗する。

## 市ヶ谷デモの作成・起動

Hub と Editor を完全に終了してから、現状と施策後をそれぞれサーバーバンドルに変換する。道路グラフは必ず同じファイルを渡す。

```powershell
$graph = 'data/generated/ichigaya-pedestrian-road-network.json'
node --max-old-space-size=8192 tools/environment-cost-network/build-environment-cost-server-bundle.mjs `
  --graph $graph --environment data/generated/ichigaya-venue-environment-cost.json `
  --bundle-directory data/generated/ichigaya-baseline-route-bundle --report data/generated/ichigaya-baseline-route-bundle-report.json --allow-unmatched-as-missing
node --max-old-space-size=8192 tools/environment-cost-network/build-environment-cost-server-bundle.mjs `
  --graph $graph --environment data/generated/ichigaya-venue-policy-demo-environment-cost.json `
  --bundle-directory data/generated/ichigaya-policy-demo-route-bundle --report data/generated/ichigaya-policy-demo-route-bundle-report.json --allow-unmatched-as-missing

$env:ROUTE_SCENARIO_BUNDLES = '[{"manifestPath":"../data/generated/ichigaya-baseline-route-bundle/manifest.json","scenarioId":"baseline"},{"manifestPath":"../data/generated/ichigaya-policy-demo-route-bundle/manifest.json","scenarioId":"ichigaya-demo-shade"}]'
$env:ROUTE_TIMESTAMPS = '2025-08-01T12:00:00+09:00'
npm --prefix server start
```

Viewer で市ヶ谷デモ条件を選択し、起終点を指定して計算する。`比較する施策` は `ichigaya-demo-shade` を選ぶ。結果には双方の scenario ID、生成時刻、フィンガープリント先頭が表示され、道路別根拠は施策後バンドルを示す。

`ROUTE_BUNDLE_MANIFESTS` は既存の単一（現状）運用との後方互換用である。A/B 比較には `ROUTE_SCENARIO_BUNDLES` を使う。

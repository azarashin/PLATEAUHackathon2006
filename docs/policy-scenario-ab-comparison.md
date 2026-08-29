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

## 複数施策を読み込む設定

`ROUTE_SCENARIO_BUNDLES` は、`manifestPath` と `scenarioId` を持つ **1件以上の JSON 配列**である。要素数は2件に限定されない。同じ地域について、現状と複数の施策案を同時に読み込める。

```powershell
$env:ROUTE_SCENARIO_BUNDLES = '[
  {"manifestPath":"../data/generated/ichigaya-baseline-route-bundle/manifest.json","scenarioId":"baseline"},
  {"manifestPath":"../data/generated/ichigaya-policy-a-route-bundle/manifest.json","scenarioId":"ichigaya-policy-a"},
  {"manifestPath":"../data/generated/ichigaya-policy-b-route-bundle/manifest.json","scenarioId":"ichigaya-policy-b"}
]'
```

- 1件: 単一シナリオの通常経路案内
- 2件: `baseline` と1施策案の A/B 比較
- 3件以上: `baseline` と案A・案Bなどの複数施策を同時に読込

同じ `areaId` 内で複数施策を比較対象にする場合、各バンドルの `roadGraphFingerprintSha256`、対象中心・半径、基準日・タイムゾーン・利用可能時刻集合が一致しなければ、サーバーは起動時に拒否する。`POST /api/v1/scenario-comparisons` は、読込済み配列から `baselineScenarioId` と `policyScenarioId` を1つずつ指定して比較する。

現Viewerは `ichigaya-demo-shade` の1案を固定選択する実装であり、上記の案A・案Bを画面から選ぶ機能は未実装である。複数案を読み込んでも、Viewer側の施策一覧・選択UIを追加するまで画面で切替はできない。

`ROUTE_BUNDLE_MANIFESTS` は既存の単一（現状）運用との後方互換用である。A/B 比較には `ROUTE_SCENARIO_BUNDLES` を使う。

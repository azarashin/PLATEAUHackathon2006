# 歩道ネットワーク v2 と日陰解析の統合

## 境界

`environment-cost-pedestrian-network-2.0` の `physicalEdges` が、日陰サンプルの正本です。Runtime City Package の設定に任意の `sidewalkNetworkPath` を指定すると、Package Builder は物理辺を一辺一回だけ `runtime-shade-input.json` へ変換し、`environment-cost-runtime-shade-input-0.3` を出力します。forward/backward の有向辺を二重に解析しません。

出力の各辺は `physicalEdgeId`、結果の `provenance` は `graphFingerprintSha256` と `networkQuality` を保持します。これにより、経路グラフ・日陰コスト・品質レポートを同じネットワーク版として追跡できます。

`sidewalkNetworkPath` を指定しない既存Packageは入力0.1のまま生成され、従来のOSM中心線解析とserver bundle v1を変更しません。

## 品質とblockedの扱い

v2グラフに品質数値が同梱されない場合、Runtime入力の `quality.status` は必ず `unverified` です。これは解析失敗ではありませんが、歩道品質を確認済みとは扱いません。`sidewalk-pedestrian-network-quality-report-2.0` の `graphFingerprintSha256` と `explicitOrDerivedRatio` / `fallbackRatio` を照合してから、配布用Packageの品質を `accepted` と記録します。

現時点の5地域はcapture contract 0.2を取得していないためv2再計算は **blocked** です。v0.2の既存結果をv2 verifiedとして再利用してはいけません。再現手順は次の通りです。

```powershell
node tools/road-network/capture-osm-snapshot-v2.mjs --config data/analysis-configs/<area>.json --output data/raw/osm/<area>/sidewalk-contract-0.2.json --query data/osm-queries/<area>-sidewalk-contract-0.2.overpassql --manifest data/osm-snapshot-manifests/<area>-sidewalk-contract-0.2.json
node tools/road-network/build-sidewalk-pedestrian-graph.mjs --config data/analysis-configs/<area>.json --osm data/raw/osm/<area>/sidewalk-contract-0.2.json --output data/generated/<area>-sidewalk-pedestrian-network-v2.json --report data/raw/<area>-sidewalk-pedestrian-network-v2-quality.json
```

この後に `sidewalkNetworkPath` を設定してRuntime City Packageを再生成し、Unityの全時刻解析を実行します。品質レポートが閾値未達または代表OD失敗なら、解析結果は出力しても `accepted` / `verified` と報告しません。

## サーバー境界

route serverの現行loaderは server bundle v1だけを実行可能です。`environment-cost-server-bundle-2.0` を検出した場合は、v1として誤読せず「v2を識別したがv2 route engineの明示デプロイが必要」として安全に拒否します。v2 topology/cost/manifestの配備は #65 の後続作業で、v2専用のloader・経路エンジン・Viewerを同時に有効化して行います。

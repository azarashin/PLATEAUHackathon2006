# 歩道ネットワーク v2 と日陰解析の統合

## 境界

`environment-cost-pedestrian-network-2.0` の `physicalEdges` が、日陰サンプルの正本です。Runtime City Package の設定に任意の `sidewalkNetworkPath` を指定すると、Package Builder は物理辺を一辺一回だけ `runtime-shade-input.json` へ変換し、`environment-cost-runtime-shade-input-0.3` を出力します。forward/backward の有向辺を二重に解析しません。

出力の各辺は `physicalEdgeId`、結果の `provenance` は `graphFingerprintSha256` と `networkQuality` を保持します。これにより、経路グラフ・日陰コスト・品質レポートを同じネットワーク版として追跡できます。

`sidewalkNetworkPath` を指定しない既存Packageは入力0.1のまま生成され、従来のOSM中心線解析とserver bundle v1を変更しません。

## 品質とblockedの扱い

v2グラフは `quality` 要約として、`qualityContractVersion: pedestrian-network-safety-1.1`、`accepted` / `rejected` / `unverified`、明示・推定および共有空間代表線の延長比、検証結果を保持する。Runtime City Package とserver bundleは、この契約の `accepted` グラフだけを受け入れる。`explicitOrDerivedRatio` と `fallbackRatio` は地域のデータ充足度を把握する参考指標であり、単独では合否に使わない。

`accepted` は、高速道路・自動車専用道路・徒歩禁止・徒歩許可のない立入制限区間を除外し、物理辺と有向辺の対応・同一levelでの横断接続を検証できた状態である。生活道路や、徒歩が明示禁止されていない `trunk` は共有空間の代表線として残せる。`rejected` は既知の歩行不能区間の混入、誤接続、構造破損、代表ODの到達不能を示す。`unverified` は必須の監査情報（地域ID、取得契約など）が欠ける場合だけに使い、代表ODが未設定であることは警告として記録する。

2026-09-01に5地域のcapture contract 0.2を取得した。その結果、全地域でOSM単独の歩道根拠が品質基準に未達だった。したがって、v2の日陰再計算・bundle作成・Viewer配布は **blocked** のままとし、v1を継続利用する。

| 地域 | 明示・推定歩道延長比 | 中心線フォールバック延長比 |
| --- | ---: | ---: |
| 市ヶ谷 | 50.3% | 49.7% |
| 京都 | 35.2% | 64.8% |
| 舞鶴 | 20.1% | 79.9% |
| 藤沢 | 19.6% | 80.4% |
| さいたま | 15.9% | 84.1% |

地域別の機械可読な失敗理由と入力ハッシュは `data/<areaId>-sidewalk-pedestrian-network-verification.json` に記録する。v0.1の中心線結果をv2 verifiedとして再利用してはいけない。次の再現手順で、CityGML交通データまたは根拠付き地域補正を追加した後に再検証する。

```powershell
node tools/road-network/capture-osm-snapshot-v2.mjs --config data/analysis-configs/<area>.json --output data/raw/osm/<area>/sidewalk-contract-0.2.json --query data/osm-queries/<area>-sidewalk-contract-0.2.overpassql --manifest data/osm-snapshot-manifests/<area>-sidewalk-contract-0.2.json
node tools/road-network/build-sidewalk-pedestrian-graph.mjs --config data/analysis-configs/<area>.json --osm data/raw/osm/<area>/sidewalk-contract-0.2.json --output data/generated/<area>-sidewalk-pedestrian-network-v2.json --report data/raw/<area>-sidewalk-pedestrian-network-v2-quality.json
```

この後に `sidewalkNetworkPath` を設定してRuntime City Packageを再生成し、Unityの全時刻解析を実行します。Runtime入力0.3は物理辺の全ポリライン頂点を保持し、折れ点を直線化せず全線分を重複なくサンプリングする。品質レポートが閾値未達または代表OD失敗なら、Package生成は失敗し、解析結果を `accepted` / `verified` と報告しない。

## 5地域の再現可能な全時刻Runtime解析

`data/runtime-city-packages/<area>-sidewalk-v2.json` はv0.2専用設定である。`sidewalkNetworkPath`（`accepted` の歩行ネットワークv2）と検証Scene、結果JSON、完了markerを必須とし、旧v0.1設定で必須だった道路bundle・基準環境コストは任意である。v2ではBuilderが基準環境コストを読まず、分析設定と物理辺geometryだけから `runtime-shade-input.json` 0.3を作る。指定された任意ファイルだけをパッケージにコピーする。

Unity HubとEditorを完全に終了した後、地域ごとに次を実行する。`<area>` は `ichigaya-venue`、`kyoto`、`maizuru`、`fujisawa`、`saitama` のいずれかである。

```powershell
$unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.18f1\Editor\Unity.exe'
& $unity -batchmode -quit -nographics `
  -projectPath 'tools\plateau-environment-cost-analyzer' `
  -executeMethod EnvironmentCostRuntimeShadeBatchRunner.Run `
  -runtimeCityPackageConfig "data\runtime-city-packages\<area>-sidewalk-v2.json" `
  -logFile "tools\plateau-environment-cost-analyzer\Logs\runtime-shade-v2-<area>.log"
```

バッチはSceneを開いて `Physics.SyncTransforms()` を実行後、既存の `EnvironmentCostRuntimeShadeAnalyzer` と `EnvironmentCostRuntimeShadeResultStore` を使い0〜23時を解析する。結果は `data/generated/<area>-sidewalk-v2-runtime-shade-result.json`、正常完了markerは同名の `.complete.json` に原子的に保存する。保存前後に全physical edge・24時・areaId・graph fingerprint・品質契約・結果semantic fingerprintの一致を検証する。成功ログは `ENVIRONMENT_COST_RUNTIME_SHADE_BATCH_READY` と `ENVIRONMENT_COST_RUNTIME_SHADE_BATCH_COMPLETE` である。

## サーバー境界

route serverはserver bundle v1とv2をschemaVersionで分岐して読み込む。v2では文字列node ID、物理辺の完全geometry、歩道品質、グラフfingerprintを保持する。v1/v2間、または異なるグラフfingerprint間のA/B比較は拒否する。v2 bundleは品質ゲートを通過した結果だけを作成でき、配備・有効化の運用自動化は引き続き #65 の対象とする。

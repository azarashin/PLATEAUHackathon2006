# 資料索引

目的や作業のタイミングから参照先を選ぶための索引です。大容量のrawデータ・生成データはGit管理外ですが、
取得条件、再生成手順、品質・検証の要約は各資料と`data/*verification.json`に残します。

## 目次

- [まず全体像を把握する](#まず全体像を把握する)
- [道路ネットワークとOSM](#道路ネットワークとosm)
- [Unity環境コスト解析](#unity環境コスト解析)
- [Viewer・経路サーバー・運用](#viewer経路サーバー運用)
- [テスト・継続的インテグレーション](#テスト継続的インテグレーション)

## まず全体像を把握する

| 資料 | 簡易説明 | 参照する場面 |
|---|---|---|
| [対象地域・表示モード・入力データ](target-area-and-input-data.md) | 5地域、中心点、半径4km、座標系、CityGML・OSMの入力方針 | 対象地域やデータ条件を確認・変更するとき |
| [環境コスト道路ネットワークデータ契約 v1](environment-cost-data-contract-v1.md) | Unity出力からViewer・サーバーへ渡すJSON/GeoJSONの正式契約 | データ形式や欠測・単位を実装するとき |
| [開発ガイド](development.md) | リポジトリ構成、開発上の基本方針 | 開発環境や役割を把握するとき |
| [バージョン方針](versions.md) | Node.js、Unityなどの採用バージョン | 環境差異を調査するとき |

## 道路ネットワークとOSM

| 資料 | 簡易説明 | 参照する場面 |
|---|---|---|
| [歩行用道路グラフ生成ツール](../tools/road-network/README.md) | OSMノードID付き入力からグラフを作る共通ツール、補正、テスト | 任意地域でグラフを生成するとき |
| [市ヶ谷周辺の歩行道路グラフ生成・品質確認](ichigaya-pedestrian-road-network.md) | 市ヶ谷での初回生成記録、品質値、代表経路 | 市ヶ谷の基準値を確認するとき |
| [4地域の歩行道路ネットワーク](four-region-pedestrian-road-networks.md) | 京都・舞鶴・藤沢・さいたまの取得、生成、品質、代表経路、再生成手順 | #36の4地域データを再生成・検証するとき |
| [ノードID付きOSMによるUnity再解析手順](reanalyze-unity-with-node-id-osm.md) | OSMスナップショットを更新した後にUnity解析以降を整合して再実行する手順 | OSMを再取得・差し替えしたとき |

品質レポートのローカル出力先は`data/raw/<areaId>-pedestrian-road-network-quality.json`です。Gitへ残す要約・決定性・代表経路の検証結果は`data/<areaId>-pedestrian-road-network-verification.json`です。

## Unity環境コスト解析

| 資料 | 簡易説明 | 参照する場面 |
|---|---|---|
| [時間別環境コストの解析・検証・可視化](hourly-environment-cost-analysis.md) | 日陰率・日射曝露、キャッシュ、欠測、Unityヒートマップ、検証方法 | 解析ロジックやUnityでの確認を行うとき |
| [環境コスト Inspection Scene のDEM・影・実行時確認](environment-cost-inspection-runtime.md) | DEM、遮蔽物、自由カメラ、Windows Playerの確認手順 | CityGML読込結果を3D表示・ビルドで確認するとき |
| [Runtime UI の入力フォーカス境界](runtime-ui-input-focus.md) | UI Toolkit とカメラ操作で競合するキーボード入力の原因、対策、回帰試験 | Runtime UI の入力・フォーカス挙動を変更するとき |
| [Runtimeの施策前後経路・KPI比較](runtime-route-comparison.md) | 現状・案A・案BのRuntime経路計算、表示、比較証跡、操作手順 | Runtime内で施策効果を比較・検証するとき |
| [Runtime 道路別ヒートマップ比較](runtime-road-heatmap-comparison.md) | 同一条件の道路辺ごとの改善・悪化・品質状態を可視化し、JSON証跡を出力する | Runtimeで道路別の施策影響を確認するとき |
| [太陽位置計算と3D影表示](solar-position-and-3d-shadows.md) | 日時・地域からの太陽方位／高度、Inspection SceneのDirectional Light・影、夜間の扱い | 太陽位置の計算根拠や影表示を確認・変更するとき |
| [市ヶ谷1地域分の実解析結果](ichigaya-pilot-analysis.md) | 市ヶ谷で行った実解析の記録と旧成果物との関係 | 市ヶ谷の実行実績・性能値を確認するとき |
| [環境コスト道路ネットワーク生成](environment-cost-road-network-generation.md) | 解析結果と道路グラフを結合し、サーバー用データを生成する手順 | Unity解析後に経路用データを作るとき |

## Viewer・経路サーバー・運用

| 資料 | 簡易説明 | 参照する場面 |
|---|---|---|
| [地域・現在位置・起終点・日時指定UI](viewer-location-and-route-controls.md) | 地域選択、GPS、地図クリック、データ状態のUI仕様 | Viewerの操作・状態表示を変更するとき |
| [経路サーバーAPI](route-server.md) | `/api/v1/routes`、道路色API、環境変数、起動方法 | APIの利用・経路サーバー設定を確認するとき |
| [Viewer・経路サーバー環境構築](server-deployment.md) | 初回配備、Nginx、環境変数、データ転送 | 新しいサーバーを構築するとき |
| [Viewer・経路サーバー更新ランブック](server-operation-runbook.md) | コード・設定・データ更新後のビルドと再起動 | 稼働中のサービスを更新するとき |
| [市ヶ谷 E2E デモ運用手順](e2e-demo-runbook.md) | 実演前後の確認、異常時の復旧、性能記録 | 市ヶ谷デモを実施するとき |

## テスト・継続的インテグレーション

| 資料 | 簡易説明 | 参照する場面 |
|---|---|---|
| [自動テストとCI](continuous-integration.md) | ローカル検証、GitHub Actions、テストの責務 | 変更後の検証やCI失敗を調べるとき |

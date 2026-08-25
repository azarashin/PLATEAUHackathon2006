# 自動テストとCI

## 目的

Unity解析、道路グラフ、サーバーバンドル、経路API、Viewerの境界不整合を、Git管理できる小型fixtureで検出する。
大容量CityGMLや市ヶ谷の実データはCIへ投入しない。

## ローカルでの統合検証

クリーンチェックアウト後、Node.js 22.18.0以上22系とnpmを用意して次を実行する。

```bash
npm --prefix viewer ci
node tools/ci/verify.mjs
```

`VERIFY_ALL_PASSED`が最後に表示され、終了コードが0なら成功である。各処理は`=== VERIFY <対象> ===`で区切られる。
失敗時は`VERIFY_STEP_FAILED`、対象名、子プロセスの終了コードを出力し、その時点で終了する。

統合コマンドは次を順番に検証する。

| 対象 | 主な検証内容 |
|---|---|
| 歩行道路グラフ | OSMノード接続、一方向、重複統合、参照、ゼロ長辺、連結成分、最短経路 |
| 時間別環境コスト | 日陰率0・1、サンプル数0、欠測理由、日射曝露時間の式、時刻の完全性 |
| 座標変換 | WGS 84と平面直角座標の既知点、軸順、往復変換 |
| 環境コスト結合 | Unity同形式fixtureと道路グラフの結合、明示的欠測、物理辺共有 |
| サーバーバンドル | manifest・topology・時刻別cost、参照、fingerprint、改変検知 |
| 経路サーバー | 3経路と固定KPI、道路スナップ、到達不能、異常リクエスト、道路辺API |
| Viewer・正式契約 | 正常・欠損・不正fixture、JSON Schema、表示用パーサー、型検査、production build |

## GitHub Actions

`.github/workflows/ci.yml`はPull Requestと`main`へのpushで統合検証を実行する。
権限は`contents: read`だけとし、同じブランチへ新しい更新が入った場合は古い実行をキャンセルする。
依存関係は`viewer/package-lock.json`を正本に`npm ci`で復元する。

必須チェックとして利用する場合はGitHubのbranch protectionでワークフローの
`データ・日陰・経路・Viewer`をrequired status checkに設定する。リポジトリ設定の変更は管理者が行う。

## Unity規則テスト

Unity側にはCityGMLを読み込まない小型規則テストがあり、日陰率0・1、サンプル数0、欠測状態、時刻形式を確認する。

実行前に Unity Editor と **Unity Hub を完全に終了**する。Hub が残した
`Unity.Licensing.Client` は Unity 6000.3.18f1 同梱クライアントとプロトコル不整合を
起こし、自己テストが成功していても終了コード `1` を返すことがある。タスクマネージャーで
`Unity Hub` と `Unity.Licensing.Client` が残っていないことを確認する。self-hosted runner
でも、Unity Hub を起動せずにこのジョブを実行する。

```powershell
$unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.18f1\Editor\Unity.exe'
$project = '<repository-root>\tools\plateau-environment-cost-analyzer'
$log = '<repository-root>\hourly-cost-self-test.log'
$process = Start-Process -FilePath $unity -ArgumentList @(
  '-batchmode', '-nographics', '-projectPath', $project,
  '-executeMethod', 'HourlyEnvironmentCostSelfTests.Run', '-logFile', $log
) -Wait -PassThru
if ($process.ExitCode -ne 0) { exit $process.ExitCode }
Select-String -Path $log -Pattern 'HOURLY_ENVIRONMENT_COST_SELF_TEST_PASSED'
```

Unity Editor本体、PLATEAU SDKのGit依存、Unityライセンスが必要なため、GitHubホストランナーの必須CIには含めない。
代わりに同じ境界値と出力規則をNode.js fixtureでも必須CIとして検証する。Unityライセンスを安全に提供できる
self-hosted runnerを用意した場合は、上記コマンドを追加ジョブとして実行する。

## Issue #14 受け入れ条件との対応

| 受け入れ条件 | 自動確認 |
|---|---|
| 正常・欠損・不正値データ | 正式契約fixture、invalid mutation、サーバーバンドル改変テスト |
| 既知グラフの3経路とKPI | `server/test/route-service.test.mjs`の固定期待値 |
| 日陰率0・1、サンプル数0 | Node.js時間別検証とUnity規則テスト |
| Unity出力または同形式fixtureをViewerが利用 | `environment-cost-road-network-integration-v1.json`の契約・表示検証 |
| クリーンチェックアウトで再現 | `npm ci`と`node tools/ci/verify.mjs` |
| PR・main更新時にCI確認 | `.github/workflows/ci.yml` |
| 失敗を終了コードとログで判別 | `VERIFY_STEP_FAILED`と各テストランナーの非0終了 |

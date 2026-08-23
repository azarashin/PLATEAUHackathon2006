# Viewer・経路サーバー更新ランブック

この文書は、初回構築済みのサーバーへコードや設定を反映する日常運用手順をまとめる。
初回のsystemd・Nginx・環境変数設定と障害対応は
[Viewer・経路サーバー環境構築](server-deployment.md)を参照する。

## 1. 2つのサービスとビルド要否

| 対象 | 実行内容 | コード更新時の操作 |
|---|---|---|
| Viewer | `viewer/dist/`の静的ファイルをVite previewまたはNginxで配信 | `npm run build`が必要。Vite preview構成ではViewerサービスも再起動 |
| 経路サーバー | Node.jsが`server/src/*.mjs`を直接実行 | ビルド不要。`server/src/`変更時は経路サーバーを再起動 |

ViewerのTypeScript・CSSはブラウザが直接利用せず、Viteが`viewer/dist/`へ変換したJavaScript・CSSを
配信する。したがって`git pull`だけではViewerの変更は反映されない。

経路サーバーにはコンパイル工程がない。現在の`server/package.json`には外部依存もないため、
経路サーバーのコード更新だけなら`npm ci`も不要である。将来依存パッケージを追加する場合は
`server/package-lock.json`もGit管理し、その変更を含む配備で`npm --prefix server ci`を実行する。

以下では運用中のサービス名を次として記載する。異なる名前で登録した環境では読み替える。

```text
Viewer:       environment-cost-route-finder.service
経路サーバー: environment-cost-route-server.service
```

## 2. コード更新時の標準手順

Viewerと経路サーバーの両方に変更がある場合は、リポジトリルートで次を実行する。
`<public-base-path>`はブラウザから見える公開パスであり、通常は
`/environment-cost-route-finder/`である。

```bash
cd <repository-root>
git pull --ff-only

npm --prefix viewer ci
VIEWER_BASE_PATH='<public-base-path>' npm --prefix viewer run build

sudo systemctl restart environment-cost-route-server.service
sudo systemctl restart environment-cost-route-finder.service
```

依存ファイルに変更がない場合も、Viewerでは再現可能な配備のため`npm ci`を標準手順に含める。
ビルドに失敗した場合はサービスを再起動せず、現在配信中の`dist/`を維持する。

片方だけを変更した場合は次の最小手順でよい。

### 2.1. Viewerだけを変更

```bash
cd <repository-root>
git pull --ff-only
npm --prefix viewer ci
VIEWER_BASE_PATH='<public-base-path>' npm --prefix viewer run build
sudo systemctl restart environment-cost-route-finder.service
```

静的ファイルをNginxから直接配信している構成では、`dist/`の生成後にViewerサービスの再起動は不要である。
Vite preview構成では、起動中プロセスへ新しいビルド成果物を確実に反映するため再起動する。

### 2.2. 経路サーバーだけを変更

```bash
cd <repository-root>
git pull --ff-only
sudo systemctl restart environment-cost-route-server.service
```

`server/src/*.mjs`はNode.jsが直接読み込むため、`npm run build`は存在しない。

## 3. 設定・データだけを変更した場合

| 変更内容 | 必要な操作 |
|---|---|
| Viewerの実行時環境変数 | Viewerサービスを再起動 |
| `VIEWER_BASE_PATH`または`VITE_*` | Viewerを再ビルドしてからViewerサービスを再起動 |
| 経路サーバーの環境変数 | 経路サーバーを再起動 |
| 経路バンドル | 配置・検証後に経路サーバーを再起動 |
| systemdユニットファイル | `daemon-reload`後に該当サービスを再起動 |
| Nginx設定 | `nginx -t`成功後にNginxをreload |

`systemctl daemon-reload`はサービスのコードや環境変数ファイルを変更しただけでは不要である。
`.service`ファイルそのものを変更した場合だけ実行する。

```bash
sudo systemctl daemon-reload
sudo systemctl restart <changed-service-name>
```

Nginx設定を変更した場合だけ、次を実行する。

```bash
sudo nginx -t
sudo systemctl reload nginx
```

## 4. 再起動後の確認

まずサーバー内部のサービスと経路APIを確認する。`<route-port>`は
`/etc/environment-cost-route-server.env`の`PORT`と一致させる。

```bash
sudo systemctl status environment-cost-route-server.service --no-pager
sudo systemctl status environment-cost-route-finder.service --no-pager

curl --fail --show-error http://127.0.0.1:<route-port>/healthz
curl --fail --show-error --head \
  http://127.0.0.1:<viewer-port><public-base-path>
```

失敗したサービスはログを確認する。

```bash
sudo journalctl -u environment-cost-route-server.service -n 100 --no-pager
sudo journalctl -u environment-cost-route-finder.service -n 100 --no-pager
```

最後に公開URLを確認する。

```bash
curl --fail --show-error --head \
  https://<public-hostname><public-base-path>
curl --include \
  https://<public-hostname><public-base-path>api/v1/routes
```

経路APIへのGETはJSON形式のHTTP 405が正常である。HTTP 200かつ`text/html`はViewerへ誤転送、
HTTP 502は経路サーバーが起動していないかNginxの転送先ポートが一致していない状態を示す。
ブラウザではキャッシュを無効化して再読込し、起終点を指定して3経路の描画とKPI更新まで確認する。

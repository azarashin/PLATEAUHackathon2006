# Viewer サーバー環境構築

この文書では、Viewer を外部公開するための基本構成を説明します。
実際のホスト名、配置パス、OSユーザー名はリポジトリへコミットせず、サーバー上で設定してください。

## 前提

- Node.js 22.18.0
- npm 11.5.2
- Nginx
- Viewer の公開URLは HTTPS 化することを推奨
- リポジトリのチェックアウト先を `<repository-root>` と表記

## サービス化せず一時的に試す

### 1. Viewer 実装ブランチを取得

Issue #10 が main へマージされる前に試す場合は、Viewer 実装ブランチを明示的に取得します。

```bash
cd <repository-root>
git fetch origin
git switch codex/issue-10-maplibre-viewer
git pull --ff-only
```

main へマージされた後は、運用対象の main またはリリースブランチを使用してください。

### 2. Node.js依存パッケージを導入

開発サーバーを停止してから、lockfileどおりに依存パッケージを導入します。

```bash
cd <repository-root>/viewer
node --version
npm --version
npm ci
npm ls maplibre-gl
```

`npm ls` で `maplibre-gl` とバージョンが表示されることを確認します。
ソースコードだけを更新して `npm ci` を実行していない場合、Vite は `maplibre-gl` を解決できません。

### 3. 現在のシェルだけに環境変数を設定

次の値は例示用プレースホルダーです。実際の値はサーバー上で入力し、リポジトリへ記録しません。

```bash
export __VITE_ADDITIONAL_SERVER_ALLOWED_HOSTS='<public-hostname>'
export VIEWER_BIND_HOST='<bind-address>'
export VIEWER_PORT='<viewer-port>'
export VIEWER_BASE_PATH='<public-base-path>'
```

`<public-base-path>` は先頭と末尾を `/` にした公開パスです。ドメイン直下で公開する場合は `/`、
サブパスで公開する場合は `/path-name/` の形式で指定します。

同じサーバー上のリバースプロキシから接続する場合、`<bind-address>` にはループバックアドレスを指定します。
Viteへ外部端末から直接接続する構成では、ファイアウォールとアクセス制限を確認してください。

### 4. 開発サーバーを起動

```bash
npm run dev -- \
  --host "$VIEWER_BIND_HOST" \
  --port "$VIEWER_PORT" \
  --strictPort
```

アクセスURLはViteの起動ログで確認します。終了するときは `Ctrl+C` を押します。
一時環境変数も削除する場合は次を実行します。

```bash
unset __VITE_ADDITIONAL_SERVER_ALLOWED_HOSTS
unset VIEWER_BIND_HOST
unset VIEWER_PORT
unset VIEWER_BASE_PATH
```

### `maplibre-gl` のimportエラーが出る場合

次のエラーは、サーバー側の依存パッケージが未導入か、Viewer実装を含まないブランチを使用している場合に発生します。

```text
Failed to resolve import "maplibre-gl" from "src/main.ts"
```

開発サーバーを停止し、次の順序で確認します。

```bash
cd <repository-root>
git branch --show-current
git log -1 --oneline
grep 'maplibre-gl' viewer/package.json

cd viewer
npm ci
npm ls maplibre-gl
```

`package.json` に依存定義がない場合は、Viewer実装を含むブランチへ切り替えてください。
依存定義があるのに `npm ls` が失敗する場合は、`npm ci` のエラー出力を確認します。

## 推奨構成：静的ファイルをNginxから配信

本番・デモ公開では、Vite サーバーを外部公開せず、ビルド結果をNginxから直接配信します。
この構成では Vite の `allowedHosts` は関係しません。

### 1. インストールとビルド

```bash
cd <repository-root>/viewer
npm ci
npm run build
```

成果物は `<repository-root>/viewer/dist` に生成されます。

### 2. Nginx設定を生成

`deploy/nginx/viewer-static.conf.template` には実際のサーバー名を含めていません。
次の値をサーバー上で設定し、テンプレートを展開します。

```bash
export VIEWER_SERVER_NAME='<public-hostname>'
export VIEWER_DIST_ROOT='<repository-root>/viewer/dist'
export VIEWER_HTTP_PORT='<http-listen-port>'

envsubst '${VIEWER_SERVER_NAME} ${VIEWER_DIST_ROOT} ${VIEWER_HTTP_PORT}' \
  < <repository-root>/deploy/nginx/viewer-static.conf.template \
  | sudo tee /etc/nginx/conf.d/environmental-cost-viewer.conf
```

設定を検証して反映します。

```bash
sudo nginx -t
sudo systemctl reload nginx
```

TLS証明書とHTTPSリダイレクトは、サーバーの証明書管理方針に従って追加してください。

## 暫定構成：Vite previewを使用

短期デモ等でVite previewをリバースプロキシ配下に置く場合だけ使用します。
待受アドレスとポートはサーバー側の環境ファイルで設定し、外部からpreviewへ直接接続させないでください。

### 1. 許可ホストをサーバー側へ設定

`deploy/viewer.env.example` をGit管理外の場所へコピーし、値を設定します。
値にはプロトコルやパスを含めず、ホスト名だけを指定してください。

```bash
sudo install -m 600 <repository-root>/deploy/viewer.env.example /etc/environmental-cost-viewer.env
sudo editor /etc/environmental-cost-viewer.env
```

設定例の形式は次のとおりです。`<public-hostname>` は実際の値へ置き換えます。

```dotenv
__VITE_ADDITIONAL_SERVER_ALLOWED_HOSTS=<public-hostname>
VIEWER_BIND_HOST=<bind-address>
VIEWER_PORT=<viewer-port>
VIEWER_HTTP_PORT=<http-listen-port>
VIEWER_BASE_PATH=<public-base-path>
```

すべてのホストを許可する設定は、DNSリバインディング対策を無効化するため使用しません。

### 2. systemdユニットを生成

`deploy/systemd/viewer-preview.service.template` の変数をサーバー上で展開します。

```bash
export VIEWER_SERVICE_USER='<service-user>'
export VIEWER_REPOSITORY_ROOT='<repository-root>'
export VIEWER_ENV_FILE='/etc/environmental-cost-viewer.env'

envsubst '${VIEWER_SERVICE_USER} ${VIEWER_REPOSITORY_ROOT} ${VIEWER_ENV_FILE}' \
  < <repository-root>/deploy/systemd/viewer-preview.service.template \
  | sudo tee /etc/systemd/system/environmental-cost-viewer.service

sudo systemctl daemon-reload
sudo systemctl enable --now environmental-cost-viewer
sudo systemctl status environmental-cost-viewer
```

リバースプロキシからは環境ファイルで指定した `<bind-address>:<viewer-port>` へ接続し、
元の `Host` ヘッダーをViteへ渡します。

サブパスでViteを公開する場合、Nginxは公開パスを削除せずViteへ転送します。
WebSocketも同じlocationで転送してください。

```nginx
location <public-base-path> {
    proxy_pass http://<bind-address>:<viewer-port>;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
}
```

`proxy_pass` の転送先末尾に `/` を付けると公開パスが削除されるため、この構成では付けません。

## 更新手順

静的配信の場合：

```bash
cd <repository-root>
git pull --ff-only
cd viewer
npm ci
npm run build
sudo nginx -t
sudo systemctl reload nginx
```

Vite previewの場合は、ビルド後にサービスも再起動します。

```bash
sudo systemctl restart environmental-cost-viewer
```

## 動作確認

```bash
curl --fail --show-error --head https://<public-hostname>/
curl --fail --show-error --head https://<public-hostname>/environment-costs-phase-a.geojson
```

サブパス配信では、各URLの先頭に `<public-base-path>` を付けて確認します。

```bash
curl --fail --show-error --head https://<public-hostname><public-base-path>
curl --fail --show-error --head https://<public-hostname><public-base-path>environment-costs-phase-a.geojson
```

確認項目：

- Viewer とfixtureがHTTP 200で取得できる
- MapLibreの地図と5本のダミー道路が表示される
- 日陰／内水モードを切り替えられる
- 実ホスト名や証明書秘密鍵がGit差分へ含まれていない

## サブパスで画面が空になる場合

HTMLは取得できても、Viteクライアント、`src/main.ts`、fixtureがサイトのルートを参照すると、
JavaScriptが実行されず空画面になります。ブラウザの開発者ツールまたは次のコマンドで確認します。

```bash
curl --fail --show-error --head https://<public-hostname><public-base-path>@vite/client
curl --fail --show-error --head https://<public-hostname><public-base-path>src/main.ts
curl --fail --show-error --head https://<public-hostname><public-base-path>environment-costs-phase-a.geojson
```

期待するContent-Typeは、順にJavaScript、JavaScript、GeoJSONです。すべて同じHTMLが返る場合は、
`VIEWER_BASE_PATH` とNginxのlocation／`proxy_pass`が一致しているか確認し、Viteを再起動します。

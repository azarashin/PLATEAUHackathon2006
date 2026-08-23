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
VIEWER_BASE_PATH='<public-base-path>' npm run build
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
curl --fail --show-error --head https://<public-hostname>/environment-cost-road-network-v1.json
```

サブパス配信では、各URLの先頭に `<public-base-path>` を付けて確認します。

```bash
curl --fail --show-error --head https://<public-hostname><public-base-path>
curl --fail --show-error --head https://<public-hostname><public-base-path>environment-cost-road-network-v1.json
```

確認項目：

- Viewer とfixtureがHTTP 200で取得できる
- MapLibreの地図と5本のダミー道路が表示される
- 日陰／内水モードを切り替えられる
- 実ホスト名や証明書秘密鍵がGit差分へ含まれていない

## systemdが `Missing script: "start"` で終了する場合

このViewerの `package.json` には `start` スクリプトがありません。systemdユニットが
`npm start` を実行していると、起動直後に終了し、`Restart=on-failure` によって再起動を繰り返します。

まず実際に読み込まれているユニットとログを確認します。

```bash
sudo systemctl cat <service-name>
sudo journalctl -u <service-name> -n 100 --no-pager
```

`WorkingDirectory` が `<repository-root>/viewer` を指していること、`ExecStart` が次の形式であることを
確認します。

```ini
WorkingDirectory=<repository-root>/viewer
EnvironmentFile=/etc/environmental-cost-viewer.env
ExecStart=/usr/bin/npm run preview -- --host ${VIEWER_BIND_HOST} --port ${VIEWER_PORT} --strictPort
```

変更後はビルドとsystemd設定の再読込を行います。

```bash
cd <repository-root>/viewer
npm ci
VIEWER_BASE_PATH='<public-base-path>' npm run build
sudo systemctl daemon-reload
sudo systemctl restart <service-name>
sudo systemctl status <service-name>
```

## サブパスで画面が空になる場合

`VIEWER_BASE_PATH` にはサーバー上の配置先ではなく、ブラウザから見える公開URLのパスを指定します。
例えば `https://example.com/environment-cost-route-finder/` で公開する場合は次の値です。

```dotenv
VIEWER_BASE_PATH=/environment-cost-route-finder/
```

このリポジトリでは、`npm run build`時に`VIEWER_BASE_PATH`を省略した場合の本番既定値も
`/environment-cost-route-finder/`です。別パスへ配置する場合は従来どおり明示指定してください。

`/home/user/repository/viewer/` のようなファイルシステム上のパスを設定すると、Viteはその文字列を
公開URLとして扱い、別のURLへアクセスするよう案内します。

`VIEWER_BASE_PATH` はビルド成果物にも埋め込まれるため、環境ファイルを直しただけでは反映されません。
正しい値を指定して再ビルドし、サービスを再起動します。

```bash
cd <repository-root>/viewer
VIEWER_BASE_PATH='<public-base-path>' npm run build
sudo systemctl restart <service-name>
```

HTMLは取得できても、JavaScript、CSS、fixtureの参照先がサイトのルートになっていると空画面になります。
公開HTMLに埋め込まれた参照先とfixtureの応答を確認します。

```bash
curl --silent --show-error https://<public-hostname><public-base-path> \
  | grep -Eo '(src|href)="[^"]+"'
curl --include https://<public-hostname><public-base-path>environment-cost-road-network-v1.json
```

JavaScriptとCSSのURLには `<public-base-path>` が含まれ、fixtureはHTTP 200でJSONまたはGeoJSONを
返す必要があります。fixtureの要求にHTMLが返る場合は、Nginxのフォールバックが誤って適用されています。

## 経路APIを同一サブパスで公開する

Viewerの既定API URLは`<public-base-path>api/v1/routes`です。Viewerを転送する汎用`location`より前に、
経路API用の完全一致`location`を追加します。次は公開パスが`/environment-cost-route-finder/`、
経路サーバーが`127.0.0.1:3000`の場合です。

### Viewerと経路サーバーの環境変数を分離する

Viewerと経路サーバーは別プロセスなので、環境変数ファイルも分離します。Viewerの`VIEWER_PORT`と
経路サーバーの`PORT`は別の待受ポートです。1つのファイルへ両方を記載することもできますが、
systemdユニット間の設定混同を防ぐため推奨しません。

現在の公開構成に対応するViewer用ファイルの例です。

```dotenv
# /etc/environmental-cost-viewer.env
__VITE_ADDITIONAL_SERVER_ALLOWED_HOSTS=www.pit-creation.com
VIEWER_BIND_HOST=127.0.0.1
VIEWER_PORT=8002
VIEWER_HTTP_PORT=80
VIEWER_BASE_PATH=/environment-cost-route-finder/
```

`__VITE_ADDITIONAL_SERVER_ALLOWED_HOSTS`には、`http://`、`https://`、パスを含めず、ホスト名だけを
指定します。`VIEWER_HTTP_PORT`は外部公開側の設定値であり、経路APIの待受ポートではありません。

経路サーバー用ファイルは別途作成します。`3000`は既定値であり固定ではありません。別の未使用ポートを
選ぶ場合は、後述するNginxの`proxy_pass`も同じ番号へ変更します。

```dotenv
# /etc/environment-cost-route-server.env
HOST=127.0.0.1
PORT=3000
ROUTE_BUNDLE_MANIFESTS=<repository-root>/data/generated/ichigaya-environment-cost-server-bundle-v1/manifest.json
ROUTE_TIMESTAMPS=2025-08-01T12:00:00+09:00
ROUTE_MAXIMUM_SNAP_DISTANCE_METERS=250
ROUTE_MAXIMUM_BODY_BYTES=16384
ROUTE_REQUEST_TIMEOUT_MILLISECONDS=10000
```

manifestには絶対パスを使用します。複数地域をロードする場合は`ROUTE_BUNDLE_MANIFESTS`をカンマ区切りに
します。`ROUTE_TIMESTAMPS`を省略するとmanifestに含まれる全時刻をロードします。

### 経路サーバーのsystemdユニット

Viewerとは別のサービスとして起動します。

```ini
[Unit]
Description=Environment Cost Route Server
After=network.target

[Service]
Type=simple
User=<service-user>
WorkingDirectory=<repository-root>/server
EnvironmentFile=/etc/environment-cost-route-server.env
ExecStart=/usr/bin/npm run start
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

例えば`/etc/systemd/system/environment-cost-route-server.service`へ保存し、次の順で反映します。

```bash
cd <repository-root>/server
npm ci
sudo systemctl daemon-reload
sudo systemctl enable --now environment-cost-route-server
sudo systemctl status environment-cost-route-server
sudo journalctl -u environment-cost-route-server -n 100 --no-pager
```

Nginx設定前に、環境ファイルの`PORT`で待受していることをサーバー内部から確認します。

```bash
sudo ss -ltnp | grep ':3000'
curl --include http://127.0.0.1:3000/healthz
```

`/healthz`がHTTP 200と`{"status":"ok"}`を返さない場合は、Nginxではなく経路サーバーの起動・
manifestパス・ログを先に修正します。

### Nginxから経路サーバーへ転送する

```nginx
location = /environment-cost-route-finder/api/v1/routes {
    proxy_pass http://127.0.0.1:3000/api/v1/routes;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_read_timeout 15s;
}
```

設定反映前後に構文と応答を確認します。GETは経路API側で`405`になることが正常であり、`200 text/html`は
ViewerのHTMLへ誤転送されています。

```bash
sudo nginx -t
sudo systemctl reload nginx
curl --include https://<public-hostname><public-base-path>api/v1/routes
```

公開URLへのGETがJSON形式のHTTP 405になれば、経路サーバーまで到達しています。HTTP 404はAPI用
`location`が未反映、HTTP 200かつ`Content-Type: text/html`はViewerのフォールバックへ誤転送、
HTTP 502は経路サーバーが指定ポートで待受していない状態です。

実リクエストは`POST application/json`です。サーバーの起動・環境変数・リクエスト例は
[経路サーバーAPI](route-server.md)を参照してください。

Vite previewを使う場合は、リバースプロキシを経由せずサーバー内部からも確認します。

```bash
curl --include http://<bind-address>:<viewer-port><public-base-path>
curl --include http://<bind-address>:<viewer-port><public-base-path>environment-cost-road-network-v1.json
```

内部URLが成功し、公開URLだけが失敗する場合はNginx設定を確認します。サブパスを維持する構成では、
`proxy_pass http://<bind-address>:<viewer-port>;` の末尾に `/` を付けません。ブラウザの開発者ツールでは
ConsoleとNetworkを確認し、アセットの404や `Unexpected token '<'` がないか調べます。後者は、
JavaScriptやGeoJSONの代わりにHTMLが返された場合によく発生します。

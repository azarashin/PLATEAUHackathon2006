# Viewer・経路サーバー環境構築

- [1. 前提](#1-前提)
- [2. サービス化せず一時的に試す](#2-サービス化せず一時的に試す)
  - [2.1. Viewer 実装ブランチを取得](#21-viewer-実装ブランチを取得)
  - [2.2. Node.js依存パッケージを導入](#22-nodejs依存パッケージを導入)
  - [2.3. 現在のシェルだけに環境変数を設定](#23-現在のシェルだけに環境変数を設定)
  - [2.4. 開発サーバーを起動](#24-開発サーバーを起動)
  - [2.5. `maplibre-gl` のimportエラーが出る場合](#25-maplibre-gl-のimportエラーが出る場合)
- [3. 推奨構成：静的ファイルをNginxから配信](#3-推奨構成静的ファイルをnginxから配信)
  - [3.1. インストールとビルド](#31-インストールとビルド)
  - [3.2. Nginx設定を生成](#32-nginx設定を生成)
- [4. 暫定構成：Vite previewを使用](#4-暫定構成vite-previewを使用)
  - [4.1. 許可ホストをサーバー側へ設定](#41-許可ホストをサーバー側へ設定)
  - [4.2. systemdユニットを生成](#42-systemdユニットを生成)
- [5. 更新手順](#5-更新手順)
- [6. 動作確認](#6-動作確認)
- [7. systemdが `Missing script: "start"` で終了する場合](#7-systemdが-missing-script-start-で終了する場合)
- [8. サブパスで画面が空になる場合](#8-サブパスで画面が空になる場合)
- [9. 経路APIを同一サブパスで公開する](#9-経路apiを同一サブパスで公開する)
  - [9.1. Viewerと経路サーバーの環境変数を分離する](#91-viewerと経路サーバーの環境変数を分離する)
  - [9.2. ローカルから経路バンドルを1コマンドで配置する](#92-ローカルから経路バンドルを1コマンドで配置する)
    - [9.2.1. SSH接続と公開鍵認証](#921-ssh接続と公開鍵認証)
  - [9.3. 経路サーバーのsystemdユニット](#93-経路サーバーのsystemdユニット)
  - [9.4. Nginxから経路サーバーへ転送する](#94-nginxから経路サーバーへ転送する)


この文書では、Viewerと経路サーバーを外部公開するための初回構築と障害対応を説明します。
構築済みサーバーへコード・設定・経路バンドルを反映する日常手順は
[Viewer・経路サーバー更新ランブック](server-operation-runbook.md)を参照してください。
実際のホスト名、配置パス、OSユーザー名はリポジトリへコミットせず、サーバー上で設定してください。

## 1. 前提

- Node.js 22.18.0
- npm 11.5.2
- Nginx
- Viewer の公開URLは HTTPS 化することを推奨
- リポジトリのチェックアウト先を `<repository-root>` と表記

## 2. サービス化せず一時的に試す

### 2.1. Viewer 実装ブランチを取得

Issue #10 が main へマージされる前に試す場合は、Viewer 実装ブランチを明示的に取得します。

```bash
cd <repository-root>
git fetch origin
git switch codex/issue-10-maplibre-viewer
git pull --ff-only
```

main へマージされた後は、運用対象の main またはリリースブランチを使用してください。

### 2.2. Node.js依存パッケージを導入

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

### 2.3. 現在のシェルだけに環境変数を設定

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

### 2.4. 開発サーバーを起動

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

### 2.5. `maplibre-gl` のimportエラーが出る場合

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

## 3. 推奨構成：静的ファイルをNginxから配信

本番・デモ公開では、Vite サーバーを外部公開せず、ビルド結果をNginxから直接配信します。
この構成では Vite の `allowedHosts` は関係しません。

### 3.1. インストールとビルド

```bash
cd <repository-root>/viewer
npm ci
npm run build
```

成果物は `<repository-root>/viewer/dist` に生成されます。

### 3.2. Nginx設定を生成

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

## 4. 暫定構成：Vite previewを使用

短期デモ等でVite previewをリバースプロキシ配下に置く場合だけ使用します。
待受アドレスとポートはサーバー側の環境ファイルで設定し、外部からpreviewへ直接接続させないでください。

### 4.1. 許可ホストをサーバー側へ設定

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

### 4.2. systemdユニットを生成

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

## 5. 更新手順

通常のコード更新では、Viewerは再ビルドが必要、経路サーバーは再ビルド不要です。
両サービスの再起動、片方だけを変更した場合、systemd・Nginx・経路バンドル変更時の条件分岐は
[Viewer・経路サーバー更新ランブック](server-operation-runbook.md)に集約しています。

初回構築の直後に両方を反映する最小例は次のとおりです。

```bash
cd <repository-root>
npm --prefix viewer ci
VIEWER_BASE_PATH='<public-base-path>' npm --prefix viewer run build
sudo systemctl restart environment-cost-route-server.service
sudo systemctl restart environment-cost-route-finder.service
```

## 6. 動作確認

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

## 7. systemdが `Missing script: "start"` で終了する場合

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

## 8. サブパスで画面が空になる場合

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

ブラウザの`Failed to load module script`と`non-JavaScript MIME type of text/html`も同じ切り分けを
行います。これはJavaScriptのURLにHTMLが返っていることを示し、JavaScript自体の構文エラーでは
ありません。開発者ツールのNetworkで失敗した`*.js`のURLを確認し、応答を直接調べます。

```bash
curl --include https://<public-hostname>/<path-reported-by-browser>.js
```

応答が`Content-Type: text/html`の場合は、`VIEWER_BASE_PATH`を指定した再ビルドが反映されているか、
ブラウザが古い`index.html`をキャッシュしていないか、Nginxのサブパス転送がViewerのHTMLへ
フォールバックしていないかを確認します。再ビルド後は、実際のHTMLに記載されたハッシュ付きJS URLが
HTTP 200かつJavaScriptのContent-Typeで取得できることを確認します。

MapLibreはメインモジュールと同じ`assets/`から`maplibre-gl-worker.mjs`を読み込み、そのワーカーは
`maplibre-gl-shared.mjs`を読み込みます。このリポジトリのVite設定は、本番ビルド時に両ファイルを
`viewer/dist/assets/`へ自動配置します。MIMEエラーの要求URLがいずれかのファイルだった場合は、サーバーで
手作業コピーする前に最新版を取得して再ビルドし、両方がJavaScriptとして取得できることを確認します。

```bash
cd <repository-root>/viewer
npm ci --include=dev
VIEWER_BASE_PATH='<public-base-path>' npm run build

test -f dist/assets/maplibre-gl-worker.mjs
test -f dist/assets/maplibre-gl-shared.mjs

curl --fail --show-error --head \
  https://<public-hostname><public-base-path>assets/maplibre-gl-worker.mjs
curl --fail --show-error --head \
  https://<public-hostname><public-base-path>assets/maplibre-gl-shared.mjs
```

## 9. 経路APIを同一サブパスで公開する

Viewerの既定API URLは`<public-base-path>api/v1/routes`と`<public-base-path>api/v1/road-edges`です。Viewerを転送する汎用`location`より前に、
両API用の完全一致`location`を追加します。次は公開パスが`/environment-cost-route-finder/`、
経路サーバーが`127.0.0.1:3000`の場合です。

### 9.1. Viewerと経路サーバーの環境変数を分離する

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
ROUTE_MAXIMUM_ROAD_EDGE_FEATURES=10000
ROUTE_MAXIMUM_BODY_BYTES=16384
ROUTE_REQUEST_TIMEOUT_MILLISECONDS=10000
```

manifestには絶対パスを使用します。複数地域をロードする場合は`ROUTE_BUNDLE_MANIFESTS`をカンマ区切りに
します。`ROUTE_TIMESTAMPS`を省略するとmanifestに含まれる全時刻をロードします。

### 9.2. ローカルから経路バンドルを1コマンドで配置する

`data/generated/`はGit管理外なので、`git pull`では市ヶ谷の実データはサーバーへ届きません。ローカルで
生成・検証済みのバンドルをSSHで転送するため、設定例をコピーします。

```powershell
Copy-Item deploy/route-bundle-upload.env.example deploy/route-bundle-upload.env
```

`deploy/route-bundle-upload.env`はGit管理外です。サーバーIPまたはホスト名、SSHユーザー、ポート、
リポジトリ配置先を実環境へ合わせます。ホストには`http://`や`https://`を付けません。

```dotenv
ROUTE_DEPLOY_HOST=<server-ip-or-hostname>
ROUTE_DEPLOY_USER=<ssh-user>
ROUTE_DEPLOY_SSH_PORT=22
ROUTE_DEPLOY_ROOT=/home/<ssh-user>/<repository-directory>
ROUTE_DEPLOY_BUNDLE_NAME=<remote-bundle-directory-name>
ROUTE_DEPLOY_LOCAL_BUNDLE=data/generated/<local-bundle-directory-name>
```

`ROUTE_DEPLOY_BUNDLE_NAME`はリモートの`data/generated/`直下に作る配置名、
`ROUTE_DEPLOY_LOCAL_BUNDLE`は転送元の生成済みバンドルです。ツール側には市ヶ谷などの地域固有名を
埋め込んでいません。地域ごとに設定ファイルの値だけを変更して同じコマンドを利用できます。

`deploy/route-bundle-upload.env`は配信スクリプトが読み込む設定ファイルであり、バンドル生成コマンドへは
自動適用されません。生成先と転送元の名前違いを防ぐため、生成時は同じ値を現在のPowerShellの環境変数へ
設定し、`--bundle-directory`へ渡します。

```powershell
$env:ROUTE_DEPLOY_LOCAL_BUNDLE = 'data/generated/<local-bundle-directory-name>'

node --max-old-space-size=8192 `
  tools/environment-cost-network/build-environment-cost-server-bundle.mjs `
  --graph data/generated/ichigaya-pedestrian-road-network.json `
  --environment data/generated/ichigaya-venue-environment-cost.json `
  --bundle-directory $env:ROUTE_DEPLOY_LOCAL_BUNDLE `
  --report data/raw/environment-cost-server-bundle-report.json `
  --allow-unmatched-as-missing

if (-not (Test-Path "$env:ROUTE_DEPLOY_LOCAL_BUNDLE/manifest.json")) {
  throw "Route bundle generation failed: $env:ROUTE_DEPLOY_LOCAL_BUNDLE"
}
```

環境変数で設定した値は現在のPowerShellと、そのPowerShellから起動する子プロセスに限って有効です。
生成後の配信コマンドも同じPowerShellで実行します。`deploy/route-bundle-upload.env`にも同じ
`ROUTE_DEPLOY_LOCAL_BUNDLE`を記録しておけば、別のターミナルから実行するときも同じ場所を参照できます。

設定後の転送は1コマンドです。Windows標準のOpenSSH `ssh`と`scp`、ローカルとサーバー双方のNode.jsを
使用します。公開鍵認証を設定しておけば、途中のパスワード入力も不要です。

```powershell
powershell.exe -ExecutionPolicy Bypass -File tools/deployment/publish-route-bundle.ps1
```

スクリプトは次を順に実施します。

1. ローカルの`manifest.json`、`topology.json`、コストファイルを検証する。
2. サーバーの`data/generated/`内に一時ディレクトリを作り、全ファイルを転送する。
3. サーバー上でもSHA-256、参照、値域を検証する。
4. 検証成功後だけ既存ディレクトリをバックアップ名へ移動し、新バンドルへ切り替える。

接続先を一時的に上書きする場合は、環境変数が設定ファイルより優先されます。

```powershell
$env:ROUTE_DEPLOY_HOST = '<temporary-server-ip>'
powershell.exe -ExecutionPolicy Bypass -File tools/deployment/publish-route-bundle.ps1
Remove-Item Env:ROUTE_DEPLOY_HOST
```

この優先順位は`ROUTE_DEPLOY_LOCAL_BUNDLE`を含む全`ROUTE_DEPLOY_*`設定に適用されます。設定ファイルを
修正しても現在のPowerShellに古い値が残っている場合は、古い値が使用されます。配信前に実効値と
生成済みmanifestを確認します。

```powershell
Get-ChildItem Env:ROUTE_DEPLOY_* | Sort-Object Name
Test-Path "$env:ROUTE_DEPLOY_LOCAL_BUNDLE/manifest.json"
```

名前を変更した場合は、生成コマンドの`--bundle-directory`と`ROUTE_DEPLOY_LOCAL_BUNDLE`を同じ値へ
更新します。不要な一時上書きを解除して設定ファイルへ戻す場合は、対象の環境変数を削除します。

```powershell
Remove-Item Env:ROUTE_DEPLOY_LOCAL_BUNDLE
```

`publish-route-bundle.ps1`が存在しない場合は、リポジトリルートにいることと、配備スクリプトを含む
最新版を取得済みであることを確認します。

```powershell
Test-Path .\tools\deployment\publish-route-bundle.ps1
git branch --show-current
git pull --ff-only
```

#### 9.2.1. SSH接続と公開鍵認証

初回接続ではSSHホスト鍵の確認が表示されます。表示されたフィンガープリントを管理者が提示した値と
照合してから受け入れます。同じサーバーをIPアドレスとホスト名の両方で接続した場合、既知の同一鍵が
別名で登録されている旨が表示されることがあります。

`Permission denied (publickey)`はリモートパスの誤りではありません。SSH認証が完了していないため、
一時ディレクトリ作成を含むリモート操作はまだ実行されていません。詳細ログを付けて直接接続を確認します。

```powershell
ssh -v -p $env:ROUTE_DEPLOY_SSH_PORT `
  "$env:ROUTE_DEPLOY_USER@$env:ROUTE_DEPLOY_HOST"
```

秘密鍵を明示する場合は、実在する鍵のパスを指定します。

```powershell
Get-ChildItem "$env:USERPROFILE\.ssh" -File
ssh-add -l
$privateKey = '<absolute-path-to-private-key>'
ssh -i $privateKey `
  -p $env:ROUTE_DEPLOY_SSH_PORT `
  "$env:ROUTE_DEPLOY_USER@$env:ROUTE_DEPLOY_HOST"
```

常に特定の鍵を使う場合は`$env:USERPROFILE\.ssh\config`の対象ホストへ`IdentityFile`と
`IdentitiesOnly yes`を設定します。サーバー側では、対象ユーザーの`~/.ssh/authorized_keys`に対応する
公開鍵が登録されている必要があります。認証確認後、次の読み取り専用コマンドが成功することを確認して
から配信を再実行します。

```powershell
ssh -p $env:ROUTE_DEPLOY_SSH_PORT `
  "$env:ROUTE_DEPLOY_USER@$env:ROUTE_DEPLOY_HOST" `
  'whoami; pwd'
```

転送だけを行い、サービスは自動再起動しません。完了後にサーバーで経路サービスを再起動し、
`/healthz`を確認します。実行内容だけを事前確認する場合は末尾へ`-WhatIf`を付けます。

### 9.3. 経路サーバーのsystemdユニット

Viewerとは別のサービスとして起動します。

Viewerと経路サーバーを1つのユニットファイルへ連結して、`[Service]`セクションを2回記述してはいけません。
systemdは`bad unit file setting`としてユニットを拒否します。Viewer用と経路サーバー用を別々の
`.service`ファイルとして作成し、それぞれに1つの`[Service]`と1つの`ExecStart`を設定します。

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
sudo systemctl daemon-reload
sudo systemctl enable --now environment-cost-route-server
sudo systemctl status environment-cost-route-server
sudo journalctl -u environment-cost-route-server -n 100 --no-pager
```

経路サーバーは現在外部依存を持たず、`.mjs`をNode.jsで直接実行するため、ビルドと`npm ci`は不要です。
依存パッケージを追加した場合はlockfileもGit管理し、その時点で配備手順へ`npm ci`を追加します。

Nginx設定前に、環境ファイルの`PORT`で待受していることをサーバー内部から確認します。

```bash
sudo ss -ltnp | grep ':3000'
curl --include http://127.0.0.1:3000/healthz
```

`/healthz`がHTTP 200と`{"status":"ok"}`を返さない場合は、Nginxではなく経路サーバーの起動・
manifestパス・ログを先に修正します。

### 9.4. Nginxから経路サーバーへ転送する

```nginx
location = /environment-cost-route-finder/api/v1/routes {
    proxy_pass http://127.0.0.1:3000/api/v1/routes;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_read_timeout 15s;
}

location = /environment-cost-route-finder/api/v1/road-edges {
    proxy_pass http://127.0.0.1:3000/api/v1/road-edges;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_read_timeout 15s;
}

location /environment-cost-route-finder/ {
    proxy_pass http://127.0.0.1:8002;
    include /etc/nginx/snippets/proxy-common.conf;
}
```

APIの2つの完全一致`location`とViewer用の汎用`location`を同じHTTPSの`server`ブロックへ置きます。
上例の経路サーバーポート`3000`は固定値ではありません。`/etc/environment-cost-route-server.env`が
`PORT=8003`なら、2か所の`proxy_pass`も`127.0.0.1:8003`へ揃えます。Viewer用の`8002`へ
`road-edges`を転送すると、道路GeoJSONではなくSPAの`index.html`が返ります。

設定反映前後に構文と応答を確認します。GETは経路API側で`405`になることが正常であり、`200 text/html`は
ViewerのHTMLへ誤転送されています。

```bash
sudo nginx -t
sudo systemctl reload nginx
sudo nginx -T 2>/dev/null | grep -A12 -B2 'environment-cost-route-finder/api/v1/road-edges'
curl --include https://<public-hostname><public-base-path>api/v1/routes
curl --include 'https://<public-hostname><public-base-path>api/v1/road-edges?areaId=<area-id>&timestamp=<url-encoded-timestamp>&bbox=<min-lng>,<min-lat>,<max-lng>,<max-lat>&solarAvoidanceFactor=2'
```

`nginx -T`の結果に`road-edges`がなければ、編集したファイルが有効な`server`ブロックにないか、
`sites-enabled`から読み込まれていません。構文検査に成功しただけでは、意図した設定ファイルが
選択されていることの確認にはなりません。

`routes`へのGETがJSON形式のHTTP 405になり、必要パラメーター付きの`road-edges`がHTTP 200になれば、経路サーバーまで到達しています。HTTP 404はAPI用
`location`が未反映、HTTP 200かつ`Content-Type: text/html`はViewerのフォールバックへ誤転送、
HTTP 502は経路サーバーが指定ポートで待受していない状態です。

### 9.5. 道路色が読み込めない場合の切り分け

最初にリバースプロキシを経由せず、経路サーバーへ直接問い合わせます。

```bash
curl --include \
  'http://127.0.0.1:<route-port>/api/v1/road-edges?areaId=ichigaya-venue&timestamp=2025-08-01T12%3A00%3A00%2B09%3A00&bbox=139.7335,35.6880,139.7385,35.6930&solarAvoidanceFactor=2'
```

ここでHTTP 200、`Content-Type: application/json`、本文の`features`が得られれば、バンドルと経路サーバーは正常です。
同じクエリの公開URLだけが`200 text/html`になる場合、データ不足ではなくNginxの誤転送です。
返されたHTMLに`<div id="app"></div>`や`assets/index-*.js`が含まれる場合は、ViewerのSPAフォールバックへ
到達したことを示します。`road-edges`の完全一致`location`を追加または修正し、`nginx -t`後にreloadします。

直接問い合わせも失敗する場合は、環境ファイルの`ROUTE_BUNDLE_MANIFESTS`が指すディレクトリで、
少なくとも次を確認します。

```bash
grep '^ROUTE_BUNDLE_MANIFESTS=' /etc/environment-cost-route-server.env
ls -lh <bundle-directory>/manifest.json \
       <bundle-directory>/topology.json \
       <bundle-directory>/cost-*.json
sudo journalctl -u environment-cost-route-server.service -n 100 --no-pager
```

`manifest.json`、`topology.json`、対象時刻の`cost-*.json`がすでに存在し、内部URLがJSONを返す場合、
道路色データの再生成・再転送は不要です。なおViewerは通信量抑制のため、日陰モード、解析済み地域、
ズーム14.5以上の条件を満たした場合だけ`road-edges`を要求します。

経路探索`routes`の実リクエストは`POST application/json`、道路表示`road-edges`はクエリ付き`GET`です。サーバーの起動・環境変数・リクエスト例は
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

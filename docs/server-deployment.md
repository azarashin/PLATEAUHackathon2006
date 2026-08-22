# Viewer サーバー環境構築

この文書では、Viewer を外部公開するための基本構成を説明します。
実際のホスト名、配置パス、OSユーザー名はリポジトリへコミットせず、サーバー上で設定してください。

## 前提

- Node.js 22.18.0
- npm 11.5.2
- Nginx
- Viewer の公開URLは HTTPS 化することを推奨
- リポジトリのチェックアウト先を `<repository-root>` と表記

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

確認項目：

- Viewer とfixtureがHTTP 200で取得できる
- MapLibreの地図と5本のダミー道路が表示される
- 日陰／内水モードを切り替えられる
- 実ホスト名や証明書秘密鍵がGit差分へ含まれていない

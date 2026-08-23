# Environment Cost Route Server

#9のサーバーバンドルを起動時に検証・読込し、道路スナップ、最短・バランス・日陰優先経路、GeoJSON、KPIを`POST /api/v1/routes`で返します。道路ネットワーク全体はブラウザへ配信しません。

## Fixtureで起動

PowerShell:

```powershell
$env:ROUTE_BUNDLE_MANIFESTS = '../data/fixtures/route-server-bundle-v1/manifest.json'
$env:ROUTE_TIMESTAMPS = '2025-08-01T12:00:00+09:00'
npm start
```

Bash:

```bash
ROUTE_BUNDLE_MANIFESTS=../data/fixtures/route-server-bundle-v1/manifest.json \
ROUTE_TIMESTAMPS=2025-08-01T12:00:00+09:00 \
npm start
```

環境変数の相対パスは`server/`を基準にします。複数地域はmanifestパスをカンマ区切りで指定できます。`ROUTE_TIMESTAMPS`を省略すると全時刻を読み込みます。

## テスト

```bash
npm test
npm run generate:fixture
```

API契約、係数、欠測方針、エラーは[`docs/route-server.md`](../docs/route-server.md)を参照してください。

# Environment Cost Route Server

#9のサーバーバンドルを起動時に検証・読込し、道路スナップ、最短・バランス・日陰優先経路、GeoJSON、KPIを`POST /api/v1/routes`で返します。`GET /api/v1/road-edges`は地図の表示範囲だけについて、道路辺ごとの日陰解析値と探索コスト根拠を返します。道路ネットワーク全体はブラウザへ配信しません。

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

環境変数の相対パスは`server/`を基準にします。複数地域はmanifestパスをカンマ区切りで指定できます。`ROUTE_TIMESTAMPS`を省略すると全時刻を読み込みます。道路辺APIの1応答は既定で10,000辺までです。変更する場合だけ`ROUTE_MAXIMUM_ROAD_EDGE_FEATURES`を指定します。

## テスト

```bash
npm test
npm run generate:fixture
```

## 市ヶ谷実データの確認

```powershell
npm run verify:ichigaya
```

既定では `../data/generated/localHackathon2026Summer/manifest.json` を読み、`../data/ichigaya-route-server-verification.json` を更新します。別の生成済みバンドルを確認する場合は、次のように `--manifest` と `--report` を後ろに追加して既定値を置き換えます。

```powershell
npm run verify:ichigaya -- `
  --manifest ../data/generated/<bundle>/manifest.json `
  --report ../data/<report>.json
```

市ヶ谷中心付近から新宿駅付近までの約3〜5 km条件で、3経路、決定性、HTTPレスポンス量、読込・探索時間、RSS・V8メモリを記録します。

API契約、係数、欠測方針、エラーは[`docs/route-server.md`](../docs/route-server.md)を参照してください。

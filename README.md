# 環境コスト経路マップビューア

PLATEAU の3D都市モデルから都市環境を計算する Unity シミュレーターと、その軽量な計算結果を利用するブラウザ版 Viewer を分離して開発するプロジェクトです。

## 現在の状態

Issue #10 のダミー版 Viewer を実装しています。MapLibre の地図上で日陰・内水コストを切り替え、道路色、凡例、説明、サンプル KPI の連動を確認できます。Unity 側はプロジェクト骨格のみで、PLATEAU SDK の導入と都市モデル読込は Issue #4 の対象です。

## ディレクトリ構成

```text
.
├── viewer/       ブラウザ版 Viewer（Vite + Vanilla TypeScript）
├── simulator/    Unity シミュレーション環境
├── data/         fixture、入力データ、生成データの境界
└── docs/         開発手順と技術判断
```

Unity と Viewer をつなぐ主要インターフェースは、JSON / GeoJSON の環境コストデータです。Viewer は CityGML や Unity アセットを直接読み込みません。

## Viewer の起動

前提バージョンは Node.js 22.18.0、npm 11.5.2 です。

```bash
cd viewer
npm ci
npm run dev
```

Viewer のURLはViteが起動時に表示します。特定のポートを使う場合は、リポジトリへ固定値を
記載せず、起動時の引数またはサーバー側の環境変数で指定してください。

外部サーバーへの配置は [Viewer サーバー環境構築](docs/server-deployment.md) を参照してください。
実際のホスト名はリポジトリへ記載せず、サーバー上の環境変数またはNginx設定へ反映します。

開発サーバーが表示する URL をブラウザで開きます。プロダクションビルドは次のコマンドです。

```bash
cd viewer
npm run build
```

## Unity プロジェクト

Unity Hub で `simulator/` を Unity 6000.5.9f1 として開きます。基本の ProjectSettings は同バージョンの Editor で生成済みです。

PLATEAU SDK for Unity は v4.3.0 を採用予定ですが、導入と動作確認は Issue #4 で行います。

## データ管理

- `data/fixtures/`: Git 管理できる小さなダミー・テストデータ
- `data/raw/`: CityGML 等の大容量入力。中身は Git 管理しない
- `data/generated/`: シミュレーション生成物。中身は Git 管理しない

データの取得元、版、ライセンス、座標系、再生成手順は、データを追加する Issue で文書化します。秘密情報や端末固有値はコミットせず、必要になった時点で `.env.example` のみ追加します。

MVPの対象地区、入力データ、座標・日時条件は
[対象地区と入力データ](docs/target-area-and-input-data.md) に記録しています。

詳細は [開発ガイド](docs/development.md) と [バージョン方針](docs/versions.md) を参照してください。

## ライセンス

[MIT License](LICENSE)

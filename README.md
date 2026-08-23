# 環境コスト経路マップビューア

PLATEAU の3D都市モデルから都市環境を計算する Unity シミュレーターと、その軽量な計算結果を利用するブラウザ版 Viewer を分離して開発するプロジェクトです。

## 現在の状態

Viewer は正式データ契約 v1 の小型 fixture を直接読み込みます。MapLibre の地図上で日陰・内水コストを切り替え、道路色、凡例、説明、サンプル KPI の連動と欠測表示を確認できます。実解析結果との結合は後続 Issue の対象です。

## ディレクトリ構成

```text
.
├── viewer/       ブラウザ版 Viewer（Vite + Vanilla TypeScript）
├── simulator/    Unity シミュレーション環境
├── data/         fixture、入力データ、生成データの境界
└── docs/         開発手順と技術判断
```

Unity と Viewer をつなぐ主要インターフェースは、JSON / GeoJSON の環境コストデータです。Viewer は CityGML や Unity アセットを直接読み込みません。
正式な v1 契約、時刻・欠測・単位・拡張規則は
[環境コスト道路ネットワークデータ契約 v1](docs/environment-cost-data-contract-v1.md) を参照してください。

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

詳細は [開発ガイド](docs/development.md) と [バージョン方針](docs/versions.md) を参照してください。

## ライセンス

[MIT License](LICENSE)

## 汎用環境コスト解析ツール

PLATEAU CityGMLを道路ごとの環境コストへ変換するUnityバッチツールは
[`tools/plateau-environment-cost-analyzer/`](tools/plateau-environment-cost-analyzer/) に置く。地域ごとの実行条件は
[`data/analysis-configs/`](data/analysis-configs/) に置く。

## 解析記録

- [市ヶ谷1地域分の実解析結果](docs/ichigaya-pilot-analysis.md)
- [市ヶ谷周辺の歩行道路グラフ生成・品質確認](docs/ichigaya-pedestrian-road-network.md)
- [ノードID付きOSMによるUnity再解析手順](docs/reanalyze-unity-with-node-id-osm.md)

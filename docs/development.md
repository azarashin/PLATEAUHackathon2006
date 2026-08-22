# 開発ガイド

## 前提

| 対象 | 固定バージョン |
|---|---|
| Node.js | 22.18.0 |
| npm | 11.5.2 |
| Vite | 8.2.2 |
| TypeScript | 7.0.2 |
| MapLibre GL JS | 6.5.0 |
| Unity | 6000.5.9f1（Unity 6.5） |
| PLATEAU SDK for Unity | 4.3.0（Issue #4 で導入） |

更新判断と互換性の注意点は [versions.md](versions.md) に記録します。

## Viewer

### 初回セットアップ

```bash
cd viewer
npm ci
```

Node.js の版が異なる場合は、利用しているバージョンマネージャーでリポジトリ直下の `.node-version` または `.nvmrc` を使用してください。

### 開発

```bash
cd viewer
npm run dev
```

Viewer は `http://127.0.0.1:8002/` で起動します。ポート `8002` が使用中の場合は、
別ポートへ自動変更せず起動を停止するため、使用中のプロセスを終了してから再実行してください。

### 型チェックとビルド

```bash
cd viewer
npm run typecheck
npm run build
```

`viewer/dist/` と `viewer/node_modules/` は生成物のため Git 管理しません。

### 社内証明書が必要な環境

`UNABLE_TO_VERIFY_LEAF_SIGNATURE` が発生し、OS の証明書ストアが組織によって管理されている場合は、TLS 検証を無効化せず Node.js の `--use-system-ca` を利用します。

PowerShell：

```powershell
$env:NODE_OPTIONS = '--use-system-ca'
npm ci
Remove-Item Env:NODE_OPTIONS
```

bash：

```bash
NODE_OPTIONS=--use-system-ca npm ci
```

## Unity

1. Unity Hub に Unity 6000.5.9f1 を追加する
2. Unity Hub の「Add project from disk」で `simulator/` を選択する
3. 初回インポートが完了するまで待つ
4. Unity が生成した設定差分を確認し、端末固有またはキャッシュのファイルをコミットしない

Unity 6000.5.9f1 での Editor 起動と ProjectSettings の生成は確認済みです。対象プラットフォームを確定した後の空ビルドは未確認です。

PLATEAU SDK のパッケージ追加、CityGML 読込、対象プラットフォームのビルド設定は Issue #4 で行います。

## データ

- 小型 fixture は `data/fixtures/` に置く
- 大容量の取得データは `data/raw/` に置き、取得手順だけを Git 管理する
- 再生成可能な出力は `data/generated/` に置く
- 一時ファイルや個人環境の絶対パスをデータ契約へ含めない

Git LFS は、再取得できない小型バイナリを管理する必要が生じた場合に限って導入します。CityGML 本体やビルド成果物の保管目的には使用しません。

## ローカル設定と秘密情報

- `.env` と `.env.*` は Git 管理しない
- 共有が必要なキー名だけを `.env.example` に記載する
- API キー、トークン、端末固有パスをソースや fixture に含めない
- Viewer から公開される値は秘密情報として扱えないことを前提にする

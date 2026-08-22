# バージョン方針

## Viewer

- Node.js: 22.18.0
- npm: 11.5.2
- Vite: 8.2.2
- TypeScript: 7.0.2

Node.js はローカル環境で利用可能な22系LTSへ固定しています。Vite 8 が要求する Node.js 22.12 以上を満たします。依存パッケージは `viewer/package-lock.json` で固定し、更新は意図した変更としてレビューします。

## Simulator

- Unity: 6000.5.9f1（Unity 6.5）
- PLATEAU SDK for Unity: 4.3.0

Unity は企画書の Unity 6.5 指定に合わせ、確認時点の 6.5 系パッチへ固定しています。PLATEAU SDK v4.3.0 の公開情報では Unity 6000.3.10f1 以上が推奨されていますが、6000.5.9f1 との実動作は Issue #4 で確認します。

PLATEAU SDK の取得元：

- https://github.com/Project-PLATEAU/PLATEAU-SDK-for-Unity/releases/tag/v4.3.0

Unity の取得元：

- https://unity.com/releases/editor/whats-new/6000.5.9f1

## 更新規則

1. ハッカソン中の自動メジャー更新は行わない
2. セキュリティ修正やブロッカー解消以外の更新は、デモ安定後に行う
3. Unity または PLATEAU SDK 更新時は、CityGML 読込・日陰判定・ビルドを再確認する
4. Viewer の依存更新時は、型チェックとプロダクションビルドを実行する

# Fixtures

Git で管理できる小規模なデモ・テストデータを配置します。

## フェーズA：Viewer プレビュー用

`environment-costs-phase-a.geojson` は、Issue #3 フェーズA向けのダミーデータです。
5本の架空道路と、次の2つの環境コストモードを収録しています。

- `shade`: 値が高いほど快適な「日陰」モード
- `inland-flood`: 値が高いほどリスクが大きい「内水」モード

各モードには表示名、説明、単位、値域、値の向き、色段階、サンプル KPI があり、
Viewer はこのファイルを直接読み込んで表示を切り替えます。すべての値は表示確認用の架空値であり、
実際の環境評価や避難判断には利用できません。

## フェーズB：正式データ契約 v1

`environment-cost-road-network-v1.json` は、JSON Schema と意味検証を通る正式契約の最小fixtureです。
4ノード、5有向辺、2時刻を収録し、次の状態を確認できます。

- 日陰率、日射曝露時間、内水想定浸水深の定義と値域
- `available`、`partial`、`missing` の各データ状態
- `null` を0と区別した欠測
- ViewerがCityGMLなしで契約JSONを直接表示できること

`invalid/environment-cost-road-network-v1-cases.json` は正常fixtureへ意図的な変更を適用する異常系テストです。
検証コマンドと詳細規則は
[`docs/environment-cost-data-contract-v1.md`](../../docs/environment-cost-data-contract-v1.md) を参照してください。

# データ領域

| 対象 | 用途 | Git管理 |
|---|---|---|
| `area-candidates.geojson` | 地域候補、役割、シミュレーション要否 | する |
| `fixtures/` | Viewer・テスト用の小さなダミーデータ | する |
| `raw/` | CityGML、道路、地形等の取得データ | 内容はしない |
| `generated/` | Unity 等が生成した環境コスト | 内容はしない |

各データには、取得元、版、ライセンス、CRS、対象範囲、取得・再生成手順を添えてください。

Issue #2の地域候補とシミュレーション要否は [`area-candidates.geojson`](area-candidates.geojson)、
入力データ、座標、日時、ライセンス、再取得手順は
[`docs/target-area-and-input-data.md`](../docs/target-area-and-input-data.md) を参照してください。

# 4地域 CityGML 取得検証結果

実行日: 2026-08-25
取得元: 国土交通省 PLATEAU データカタログの各マニフェスト記載URL

`tools/plateau-environment-cost-analyzer/prepare-citygml-datasets.ps1` を実行し、各ZIPについてカタログ掲載サイズの±1%以内であること、`tar -tf` による一覧読取、展開後の `udx/` ディレクトリを確認した結果です。ZIPおよび展開済みデータ本体は `data/raw/` にあり、Git管理外です。

| 地域 | データセット | 実ZIPバイト数 | SHA-256 | 展開先 |
| --- | --- | ---: | --- | --- |
| 京都 | 26100 京都市（2025） | 2,700,713,512 | `3ea8f10ac188b7042d151efdf29534f060196e523b1d66e8eb892e82a7ec293c` | `data/raw/plateau/26100-kyoto-2025` |
| 舞鶴 | 26202 舞鶴市（2025） | 914,222,089 | `13f4020ade066dc7139b7653c47a55a09af0093dee743f6b9cca5d3177a71cff` | `data/raw/plateau/26202-maizuru-2025` |
| 藤沢 | 14205 藤沢市（2025） | 736,037,983 | `7e85ff8e1642b9c2cc627f356acedbe792e95fac25febe2ee70c9312d6c415ea` | `data/raw/plateau/14205-fujisawa-2025` |
| 藤沢 | 14204 鎌倉市（2024） | 469,040,313 | `802aec587322a83846f00f039671deffd572df807bfd341558d6b1a97d4d9eff` | `data/raw/plateau/14204-kamakura-2024` |
| 藤沢 | 14100 横浜市（2024） | 2,777,093,929 | `e00c0edef51db6e967b6f45c0184c221b748868f55ad2bee66c83d306895fddc` | `data/raw/plateau/14100-yokohama-2024` |
| さいたま | 11100 さいたま市（2025） | 2,516,277,839 | `446eec6dd2448decde8de6019a5a3600ca61320600bc75c1392f6fe219d640f0` | `data/raw/plateau/11100-saitama-2025` |
| さいたま | 11219 上尾市（2025） | 196,710,482 | `67c091eb837ad5e6227531f52ee63894b13e94a69644feb371aadb3b208730f0` | `data/raw/plateau/11219-ageo-2025` |
| さいたま | 11203 川口市（2024） | 410,936,132 | `39ab950e13e2343ccfea3099884030c3462267470c5923432fe566ca43c7379e` | `data/raw/plateau/11203-kawaguchi-2024` |

データカタログの表示サイズと実配信サイズには数バイトの差がある場合があるため、サイズ一致だけを完全性判定に使わず、ZIP読取とSHA-256記録を組み合わせています。

## 続くUnity検証

この取得結果を使い、`docs/citygml-acquisition-and-unity-import.md` の手順で、データセットカタログ照合、半径4 kmのメッシュ選定、Inspection Sceneの読込、Building/Road Collider数、座標系を確認します。上尾市・川口市では6桁メッシュしか返らないため、8桁メッシュがある場所を優先しつつ6桁しかない場所は残す正規化処理を適用します。

## Unity Editorでの読込確認

2026-08-25にUnity 6000.3.18f1で市ヶ谷の既存入力を使ってInspection Sceneを再生成しました。`ENVIRONMENT_COST_INSPECTION_SCENE_READY` のログで、`Building colliders=183`、`Road colliders=238`を確認しています。Scene上では建物と道路が同一地点に表示され、`coordinateZoneId` をPLATEAU SDKの`GeoReference`へ渡す座標系設定が機能していることを確認しました。

一部の大きな三角形についてUnityの`MeshCollider`警告が表示されましたが、読込処理は継続し、上記の完了ログとCollider数を出力しました。4地域の各設定についても、同じInspection Scene手順を使って個別に確認します。

# 対象地区と入力データ

Issue #2で、後続の都市モデル読込、日陰計算、道路グラフ、経路探索が共通して使う入力条件を定めます。
調査結果は2026年8月23日時点です。外部データ本体はGitへ追加せず、ここに取得元と再取得手順を残します。

## 決定

| 項目 | 採用値 |
|---|---|
| 対象地区 | 東京都千代田区、東京駅丸の内側から和田倉噴水公園を含む範囲 |
| 境界 | 西139.7590、南35.6810、東139.7675、北35.6870 |
| 概算寸法・面積 | 東西0.768 km、南北0.667 km、0.512 km² |
| デモ起点 | 東京駅丸の内中央広場側、`[139.7653236, 35.6816170]` |
| デモ終点 | 和田倉噴水公園内園路、`[139.7608385, 35.6834690]` |
| 代表日 | 2025-08-01 |
| 時刻 | 08:00から17:00まで1時間間隔、両端を含む10スライス |
| タイムゾーン | `Asia/Tokyo`、UTC+09:00。夏時間は適用しない |
| 3D都市モデル | PLATEAU 千代田区2025年版、標準製品仕様書第5.0版 |
| 歩行道路ネットワーク | OpenStreetMap |
| シミュレーション平面座標 | JGD2011 / 平面直角座標系第IX系、EPSG:6677、メートル |
| Viewer交換座標 | RFC 7946 GeoJSON、OGC:CRS84、`[経度, 緯度]` |

境界と起終点の機械可読な正本は [`data/target-area.geojson`](../data/target-area.geojson) です。

### 選定理由

- Issue #10で作成したフェーズA fixtureと同じ東京駅周辺であり、Viewerから実データへの移行を確認しやすい。
- 駅前広場、街路、高層建築物、樹木、公園を含み、時間帯による日陰経路の差を説明しやすい。
- 約0.5 km²に抑えつつ、意味のある起終点間の歩行経路を確保できる。
- PLATEAU公式APIで、対象範囲に建築物、交通（道路）、地形、植生データがあることを確認できる。

## 起終点の接続確認

2026年8月23日にOpenStreetMap API 0.6から対象境界を取得し、次の条件で無向グラフを仮生成しました。

- `highway`タグを持つwayを候補とする。
- `motorway`、`motorway_link`、`trunk`、`trunk_link`、`construction`、`proposed`、`raceway`を除外する。
- `foot=no`または`access=no/private`を除外する。
- 起終点に最も近いグラフノード間を幅優先探索する。

確認時は1,035 way、2,666ノードが候補となり、起点のOSMノード`12615257648`から終点の
OSMノード`5500162103`まで46ノードの接続を確認しました。これはデータ選定時の接続確認であり、
通行方向、横断条件、階層、屋内・地下経路を含む正式な歩行可能判定はIssue #5で実装します。

## 入力データ

### PLATEAU 3D都市モデル

採用データセットは千代田区2025年版です。

| 項目 | 値 |
|---|---|
| 自治体コード | `13101` |
| データセット | `13101_chiyoda-ku_pref_2025_citygml_1_op.zip` |
| 整備・登録年度 | 2025 |
| PLATEAU仕様 | 5.0 |
| 配布zipサイズ | 2,107,411,300 bytes |
| 固定年度URL | `https://api.plateauview.mlit.go.jp/datacatalog/citygml/13101-2025/citygml.zip` |

対象境界に対する公式CityGML検索APIの応答では、次のファイルが候補になりました。

| 地物 | ファイル・LOD | 用途 | 扱い |
|---|---|---|---|
| 建築物 `bldg` | `53394610/11/20/21_bldg_6697_op.gml`、最大LOD2 | 建物による遮蔽 | 必須 |
| 交通（道路）`tran` | `53394610/11/20/21_tran_6697_op.gml`、最大LOD3 | 道路面の表示・位置照合 | 必須。ただし経路グラフには直接使わない |
| 地形 `dem` | `533946_dem_6697_op.gml`、LOD1 | 地表高と建物配置 | 必須 |
| 植生 `veg` | `53394611_veg_6697_op.gml`、最大LOD3 | 樹木による遮蔽 | 利用可能な範囲で使用 |
| 橋梁 `brid` | API検索結果に含まれる対象ファイル | 高架・橋梁周辺の補助形状 | 必要箇所だけ使用 |

植生モデルは対象境界全体を覆っていない可能性があります。植生データがない場所を「樹木なし」とは判定せず、
建物のみの日陰計算と樹木を含む計算を区別して結果メタデータへ記録します。

公式情報：

- [PLATEAU-CityGML APIの説明](https://docs.plateauview.mlit.go.jp/datasets/citygml/)
- [対象境界のCityGML検索API](https://api.plateauview.mlit.go.jp/datacatalog/citygml/r:139.7590,35.6810,139.7675,35.6870)
- [3D都市モデルの座標と高さ](https://www.mlit.go.jp/plateau/learning/tpc03-4/)

### 歩行道路ネットワーク

経路探索にはOpenStreetMapの`highway`ネットワークを使います。PLATEAUの交通モデルは道路面の
3D表現と位置照合に使いますが、歩道接続や歩行可否を持つルーティンググラフの正本にはしません。

取得時にOSM要素のID、version、timestampと抽出日時を保存します。OSMは継続更新されるため、再取得時に
差分が生じた場合はIssue #5のグラフ生成結果を再検証します。

公式情報：

- [OpenStreetMap API 0.6](https://api.openstreetmap.org/api/0.6/)
- [OpenStreetMap Copyright and License](https://www.openstreetmap.org/copyright)
- [OSMF Attribution Guidelines](https://osmfoundation.org/wiki/Licence/Attribution_Guidelines)

## 再取得手順

### ディレクトリ

```text
data/raw/
├── plateau/13101-chiyoda-2025/
└── osm/marunouchi-otemachi/
```

これらのディレクトリ内のデータ本体は`.gitignore`の対象です。ダウンロード日時、取得URL、サイズ、
SHA-256は各ローカルディレクトリの`manifest.txt`へ記録します。manifestも生データと同様にGit管理しません。

### PLATEAU

年度を`latest`にせず2025へ固定して取得します。

```bash
mkdir -p data/raw/plateau/13101-chiyoda-2025
curl --fail --location \
  'https://api.plateauview.mlit.go.jp/datacatalog/citygml/13101-2025/citygml.zip' \
  --output data/raw/plateau/13101-chiyoda-2025/13101_chiyoda-ku_pref_2025_citygml_1_op.zip
sha256sum data/raw/plateau/13101-chiyoda-2025/*.zip
```

全区zipが大きすぎる場合は、対象境界のCityGML検索APIから上表の個別GML URLを取得します。
PLATEAU配信APIは試験運用中のため、取得時にはURLとSHA-256を記録してください。Unityへの読込では、
CityGMLに同梱されるschemas、codelists、metadata、specificationも同じ年度のものを使用します。

### OpenStreetMap

対象が小さいため、API 0.6のmap取得を使います。

```bash
mkdir -p data/raw/osm/marunouchi-otemachi
curl --fail --location \
  'https://api.openstreetmap.org/api/0.6/map?bbox=139.7590,35.6810,139.7675,35.6870' \
  --output data/raw/osm/marunouchi-otemachi/map.osm
sha256sum data/raw/osm/marunouchi-otemachi/map.osm
```

大量または反復的な取得へ拡張する場合は、公開APIへ負荷をかけず、地域extractまたは管理された
Overpassインスタンスへ切り替えます。

## 座標とUnityへの変換

1. PLATEAU CityGMLはEPSG:6697（JGD2011の緯度・経度と東京湾平均海面基準の標高）として読む。
2. 水平位置をEPSG:6677（JGD2011 / 平面直角座標系第IX系）へdouble精度で投影する。
3. 対象範囲中心`[139.76325, 35.68400]`の投影座標と基準標高をUnityローカル原点とする。
4. Unityでは`X=東方向`、`Y=上方向`、`Z=北方向`とし、原点差し引き後にfloatへ変換する。
5. Viewerへ出力するGeoJSONはRFC 7946に従い、OGC:CRS84の`[経度, 緯度]`へ戻す。

PLATEAUとOSMは測地系の表現が異なるため、文字列の付け替えではなく座標変換ライブラリを使います。
既知の起終点を変換して往復誤差を測り、許容誤差はIssue #4と#5で決定します。高さはPLATEAUの標高を使い、
GPSの楕円体高と混在させません。

## 日時条件

- 代表日は夏季の暑熱・日陰デモとして`2025-08-01`に固定する。
- 時刻は`08:00`から`17:00`までの正時を1時間刻みで使用する。
- データ契約では`Asia/Tokyo`とUTCオフセット`+09:00`を併記する。
- 太陽位置計算は各スライスの瞬間値とし、区間集計方法はIssue #8で定義する。
- 任意日時UIへ拡張しても、この10スライスを回帰試験の固定条件として残す。

## 利用条件と帰属

### PLATEAU

3D都市モデルの著作権は地方公共団体に帰属し、PLATEAUのサイトポリシーに記載された公共データ利用規約、
CC BY 4.0、ODC BY、ODbLの選択肢に従います。表示例は次のとおりです。

```text
この成果物は、国土交通省Project PLATEAUが提供する東京都千代田区3D都市モデル（2025年度）を利用して作成しました。
```

[PLATEAUサイトポリシー](https://www.mlit.go.jp/plateau/site-policy/)と、ダウンロード物に同梱される
利用条件を公開前に再確認します。OSM由来データと結合してデータベースとして公開する場合は、PLATEAU側も
ODbLの選択肢で利用し、結合成果物のライセンスをIssue #9で明記します。

### OpenStreetMap

OSMデータはODbL 1.0です。Viewerには次の帰属を常時表示し、`OpenStreetMap`から
`https://www.openstreetmap.org/copyright`へリンクします。

```text
© OpenStreetMap contributors
```

道路グラフまたはその派生データベースを公開する場合は、ODbL本文へのリンク、利用データの範囲、
取得日、派生データの入手方法も添えます。

## 手動補正方針

- rawのCityGMLとOSM XMLは直接編集しない。
- 歩道の欠落、誤接続、横断不可などは、将来追加する`data/road-network-overrides.geojson`へ差分として記録する。
- 補正には安定ID、対象OSM ID、操作種別、理由、根拠URLまたは現地確認方法、作成日、確認者を持たせる。
- 自動生成グラフへ補正を適用した後、ID一意性、参照整合性、起終点接続性を再検証する。
- OSM自体へ反映できる修正はOSMの編集方針に従って別途行い、プロジェクト固有の推測を投稿しない。

## 後続Issueへの引き継ぎ

- Issue #3: 境界、CRS、タイムゾーン、代表日、時間スライスを正式データ契約v1へ反映する。
- Issue #4: 千代田区2025年版をPLATEAU SDKで読み込み、EPSG:6677とローカル原点の変換を検証する。
- Issue #5: OSMから歩行グラフを生成し、正式な通行可否と手動補正を実装する。
- Issue #6〜#8: 固定した日時条件で太陽位置、日陰判定、時間帯別集計を実装する。
- Issue #9: PLATEAUとOSMの出典、取得日、ライセンスを出力メタデータへ含める。

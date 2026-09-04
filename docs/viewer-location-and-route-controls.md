# 地域・現在位置・起終点・日時指定UI

Issue #12でViewerの検索条件UIを実操作へ接続し、Issue #13で3経路の描画とKPI比較を実計算へ接続しました。

## 地域とデータ状態

地域セレクターは京都市、舞鶴市、藤沢市、さいたま市、市ヶ谷周辺を表示し、選択時に各中心点から半径4 kmの初期範囲へ地図を移動します。地域変更時には起終点、処理中のリクエスト、以前の経路状態を破棄します。

5地域すべてに、v2歩行ネットワークと`2025-08-01`の0時から23時までのbaseline解析結果を登録しています。Viewerは、経路サーバーが読み込んだ時刻を「計算済み日時」から選択し、地域を問わず単独baselineの`POST /api/v1/routes`と`GET /api/v1/road-edges`で経路・道路別日陰率を表示します。サーバーを`ROUTE_TIMESTAMPS=2025-08-01T12:00:00+09:00`で起動した場合は12時だけを選択してください。全24時刻を選んで利用するには、この資料後半の全時間帯起動手順でサーバーを再起動します。

今回の5地域閲覧では、市ヶ谷の旧`ichigaya-demo-shade`施策バンドルは使用しません。v2歩行ネットワークと互換な施策後バンドルが未生成であるため、A/B比較は対象外です。

### 5地域v2をローカルで閲覧する起動例

リポジトリ直下のPowerShellで、まず経路サーバーを起動します。初回確認ではメモリ使用量を抑えるため、12:00の1時刻だけを読み込みます。

```powershell
$env:HOST = '127.0.0.1'
$env:PORT = '3102'
$env:ROUTE_CORS_ORIGIN = 'http://localhost:5173'
$env:ROUTE_TIMESTAMPS = '2025-08-01T12:00:00+09:00'
$env:ROUTE_BUNDLE_MANIFESTS = @(
  (Resolve-Path 'data/generated/ichigaya-venue-environment-cost-server-bundle-v2/manifest.json').Path,
  (Resolve-Path 'data/generated/kyoto-environment-cost-server-bundle-v2/manifest.json').Path,
  (Resolve-Path 'data/generated/maizuru-environment-cost-server-bundle-v2/manifest.json').Path,
  (Resolve-Path 'data/generated/fujisawa-environment-cost-server-bundle-v2/manifest.json').Path,
  (Resolve-Path 'data/generated/saitama-environment-cost-server-bundle-v2/manifest.json').Path
) -join ','
npm --prefix server start
```

別のPowerShellでViewerを起動し、`http://localhost:5173/`を開きます。

```powershell
$env:VITE_ROUTE_API_URL = 'http://127.0.0.1:3102/api/v1/routes'
$env:VITE_ROAD_EDGE_API_URL = 'http://127.0.0.1:3102/api/v1/road-edges'
npm --prefix viewer run dev -- --host localhost --port 5173 --strictPort
```

環境コストfixtureの`areaId`と選択地域が一致しない場合、その道路形状は地図へ描画しません。現在の小型fixtureは`tokyo-demo`（東京駅周辺）なので、5つのシミュレーション地域では非表示です。これにより、架空の直線を市ヶ谷の実道路または検索経路と誤認することを防ぎます。

| 状態 | Viewerの扱い |
|---|---|
| `available` | 計算済み日時と経路検索を有効化 |
| `not-precomputed` | 固定地域として地図を表示し、検索を無効化 |
| `outside-coverage` | GPS位置へ移動し、結果なしを正常状態として案内 |
| `load-error` | GPSまたは経路データを取得できない理由を案内 |

## 現在位置

「現在位置へ移動」を押した時だけ`navigator.geolocation.getCurrentPosition`を呼び出します。成功時は現在位置マーカーと測位精度円を表示し、固定地域の半径4 km内なら対応地域を選択します。許可拒否、取得不可、タイムアウトは別のメッセージです。

GPS座標はメモリ上の地図表示と範囲判定だけに使います。保存せず、出発地へ自動設定せず、経路サーバーへも送信しません。Geolocation APIを利用するため、本番ViewerはHTTPSで配信してください（localhostは開発用途として利用可能です）。

## 起終点と経路API

「出発地を指定」「目的地を指定」で次の地図クリックの設定先を切り替えます。個別解除、入れ替え、全リセットが可能です。両地点と計算済み日時が揃うと、条件変更ごとに次のリクエストを送ります。

```http
POST /api/v1/routes
Content-Type: application/json
```

クリック座標は経路サーバーの同一規則で道路ノードへスナップされ、応答後のマーカーをスナップ座標へ更新します。`SNAP_NOT_FOUND`と`OUTSIDE_COVERAGE`は検索せず日本語で理由を表示します。処理中に条件が変わった場合はリクエスト番号で古い応答を無視します。

APIを別オリジンで稼働する場合は、ビルド時に`VITE_ROUTE_API_URL`を指定します。同一オリジン構成では`<VIEWER_BASE_PATH>api/v1/routes`が既定値です。例えば公開URLが`/environment-cost-route-finder/`なら、API URLは`/environment-cost-route-finder/api/v1/routes`になります。

## 実日陰道路とコスト根拠

日陰モードで実データのある地域を選び、地図をズーム14.5以上へ拡大すると、Viewerは`GET /api/v1/road-edges`へ現在のbbox、地域、日時、日射回避係数を送り、表示範囲に交差する道路辺だけを取得します。全道路ネットワークや全時刻の解析値はブラウザへ配信しません。道路が上限を超える場合は部分データを誤表示せず、「地図を拡大してください」と案内します。

道路色は探索コストではなく#9の`shadeRatio`です。日陰率75%以上を緑、25〜75%を黄、25%未満を橙の3段階（補間なし）、欠測を灰色の破線で表示します。`partial`は解析値の色を使い、道路詳細で一部欠測と未照合点数を示します。

「道路詳細を確認」を選び、色付き道路をクリックすると次を表示します。

- 道路辺ID、選択日時、解析状態、日陰率、日射曝露時間、解析点数
- 歩行時間、日射回避係数、環境コスト加算分、最終探索コストと計算式
- 欠測理由と、探索時だけ全日向として扱う仮定
- 最短・バランス・日陰優先経路のどれに含まれるか

経路カードを選ぶと、表示範囲内でその経路に含まれる道路辺を経路プロファイルの色で強調します。移動経路には明るい紫の縁を付け、白い縁を持つ解析対象道路と区別します。日時または係数を変更した瞬間に古い道路辺応答と詳細を消去し、新しい応答が返るまで再利用しません。経路KPIと道路辺詳細は同じサーバーバンドルを正本とし、fixtureテストでは構成辺の歩行時間、探索用日射曝露時間、最終コストの合計が経路KPIと一致することを確認します。

`VITE_ROAD_EDGE_API_URL`を省略した場合は、`VITE_ROUTE_API_URL`の末尾`routes`を`road-edges`へ置換したURLを使用します。両APIが隣接しない構成の場合だけ明示指定します。

### 道路解析値の通信量抑制

Viewerはサーバーバンドルをブラウザへ一括配信せず、次の条件で道路解析値の通信を抑えます。

- ズーム14.5未満では`road-edges` APIを呼び出さず、道路解析値を0 byteにする
- 日陰モードかつ解析時刻のある地域でのみ取得する
- 地図操作中には連続取得せず、`moveend`（移動・拡大縮小の完了）ごとに1回取得する
- リクエストには画面内のbbox、1地域、1時刻、1係数だけを指定し、サーバーもbboxと交差する物理道路辺だけを返す
- サーバーでは0.005度単位の空間グリッド索引を使い、全130,508物理辺を毎回応答へ展開しない
- 既定上限の10,000辺を超える範囲は部分応答を返さず、HTTP 422 `TOO_MANY_ROAD_EDGES`で拡大を促す。bbox自体も緯度・経度それぞれ最大0.2度に制限する
- 地域、日時、係数または表示範囲が変わった後に古い応答が到着しても、要求シーケンス番号が一致しなければ画面へ反映しない（これは表示整合性の対策であり、すでに転送されたbyte数は減らさない）

辺数上限は経路サーバーの`ROUTE_MAXIMUM_ROAD_EDGE_FEATURES`で変更できます。これは通信byte数の直接上限ではなく、GeoJSON Feature数の上限です。形状やプロパティの長さで1辺当たりの容量が変わるため、厳密な最大byte数を保証するものではありません。

市ヶ谷の実バンドル（130,508物理辺、2025-08-01 12:00、係数2、中心`139.736043,35.69047`）を使い、正方形bboxで`JSON.stringify`後の容量を計測した結果は次のとおりです。gzip値は同じJSONをgzip圧縮した参考値であり、実通信でこの値にするにはnginxなどでJSONのgzip圧縮を有効にする必要があります。

| bboxの緯度・経度幅 | 応答辺数 | 非圧縮JSON | gzip参考値 | 結果 |
|---:|---:|---:|---:|---|
| 0.005度 | 389辺 | 240,586 bytes（約235 KiB） | 29,759 bytes（約29 KiB） | HTTP 200相当 |
| 0.010度 | 1,386辺 | 859,102 bytes（約839 KiB） | 102,610 bytes（約100 KiB） | HTTP 200相当 |
| 0.020度 | 6,634辺 | 4,120,013 bytes（約3.93 MiB） | 486,244 bytes（約475 KiB） | HTTP 200相当 |
| 0.040度 | 33,192辺 | 応答本体を生成しない | 小さなエラー応答のみ | HTTP 422相当（10,000辺上限） |

したがって、現在の実装は通常の拡大表示では非圧縮約0.24〜4.12 MB（gzip有効時の参考値約30〜486 KB）程度に収め、広域表示では道路データ本体を送らない設計です。画面サイズと道路密度によってbbox内の辺数は変わるため、この範囲は市ヶ谷での実測値であり全地域の保証値ではありません。市ヶ谷バンドル全体12ファイルの約54.28 MiBをブラウザへ配信する方式とは異なります。

## 3経路とKPI比較

日陰モードでは、日陰優先度を0〜4（0.25刻み）で指定します。Viewerは次の3プロファイルを明示してAPIへ送り、既定値2ではサーバー既定の係数0、0.5、2と一致します。

| 表示 | APIプロファイル | 日射回避係数 |
|---|---|---:|
| 最短経路 | `shortest` | 0 |
| バランス | `balanced` | 日陰優先度の1/4 |
| 日陰優先 | `shade` | 日陰優先度 |

応答のGeoJSON `LineString`を赤、紫、青で描画し、バランス型の移動経路には凡例と同じ紫を使用します。候補経路と選択中の経路には共通の明るい紫の縁を付け、白い縁を持つ解析対象道路と区別します。選択中の経路はプロファイル色を維持したまま最前面に強調します。各カードのチェックボックスで経路ごとの表示を切り替えられます。カードには距離、推定所要時間、観測済み区間の日陰率、日向時間、最短経路との差、不明な歩行時間、解析値の充足状態を表示します。距離は1 m、時間は1秒、日陰率は1パーセントポイント単位で丸めます。

選択経路について「追加○分で日向時間を○分削減」を表示します。同じ道路列が複数プロファイルで選ばれた場合は、係数が異なっても当該条件では最適経路が一致した旨を表示し、描画不良と誤認させません。実計算後は小型fixtureの架空KPIと注意書きを隠し、実計算結果であることを明示します。

係数、地域、日時、起終点、コストモードの変更時は表示中の経路を無効化します。日陰モードかつ計算可能な市ヶ谷で両地点が指定されている場合は、変更後の3経路を再計算します。内水モードの実経路計算はMVP対象外です。

## 確認

```powershell
cd viewer
npm ci
npm run typecheck
npm run build
npm run test:route-display
npm run test:road-edge-display
```

ブラウザでは、5地域の選択、計算済み日時の選択、地図クリック後の起終点切替、解除・入替・リセットに加え、3経路の色分け・選択・表示切替、KPI、同一路線の説明、日陰優先度変更後の再計算を確認します。さらに地図を拡大して日陰・日向・欠測の凡例、道路クリック詳細、選択経路の構成辺強調、日時・係数変更直後の旧表示消去を確認します。GPSの権限結果はHTTPSまたはlocalhost上の実機ブラウザで確認します。

## 5地域v2バンドルの全時間帯読み込みとメモリ目安

5地域のv2バンドル（市ヶ谷・京都・舞鶴・藤沢・さいたま）を、0時から23時までの全24時間帯で読み込む場合は、リポジトリ直下で次を実行します。`ROUTE_TIMESTAMPS` を設定しないことで、各マニフェストの全コストスライスが読み込まれます。

```powershell
$env:HOST = '127.0.0.1'
$env:PORT = '3102'
$env:ROUTE_CORS_ORIGIN = 'http://localhost:5173'
$env:NODE_OPTIONS = '--max-old-space-size=4096'
$env:ROUTE_BUNDLE_MANIFESTS = @(
  (Resolve-Path 'data/generated/ichigaya-venue-environment-cost-server-bundle-v2/manifest.json').Path,
  (Resolve-Path 'data/generated/kyoto-environment-cost-server-bundle-v2/manifest.json').Path,
  (Resolve-Path 'data/generated/maizuru-environment-cost-server-bundle-v2/manifest.json').Path,
  (Resolve-Path 'data/generated/fujisawa-environment-cost-server-bundle-v2/manifest.json').Path,
  (Resolve-Path 'data/generated/saitama-environment-cost-server-bundle-v2/manifest.json').Path
) -join ','
Remove-Item Env:ROUTE_TIMESTAMPS -ErrorAction SilentlyContinue
Remove-Item Env:ROUTE_SCENARIO_BUNDLES -ErrorAction SilentlyContinue
npm --prefix server start
```

メモリを抑えて動作確認する場合は、次の2行を設定すると5地域の12時だけを読み込みます。

```powershell
$env:NODE_OPTIONS = '--max-old-space-size=2048'
$env:ROUTE_TIMESTAMPS = '2025-08-01T12:00:00+09:00'
```

2026年9月時点の実ファイル計測では、5地域合計は次のとおりです。

| 入力 | サイズ（ディスク上） |
|---|---:|
| `topology.json` 5地域 | 約185.4 MiB |
| 12時のコストスライス×5地域 | 約11.4 MiB |
| 24個のコストスライス×5地域 | 約225.0 MiB |
| topology＋12時slice（12時起動の入力） | 約196.8 MiB |
| topology＋全24時slice（全時間帯起動の入力） | 約410.5 MiB |

これはファイルサイズであり、Node.jsの実RSSを測定した値ではありません。JSONの解析後はオブジェクト、文字列、インデックス等のオーバーヘッドが加わるため、起動時の目安として12時のみは--max-old-space-size=2048（2 GiB）、全24時間帯は--max-old-space-size=4096（4 GiB）を推奨します。OSやViewerの分も含めて同量以上の空きメモリを確保してください。実際のRSSはNode.jsのバージョン、同時に保持するリクエスト、GCのタイミングで変動します。

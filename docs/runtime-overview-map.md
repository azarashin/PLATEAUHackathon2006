# Runtime俯瞰地図

Runtime Inspection Sceneでは、右上の「俯瞰地図を表示」から、メインカメラの真上を中心とする北上固定の俯瞰地図を表示できる。

- 地図はメインカメラのX/Z位置へ追従する。回転は追従せず、常に北が上である。
- 青緑の三角形は現在地（地図中心）とメインカメラの向きを示す。三角形の上がカメラの正面である。
- 表示範囲はメインカメラの高度に連動する。低いほど狭い範囲（拡大）、高いほど広い範囲（縮小）となる。スライダーと表示範囲ラベルは表示しない。
- 高度の有効範囲は、Inspection Scene内のRenderer boundsの最低Y座標を地表相当として求める。メインカメラのnear clip planeをその下限からの最小高度、地表相当Yに都市パッケージの`radiusMeters`を足した値を上限とする。下限では半径は最大200 m（小さいパッケージではパッケージ半径）、上限では`radiusMeters`そのままとし、その間を線形対応させる。範囲外の高度は最小または最大範囲に固定する。
- 表示はOrthographic Cameraと512×512 RenderTextureで行う。Building/Road/Terrainレイヤーだけを描画する専用Culling Maskを使う。そのためUI、選択マーカー、経路・道路別ヒートマップなどの一時オーバーレイは含まれない。
- メインカメラの移動中は最大毎秒5回、静止中は最大毎秒1回だけ更新する。通常のメイン描画を増やさない。
- 俯瞰地図は表示専用である。地図上のクリック、ドラッグ、右クリックはカメラ回転・施策配置・経路始終点選択へ渡さない。カメラの上昇・下降（E/Q）はそのまま使え、俯瞰地図の縮尺だけを更新する。

## 地名ラベル

都市データパッケージの検証が完了した後、`manifest.json` のinventoryで宣言され、整合性検証済みの `place-labels.json` と `place-label-report.json` をRuntimeが読み込む。地名は3D空間へ生成せず、俯瞰地図の画像レイヤーにだけ表示する。

- `place-labels.json` の座標は既存のRuntime道路・施策と同じPLATEAU SDKの `GeoReference` 契約でローカルX/Zへ変換する。北が上の画像へ投影するため、地名の座標系を推測したり、Sceneメッシュから地名を取得したりしない。
- 地図の矩形外にあるラベルは表示しない。各縮尺で優先度と件数を絞り、ラベル同士と現在地マーカーが重なる候補を除外する。半径160 m以下では優先度60以上を最大12件、160–350 mでは70以上を最大10件、350 m超では80以上を最大8件とする。
- 「地名を表示／非表示」で切り替えられる。パッケージが地名スキーマ未対応、または抽出結果が不足している場合は無効にし、同じパネルに理由を表示する。
- ステータスには抽出件数、最初の取得元（例: PLATEAU・年度）とソース版を表示する。地名が0件の場合は、CityGML未配置・取得台帳なし・読込エラー・抽出対象なしなどの出力済みreason codeを日本語で表示する。

地名ラベルは表示補助であり、日陰解析、経路コスト、道路ネットワーク、都市データパッケージの検証結果を変更しない。

### パッケージの選択と読込状態

Inspection Sceneを新規生成する際は、`RuntimeCityPackageConfig.packageRelativePath` を `EnvironmentCostRuntimeCityPackageLoader` へ明示的に渡す。たとえば市ヶ谷v2では `EnvironmentCostCities/ichigaya-venue-sidewalk-v2` をそのまま読む。`EnvironmentCostCities/<areaId>` を補完するのは、都市パッケージ設定を渡さずに生成した旧Sceneだけである。バージョン名やディレクトリ名をRuntime側で探索・自動選択しない。

既存Sceneに保存済みのLoader設定は書き換わらないため、地名表示を含むv2パッケージを使う場合は、対象の`-runtimeCityPackageConfig`を指定してInspection Sceneを再生成する。UI生成より先に読込状態が確定しても、ステータスを保持して「読込み待ち」のまま残さない。Loaderは待機側からも一度だけ読込開始できるため、`NotStarted`の無限待機をしない。

### 高度連動縮尺の上限

都市パッケージの範囲は `data/analysis-configs/<areaId>.json` の `radiusMeters` で変更する。この設定値はInspection Scene作成時に `EnvironmentCostInspectionMetadata.radiusMeters` へコピーされ、都市パッケージ作成時にはmanifestの `radiusMeters` にもコピーされる。Runtime loaderは両者の不一致をエラーにするため、高度連動縮尺の上限は、実際に読み込んだ都市パッケージの範囲と一致する。

範囲を変更する場合は、対象のanalysis configの `radiusMeters` を変更してからInspection Sceneと都市パッケージを再生成する。Runtime中に俯瞰地図を操作するUIはなく、メインカメラの高さだけが一時的な表示範囲を変更する。分析範囲・都市パッケージ・メインカメラ位置を変更しない。地図はカメラ位置を中心にするため、カメラが分析半径の外側へ移動した場合や正方形の四隅では、読み込み済みデータの範囲外を空白として含む場合がある。この上限は分析範囲を保証・変更するものではなく、表示上の制約である。

## 描画コストと更新頻度の確認手順

1. Unity Editorで対象のRuntime Inspection Sceneを開き、Playを開始する。右上で俯瞰地図を表示する。
2. **Window > Analysis > Profiler** を開き、CPU Usageモジュールで `EnvironmentCost.OverviewMap.Render` を検索する。選択すると、手動描画呼出しのCPU時間と発生回数を確認できる。
3. GPU時間も確認する場合は、ProfilerのGPU Usageモジュールを有効にし、対象Player/EditorでGPU profilingを有効にする。RenderTextureへの俯瞰地図描画に対応するCamera.Render/Render Camera項目を展開し、`EnvironmentCost.OverviewMap.Render` の発生時刻と照合する。GPU Usageが利用できない環境では、Frame Debuggerで俯瞰地図カメラのRenderTexture描画の有無だけを確認する（Frame DebuggerはGPU時間の計測手段ではない）。
4. カメラを連続移動し、Profiler TimelineまたはCPU Usageの同マーカーが0.2秒以上の間隔（最大5 fps）で発生することを確認する。停止後は1秒以上の間隔（最大1 fps）へ切り替わることを確認する。
5. E/Qでカメラを上昇・下降させ、高いときに縮小、低いときに拡大すること、建物・道路の縮尺と青緑の現在地・向きマーカーが維持されることを確認する。地図上で右クリックやドラッグをしても、メインカメラが回転・移動しないことを確認する。

## 自己テスト

`HourlyEnvironmentCostSelfTests.Run` は俯瞰地図用Culling Mask、更新間隔、都市パッケージ半径に応じた縮尺の最小・最大・クランプ、カメラ高度から縮尺への線形対応、北上マーカーの90度回転に加え、縮尺ごとの地名優先度・件数、地図矩形への投影と範囲外クリッピングを確認する。

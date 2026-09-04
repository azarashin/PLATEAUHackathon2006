# Runtime俯瞰地図

Runtime Inspection Sceneでは、右上の「俯瞰地図を表示」から、メインカメラの真上を中心とする北上固定の俯瞰地図を表示できる。

- 地図はメインカメラのX/Z位置へ追従する。回転は追従せず、常に北が上である。
- 青緑の三角形は現在地（地図中心）とメインカメラの向きを示す。三角形の上がカメラの正面である。
- 「表示範囲」スライダーは地図の半径を変更する。ラベルには半径と、画面に表示する正方形の幅・高さを表示する。
- スライダーの最小値・最大値・初期値は都市パッケージの`radiusMeters`から決める。初期値と最大値はその値、最小値はその範囲内で最大200 m（小さいパッケージではパッケージ半径）である。半径は丸めず、そのまま表示範囲の上限にする。
- 表示はOrthographic Cameraと512×512 RenderTextureで行う。Building/Road/Terrainレイヤーだけを描画する専用Culling Maskを使う。そのためUI、選択マーカー、経路・道路別ヒートマップなどの一時オーバーレイは含まれない。
- メインカメラの移動中は最大毎秒5回、静止中は最大毎秒1回だけ更新する。通常のメイン描画を増やさない。
- 俯瞰地図は表示専用である。地図・スライダー上のクリック、ドラッグ、右クリックはカメラ回転・施策配置・経路始終点選択へ渡さない。スライダーはキーボードフォーカスを取らないため、W/A/S/D等のカメラ移動とも競合しない。

地名の抽出とラベル表示はIssue #87/#88の対象であり、この地図にはまだ含めない。

### 初期縮尺・上限の設定

都市パッケージの範囲は `data/analysis-configs/<areaId>.json` の `radiusMeters` で変更する。この設定値はInspection Scene作成時に `EnvironmentCostInspectionMetadata.radiusMeters` へコピーされ、都市パッケージ作成時にはmanifestの `radiusMeters` にもコピーされる。Runtime loaderは両者の不一致をエラーにするため、縮尺スライダーの初期値・上限は、実際に読み込んだ都市パッケージの範囲と一致する。

範囲を変更する場合は、対象のanalysis configの `radiusMeters` を変更してからInspection Sceneと都市パッケージを再生成する。Runtime中のスライダー値は一時的な表示範囲だけを変更し、分析範囲・都市パッケージ・メインカメラ位置を変更しない。地図はカメラ位置を中心にするため、カメラが分析半径の外側へ移動した場合や正方形の四隅では、読み込み済みデータの範囲外を空白として含む場合がある。この上限は分析範囲を保証・変更するものではなく、表示上の制約である。

## 描画コストと更新頻度の確認手順

1. Unity Editorで対象のRuntime Inspection Sceneを開き、Playを開始する。右上で俯瞰地図を表示する。
2. **Window > Analysis > Profiler** を開き、CPU Usageモジュールで `EnvironmentCost.OverviewMap.Render` を検索する。選択すると、手動描画呼出しのCPU時間と発生回数を確認できる。
3. GPU時間も確認する場合は、ProfilerのGPU Usageモジュールを有効にし、対象Player/EditorでGPU profilingを有効にする。RenderTextureへの俯瞰地図描画に対応するCamera.Render/Render Camera項目を展開し、`EnvironmentCost.OverviewMap.Render` の発生時刻と照合する。GPU Usageが利用できない環境では、Frame Debuggerで俯瞰地図カメラのRenderTexture描画の有無だけを確認する（Frame DebuggerはGPU時間の計測手段ではない）。
4. カメラを連続移動し、Profiler TimelineまたはCPU Usageの同マーカーが0.2秒以上の間隔（最大5 fps）で発生することを確認する。停止後は1秒以上の間隔（最大1 fps）へ切り替わることを確認する。
5. スライダーを最小・最大へ動かし、表示範囲ラベル、建物・道路の縮尺、青緑の現在地・向きマーカーを確認する。地図上で右クリックやスライダーのドラッグをしても、メインカメラが回転・移動しないことを確認する。

## 自己テスト

`HourlyEnvironmentCostSelfTests.Run` は俯瞰地図用Culling Mask、更新間隔、都市パッケージ半径に応じた縮尺の最小・最大・クランプ、北上マーカーの90度回転を確認する。

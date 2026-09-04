# Runtime俯瞰地図

Runtime Inspection Sceneでは、右上の「俯瞰地図を表示」から、メインカメラの真上を中心とする北上固定の俯瞰地図を表示できる。

- メインカメラのX/Z位置へ追従する。視線の向きは引き継がず、常に北が上である。
- 表示はOrthographic Cameraと512×512 RenderTextureで行う。
- メインカメラの移動中は最大毎秒5回、静止中は最大毎秒1回だけ更新する。通常のメイン描画を増やさない。
- Building/Road/Terrainレイヤーだけを描画する専用Culling Maskを使う。そのためUI、選択マーカー、経路・道路別ヒートマップなどの一時オーバーレイは含まれない。
- 俯瞰地図は表示専用である。地図上のクリックはカメラ回転・施策配置・経路始終点選択へ渡さない。

初期縮尺はInspection Sceneの解析半径から決める。利用者が縮尺を変更するUIはIssue #86で追加する。地名抽出とラベル表示はIssue #87/#88の対象であり、この段階では含めない。

## 検証

`HourlyEnvironmentCostSelfTests.Run` は俯瞰地図用Culling MaskがBuilding/Road/Terrainだけを維持すること、および更新間隔を確認する。Profilerでは `EnvironmentCost.OverviewMap.Render` を確認できる。実機ではRuntime Inspection Sceneを再生し、メインカメラを移動して地図中心が追従すること、右クリックを地図上で行っても視線が回転しないことを確認する。

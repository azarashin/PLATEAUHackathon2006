# 太陽位置計算と3D影表示

Issue #6では、UnityのInspection Sceneで日時・地域に応じてDirectional Lightと建物影を更新する。これはデモの視覚確認用であり、保存済みの時間別環境コストJSONを変更しない。

## 計算の定義

`HourlyEnvironmentCostRules.CalculateSun`が、バッチ解析・Heatmap・実行時Sceneで共通に使用する。計算式は[NOAA General Solar Position Calculations](https://gml.noaa.gov/grad/solcalc/solareqns.PDF)の近似式である。

- 入力座標は `latitude, longitude` の順で、北・東を正のWGS84度とする。分析設定ファイルの`center`は`[longitude, latitude]`のため、呼出し時に並び順を明示的に変換する。
- 日付と時刻は地域設定の現地民生時刻として扱う。市ヶ谷は`Asia/Tokyo`（Windowsでは`Tokyo Standard Time`へ解決）であり、JSTはUTC+09:00・夏時間なしである。他地域へ展開する場合は設定のIANA名またはOSの対応名を指定し、その日のUTCオフセットを用いる。
- 方位角は真北を0°、時計回り（東90°、南180°）。高度は地平線を0°、上方を正とする。Unityの太陽ベクトルは地面から太陽へ向かう方向なので、Directional Lightのforwardにはその反対ベクトルを設定する。
- 大気差補正はしない。建物による幾何学的な遮蔽判定と同じ、屈折補正なしの太陽高度を用いる。

## 既知値による確認

既存のUnityバッチ自己テスト（`-executeMethod HourlyEnvironmentCostSelfTests.Run`）には、NOAA式から得た市ヶ谷中心（北緯35.690470°、東経139.736043°）・2025-08-01・JSTの参照値を含める。許容差は方位・高度とも0.05°である。

| JST | 高度 | 方位 |
| --- | ---: | ---: |
| 08:00 | 37.166051° | 93.458541° |
| 12:00 | 72.317290° | 189.768435° |

これは方位・高度の定義がNOAAと同じであること、およびタイムゾーン・東経の符号を取り違えていないことを確認するための回帰値である。

## Inspection Sceneでの操作

1. `PLATEAU > Environment Cost > Create Inspection Scene`から、対象地域の分析設定を選択してSceneを再生成する。
2. Playを開始する。左上の「太陽・影の確認」で日付を`YYYY-MM-DD`形式で入力し、08:00〜17:00のスライダーを連続操作する。
3. 方位・高度の表示と建物影が同時に変化することを確認する。Scene操作は右ドラッグ、移動はWASD/Q/Eで行える。

高度が0°以下の場合はDirectional Lightを無効にし、UIに夜間であることを表示する。夜間を「建物影による日陰」として扱わず、解析出力では従来どおり`status: missing`、`exclusionReason: sun-below-horizon`となる。

## 影の可視化範囲

Inspection Sceneのリアルタイム影は、実行時にカメラから**約250m**まで描画する。4km半径のCityGML全域へ高解像度のリアルタイム影を描画すると、影の解像度低下とGPU負荷が大きくなるためである。画面上のUIにも現在の可視化範囲を表示する。

- この制限は、太陽方位の変化と建物影の移動を目視確認するためのレンダリング上の制約である。建物に近づいてスライダーを操作して確認する。
- 遠方の影が表示されないことは、環境コスト解析の欠測・日向判定を意味しない。解析結果は道路面から建物へ行うレイキャストで算出し、カメラ距離の影響を受けない。
- より広い表示範囲が必要な場合は`EnvironmentCostSolarController`の`shadowDistanceMeters`を増やせるが、デモ用Playerの負荷と影品質を実機で確認して決める。

## 実装箇所

- `tools/plateau-environment-cost-analyzer/Assets/HourlyEnvironmentCostRules.cs`: 共通の太陽位置計算。
- `tools/plateau-environment-cost-analyzer/Assets/EnvironmentCostSolarController.cs`: 実行時UI、Directional Light更新、夜間無効化。
- `tools/plateau-environment-cost-analyzer/Assets/Editor/EnvironmentCostInspectionSceneBuilder.cs`: Scene生成時の参照設定。

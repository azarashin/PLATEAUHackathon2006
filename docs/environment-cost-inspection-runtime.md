# 環境コスト Inspection Scene のDEM・影・実行時確認

Issue #4後半で追加した、CityGML検証用Sceneの地形、影、実行時カメラ、Windowsビルドの手順です。対象となる生成Sceneは `tools/plateau-environment-cost-analyzer/Assets/Scenes/EnvironmentCostInspection/<areaId>.unity` であり、CityGML本体と同様にローカル生成物としてGit管理しません。地域ごとに別ファイルを生成し、同じ地域を再生成する場合だけ既存Sceneを置換します。

## Sceneの構成

**PLATEAU > Environment Cost > Create Inspection Scene** は、対象設定とメッシュ被覆レポートを基に、利用可能なLODのうちLOD1以下で次を読み込みます。

| CityGMLパッケージ | 用途 | Unity layer | 影 |
| --- | --- | --- | --- |
| `bldg` | 建物による遮蔽 | `Building`（8） | 投影・受光する |
| `tran` | 道路面と位置照合 | `Road`（9） | 受光のみ |
| `dem` | 地表高・地形確認 | `Terrain`（10） | 投影・受光する |

生成時に `Environment Cost Inspection Sun`（Directional Light、Soft Shadows）と `Environment Cost Runtime Camera` をSceneへ追加します。カメラには `EnvironmentCostInspectionFlyCamera` が付き、再生時にWASDで水平移動、Q/Eで上下移動、Shiftで加速、右マウスドラッグで視点回転ができます。

生成完了ログは次の形式です。

```text
ENVIRONMENT_COST_INSPECTION_SCENE_READY area=<areaId> buildingColliders=<n> roadColliders=<n> terrainColliders=<n> shadowCasters=<n> shadowReceivers=<n> scene=Assets/Scenes/EnvironmentCostInspection/<areaId>.unity
```

`buildingColliders`、`roadColliders`、`terrainColliders`がすべて0より大きいことを確認します。`shadowCasters`は建物・地形のRenderer数、`shadowReceivers`はScene内で影を受けるRenderer数です。

## Editorでの確認

1. `tools/plateau-environment-cost-analyzer` をUnity 6000.3.18f1で開く。
2. **PLATEAU > Environment Cost > Create Inspection Scene** を開く。
3. `data/analysis-configs/<areaId>.json` を選び、**Create inspection Scene** を実行する。
4. Consoleの完了ログとCollider数を確認する。
5. Scene表示で建物・道路・地形が同一地点に重なっていること、Directional Lightにより建物・地形の影が落ちることを確認する。
6. 再生し、自由カメラで地表・道路・建物の位置関係を確認する。

大きな三角形を含む一部CityGMLでは、Unityの`MeshCollider`警告が表示される場合があります。これは自動的に処理を中断するエラーではありません。完了ログ、Collider数、Scene表示を合わせて判断します。

## Windows Playerの確認

Inspection Sceneを作成後、ビルドしたい地域の `Assets/Scenes/EnvironmentCostInspection/<areaId>.unity` を開いた状態で、**PLATEAU > Environment Cost > Build Inspection Player (Windows)** を実行します。出力先は次です。

```text
tools/plateau-environment-cost-analyzer/Builds/EnvironmentCostInspection/<areaId>/<areaId>.exe
```

成功時は次のログを出力します。

```text
ENVIRONMENT_COST_INSPECTION_PLAYER_READY area=<areaId> scene=Assets/Scenes/EnvironmentCostInspection/<areaId>.unity path=Builds/EnvironmentCostInspection/<areaId>/<areaId>.exe bytes=<n>
```

Playerを起動し、自由カメラ、DEM、建物・地形の影、道路面の表示を確認します。ビルド出力はGit管理外です。

## 市ヶ谷での検証結果（2026-08-25）

`ichigaya-venue` の7データセットを用いて生成し、次の完了ログを確認しました。

```text
ENVIRONMENT_COST_INSPECTION_SCENE_READY area=ichigaya-venue buildingColliders=183 roadColliders=238 terrainColliders=14 shadowCasters=197 shadowReceivers=435
```

Scene表示では、建物・道路の下にDEM地表面が配置され、建物と地形の影が道路面へ落ちることを確認しました。再生モードでは `Environment Cost Runtime Camera` から広域モデルを表示できました。

Windows Playerも生成でき、Unityのビルドログは次を出力しました。

```text
ENVIRONMENT_COST_INSPECTION_PLAYER_READY area=ichigaya-venue scene=Assets/Scenes/EnvironmentCostInspection/ichigaya-venue.unity path=Builds/EnvironmentCostInspection/ichigaya-venue/ichigaya-venue.exe bytes=4076852264
```

展開後の出力は302ファイル、約4.08 GBです。これは4 km半径の7データセットを、DEM・建物・道路の全メッシュおよびテクスチャを含めてPlayerへ梱包した結果です。配布用の軽量化（対象メッシュの絞込み、Addressables等）は別途の最適化課題として扱います。

生成したPlayerも起動し、広域の建物とDEM地表面が表示されること、`W`入力で実行時カメラが前進することを確認しました。大容量メッシュを初期化するため、ウィンドウが表示されるまで数分を要しました。

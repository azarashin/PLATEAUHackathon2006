# Runtime施策シナリオ編集（#62）

Windows Player では、都市計画担当者が Unity Editor や手書き JSON を使わずに、検証済みの都市パッケージ上で施策を編集できます。

## 操作

1. `Assets/Scenes/EnvironmentCostInspection/<areaId>.unity` を対象に Inspection Player をビルドして起動します。
2. 左下の **Runtime Policy Scenario Editor** で `tree`、`shade`、`obstacle` を選択します。
3. **Place selected type by clicking Road / Terrain** をオンにして地図をクリックします。既存の施策をクリックすると選択でき、道路・地表上へドラッグして移動できます。`Delete` キーまたは **Delete selected** で削除します。
4. 選択した施策の高さ、樹冠半径（tree）、幅・奥行き（shade / obstacle）、向きを編集して **Apply selected dimensions** を押します。
5. シナリオ ID、名称、作成者、証跡メモを入力し、**Save** を押します。**Clone A/B** は比較案を複製します。
6. 施策変更後は **Runtime Shade Analysis** の分析を再実行します。変更前の結果は無効化されます。

## 保存先と監査情報

保存先は Player ごとの次のディレクトリです。`StreamingAssets` と都市パッケージ自体は書き換えません。

```text
%USERPROFILE%\AppData\LocalLow\<CompanyName>\<ProductName>\EnvironmentCostScenarios\<areaId>\<scenarioId>.json
```

Runtime シナリオ JSON（`environment-cost-runtime-policy-scenario-0.1`）には、少なくとも次を記録します。

- 施策 ID、種別、ローカル位置、緯度経度、高さ、寸法、向き
- `areaId`、平面直角座標系の zone、中心座標
- 編集に用いた city package の版と manifest SHA-256
- 作成・更新時刻、作成者、証跡メモ

この保存物は施策案そのものの機械可読な監査記録です。計算結果とその証跡の保存・エクスポートは #63 の責務です。

ロード時は `areaId` だけでなく、平面直角座標系、中心座標、city package の版、manifest SHA-256 を照合します。一致しない保存物は誤った座標へ施策を再利用しないよう読み込みません。

## 既存の施策 JSON を取り込む

既存の `environment-cost-policy-scenario-0.1` JSON は、次の場所へ
`import-policy-scenario.json` として置くと **Import existing 0.1 JSON from persistentDataPath** で取り込めます。

```text
%USERPROFILE%\AppData\LocalLow\<CompanyName>\<ProductName>\EnvironmentCostScenarios\import-policy-scenario.json
```

互換取込の対象は既存形式の `tree` と `shade` です。Runtime 固有の `obstacle` は既存 0.1 形式に存在しないため、Runtime シナリオとして保存します。

## 入力検証の範囲

- 都市パッケージの半径外には配置できません。
- 施策同士が平面上で重なる位置には配置できません。移動して解消します。
- 高さ、樹冠半径、幅・奥行きは正の値が必要です。
- 地図上の配置は `Road` / `Terrain` collider に Raycast して地表高を取得します。

建物との詳細な干渉、道路通行可能性の変更、連続配置・複数選択・スナップは後続の高度化対象です。`obstacle` はこの段階では日射 Raycast の遮蔽物であり、道路ネットワークを通行止めにはしません。

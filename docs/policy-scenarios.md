# 街路樹・人工シェード施策シナリオ

Issue #15 は、建物影の基準解析を保持したまま、街路樹または人工シェードを仮想配置したシナリオを比較する。ベースラインは分析設定に`policyScenarioInputPath`を指定しない実行であり、出力の`scenario.id`は`baseline`になる。

施策シナリオは`environment-cost-policy-scenario-0.1` JSONで管理する。`id`は出力データとキャッシュキーに含まれ、設備の追加・移動・削除や寸法変更は全時刻キャッシュを無効化する。現時点の`recalculationScope`は`all`のみで、全対象範囲を安全に再計算する。影響範囲だけの再計算は未実装であり、部分キャッシュを再利用してはならない。

## 配置・移動・削除

Unityで検証用Sceneを開き、**PLATEAU > Environment Cost > Policy Scenario** を選ぶ。`Add tree`または`Add artificial shade`で設備を追加し、緯度・経度・高さ・寸法を編集して移動／変更する。街路樹の既定値は高さ6 m・樹冠半径1.8 mで、樹冠は球ではなく横長の楕円体である。`Delete facility`で削除し、`Save`でJSONへ保存する。`Preview in Scene`は同じCollider形状をSceneへ表示するため、見た目の影と解析時Raycastの遮蔽物は同一である。

## 実行

`data/analysis-configs/ichigaya-venue-policy-demo.json`は人工シェードを一つ含む最小例である。ベースラインを上書きせず、専用の出力・状態・キャッシュへ書き出す。

```powershell
$unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.18f1\Editor\Unity.exe'
$project = 'tools\plateau-environment-cost-analyzer'
$config = 'data\analysis-configs\ichigaya-venue-policy-demo.json'

& $unity -batchmode -projectPath $project `
  -executeMethod EnvironmentCostAnalyzer.Run -analysisConfig $config -forceRecalculate
```

解析時は、設備ごとにBuildingレイヤーのColliderを生成する。街路樹は幹と樹冠、人工シェードは屋根と4本の支柱で構成され、既存の建物と同じ上向きRaycastに参加する。出力の`scenario.id`と`scenario.fingerprintSha256`によりベースラインと施策案を識別する。

## 確認

同一日時でベースラインとシナリオ出力の対象エッジを比較し、設備の影響範囲で`shadeRatio`が増え、`solarExposureSeconds`が減ることを確認する。設備の座標・寸法・種別を変えた場合は、必ず`-forceRecalculate`付きで全範囲を実行する。

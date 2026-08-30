# Runtime UI の入力フォーカス境界

Runtime Player では、3D カメラの移動キーと UI Toolkit の入力欄が同じキーボードを共有する。本資料では、意図しないテキスト入力・フォーカス移動を防ぐための入力境界を記録する。

## 発生した現象

次の操作で、日付欄へ `w` が連続入力されることがあった。

1. Runtime Player を起動する。
2. 画面中央（都市表示または UI パネルの余白）をクリックする。
3. `W` キーを押し続ける。

期待する動作は、日付欄などを直接クリックしていない限り、`W/S/A/D/Q/E` をカメラ操作にだけ使用することである。

## 原因

初期実装では、各 `runtime-panel` にだけ `KeyDownEvent` と `NavigationMoveEvent` の遮断処理を登録していた。しかし Runtime UI ではフォーカス対象がない場合、入力イベントの送信先は個別パネルではなく UIDocument 最上位の `visualTree` となる。そのため最初の `W` による UI ナビゲーションが個別パネルの処理を通らず、先頭の `TextField` を選択していた。

また、フォーカス解除は「UI 外をクリックした」ことを `IsPointerOverUi` で判定していた。画面中央が UI パネルの余白に含まれる場合や、UI Toolkit の PointerDown が期待どおり届かない場合には、入力欄のフォーカスが残った。

## 設計方針

### 1. UIDocument 最上位で入力を受ける

`EnvironmentCostRuntimeUiInputGate.TrackDocument` は、UIDocument の最上位 `visualTree` へ次を登録する。

- `KeyDownEvent`: `W/S/A/D/Q/E` は、明示的に選択された編集欄にフォーカスがある場合だけ通す。
- `NavigationMoveEvent`: 常に停止する。Legacy Input Manager が移動キーを UI ナビゲーションに変換して、Slider や TextField を選択することを防ぐ。
- `FocusInEvent` / `FocusOutEvent`: 実際のフォーカス状態を同期する。ポインターで選択されていない編集欄への自動フォーカスは直ちに解除する。

個別パネルでのみイベントを捕捉してはならない。

### 2. 編集は明示的なポインター選択でのみ開始する

`TextField` または `FloatField` 自体、もしくはその子要素を直接クリックした場合だけ、入力欄を「編集許可」状態にする。

それ以外のクリックでは `ClearTextInputFocus` を実行する。対象は次のすべてである。

- 都市表示・地図
- UI パネルの余白、ラベル、タブ
- Button、Slider、Toggle、ScrollView

この状態は `EnvironmentCostRuntimeUiInputGate.IsTextInputFocused` としてカメラ側へ公開する。`EnvironmentCostInspectionFlyCamera` は、編集許可状態の間だけカメラ移動を止める。

### 3. UI 外判定は座標変換と Pick で補完する

PointerDownEvent だけに依存せず、`EnvironmentCostRuntimeUiController.Update` で左クリック時に次を行う。

1. `Input.mousePosition` の Y 座標を UI Toolkit の上端基準へ反転する。
2. `RuntimePanelUtils.ScreenToPanel` で Panel 座標へ変換する。
3. `runtimeUiSurface.worldBound` と `panel.Pick` で、UI 内のクリック要素を求める。
4. UI 外は `null` として Input Gate へ渡し、入力フォーカスを解除する。

この座標変換は [Unity の RuntimePanelUtils.ScreenToPanel](https://docs.unity3d.com/ScriptReference/UIElements.RuntimePanelUtils.ScreenToPanel.html) の手順に従う。固定ピクセル矩形や、ポインター状態フラグだけで UI 領域を判断してはならない。

### 4. キーボードのフォーカスリングを限定する

- Button、Slider、SliderInt、Toggle は `focusable = false` と `tabIndex = -1` にする。
- TextField、FloatField はポインターで編集できるよう `focusable` のままにし、`tabIndex = -1` で方向キー・Tab による自動選択から外す。

Runtime UI はポインター優先の操作を採用する。キーボードだけで全 UI を操作するアクセシビリティ要件を追加する場合は、この方針を見直し、カメラキーと UI ナビゲーションの入力マップを分離する。

### 5. 初期化と破棄を対称に扱う

UI 構築直後と次フレームで、明示的に編集を開始していない場合はフォーカスを解除する。UI を破棄・再生成する時は `StopTracking` で全コールバックを解除する。これにより、古い UIDocument の static 参照やイベントが新しい Scene に影響することを防ぐ。

## 操作時の期待動作

| 操作 | TextField への入力 | カメラ移動 |
| --- | --- | --- |
| UI 外、都市表示、UI 余白をクリック後に `W` | 入力しない | 移動する |
| Button / Slider / Toggle をクリック後に `W` | 入力しない | 移動する |
| TextField / FloatField を直接クリック後に `W` | 入力する | 停止する |
| 日陰解析実行中 | 入力しない | 停止する |

## 実装箇所

- `tools/plateau-environment-cost-analyzer/Assets/EnvironmentCostRuntimeUiInputGate.cs`
  - UIDocument 最上位のイベント処理、編集許可状態、フォーカス解除、破棄時の解除
- `tools/plateau-environment-cost-analyzer/Assets/EnvironmentCostRuntimeUiController.cs`
  - クリック座標の Panel 変換、Pick、初期フォーカス解除
- `tools/plateau-environment-cost-analyzer/Assets/EnvironmentCostInspectionFlyCamera.cs`
  - 編集中・解析中のカメラ移動抑制

## 回帰テスト

`HourlyEnvironmentCostSelfTests.Run` は実UIDocumentを生成して、少なくとも次を検証する。

- 自動復元された TextField に `W` を送っても値が変わらない。
- TextField を直接クリックした後は `W` が妨げられない。
- UI 余白クリック後、および UI 外クリックを表す `null` target 後は、`W` が妨げられ、TextField へ届かない。
- 非編集コントロールがキーボードフォーカス対象外であり、編集欄が方向キー・Tab のナビゲーション対象外である。

バッチ検証は次を実行し、`HOURLY_ENVIRONMENT_COST_SELF_TEST_PASSED` を確認する。

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.3.18f1\Editor\Unity.exe' `
  -batchmode -nographics `
  -projectPath 'H:\MyDevelopment\PLATEAUHackathon2006\tools\plateau-environment-cost-analyzer' `
  -executeMethod HourlyEnvironmentCostSelfTests.Run `
  -logFile 'H:\MyDevelopment\PLATEAUHackathon2006\tools\plateau-environment-cost-analyzer\Logs\runtime-ui-focus-selftest.log'
```

手動確認では、更新済み Player を起動して「入力欄ではない位置をクリックしてから `W` を長押し」し、入力欄の値が変化せずカメラだけが移動することを確認する。次に入力欄を直接クリックし、文字を入力できることとカメラが移動しないことを確認する。

---
name: 不具合報告
about: MOD を入れた状態で起きた不具合の報告
title: ''
labels: bug
assignees: ''
---

## 何が起きたか
(例: 戦闘で L を押しても退却が出ない)

## 再現手順
1.
2.
3.

## 期待した動き

## 環境
- RS2QoL の版: (zip の名前、または `BepInEx\LogOutput.log` の `Loading [RS2QoL x.y.z]` の行)
- ゲームパッド: (機種、USB / Bluetooth / Xbox ワイヤレス アダプター)
- Steam Input: (ON / OFF)
- Windows: (10 / 11)
- 他に入れている BepInEx プラグイン: (無ければ「無し」)

## ログ
MOD 設定画面 (フィールドで Start のポーズ中、またはタイトル画面の NEW GAME / CONTINUE で L3。キーボードは F1) の「その他」ページで
**「診断ログを出す」を ON** にし (キーボードなら F8。または `BepInEx\config\net.my-p.rs2qol.cfg` の `[Diagnostics] Enabled = true`)、現象を再現した直後に
ゲームパッドの **Start と Select を同時押し** (キーボードなら **F7**。ログに目印が入ります) してから、診断ログを OFF にするかゲームを閉じてください。
ログが `BepInEx\RS2QoL-diag\` に取り分けられてそのフォルダがエクスプローラで開くので、そこの `RS2QoL-diag_<起動日時>.log` をここに添付 (ドラッグ & ドロップ) してください (ファイルは起動ごとに 1 つで、同じ起動の中で OFF を繰り返すと同じファイルが更新されます)。

## 設定ファイル (あれば)
`BepInEx\config\net.my-p.rs2qol.cfg` を添付してください (既定のままなら不要)。

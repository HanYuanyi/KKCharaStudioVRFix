# KKCharaStudioVRFix

KKCharaStudioVRPlugin_0.0.3的独立修复插件，给原版的一堆BUG修复和添加本该有的功能

## 前置依赖

| 依赖 | 版本 |
|---|---|
| Koikatu / Koikatsu Party（含 CharaStudio） | 本体 |
| BepInEx | 5.4.x（64 位） |
| KKCharaStudioVRPlugin | 0.0.3（修这个的BUG） |

## 安装

1. 装好 BepInEx 5.4 和原版 KKCharaStudioVRPlugin 0.0.3
2. 把 KKCharaStudioVRFix.dll 放进 BepInEx/plugins/
3. 用你原来的方式启动VR

## 修了什么

KKCharaStudioVRPlugin_0.0.3的BUG
- 第二次加载场景卡或连续切换地图后镜头跑到一个诡异位置，UI，手柄消失，IKFK点强制永久显示，画面变灰(属于滤镜变成无了，就是早晨黄昏夜晚那个) 只能重启游戏，现在不会再触发这个BUG了。修复方法：游戏反复把 VR rig 搬进地图场景，卸载时 rig 被带走导致 Mode 死亡。改为事件驱动（VRCamera.OnAwake 加四个场景卸载 API 前缀），在卸载前补齐 DontDestroyOnLoad。
- 手柄如果先于游戏启动，进入游戏手柄会只剩圆盘，VR无法控制指针，或者是手柄能控制指针但按下扳机键VR指针就瞬移到左上角，现在手柄先启动和后启动都不会出现这些BUG了，修复方法：SteamVR_RenderModel.UpdateModel 只在名字变化时重建模型，开机时序不对时会静默失败不重试。
- 修复进入游戏后齿轮图标的system栏里面的所有窗口看不见问题，比如画面效果菜单看不到但是又能点到(只能盲点)，和各种功能的下拉出现的菜单看不到，现在可以一直看到了。
- 现在游戏进入后会自动解锁拖动世界，按侧键拖动。
## 新添加的
- 修复画面效果无法实时更改问题，游戏启动后只能一直用默认保存的画面预设的效果，现在可以实时更改，也可以加载场景卡里面保存的画面效果了。
- 现在加载场景卡以后，镜头会自动回到场景卡保存正确的镜头的旋转和位置，上面保存的10个镜头点击后VR相机也可以去到对应正确的旋转和位置了。
- 修复ME窗口一拖动就瞬移到屏幕中央和ME下拉菜单看不到和ME悬浮说明框错位问题，(原UILib.MovableWindow 用真实鼠标坐标当世界坐标，VR 下必然错位。)
- 增加按F4黑屏平面显示器隐私屏幕功能

## 未修复的
- KKCharaStudioVRPlugin_0.0.3就有的部分地图里的镜子反射问题，只有一只眼会渲染镜子，因为会造成帧率下降暂时没修复。
- KKCharaStudioVRPlugin_0.0.3原版的VR第一人称功能，先按ctrl+B，再ctrl+C，ctrl+shift+X的第一人称功能，已被我开发出的KK_PerspectiveX插件里面的VR第一人称功能完美替代，且功能更完善更强大体验更自由更好，所以不修复这部分，请不要按CTRL+B进入那个模式，会触发指针BUG，只能重启游戏解决，不知道有没有什么人需要这功能，暂时没禁用。
- 想使用VR第一人称视角(强烈建议)可以下载K_PerspectiveX插件，KK_PerspectiveX下载请前往或github地址https://github.com/HanYuanyi/KK_PerspectiveX

## 兼容
和Ermin那边的"Timeline 镜头进行了 VR 视角补正"插件https://github.com/YukyoMoe/KK_VR_CameraSync 兼容，你可以选择用我的这个插件KKCharaStudioVRFix+KK_VR_CameraSync，也可以继续使用KK_VR0.4.0+KK_VR_CameraSync。
这个插件和Ermin开发的KK_VR0.4.0兼容性未知，因为KK_VR0.4.0是通过手腕菜单功能绕过了上面的这些BUG，但绕的什么情况我没测试过，不过本插件需要有KKCharaStudioVRPlugin0.0.3，大概率无法一起使用。

## 许可
MIT，见 LICENSE。

----------------------------------------------------------------------------------------------------------------------------------------------------------

# KKCharaStudioVRFix

A standalone fix plugin for KKCharaStudioVRPlugin 0.0.3 — it fixes a pile of bugs in the original and adds a few things it should have had in the first place.

## Requirements

| Dependency | Version |
|---|---|
| Koikatu / Koikatsu Party (with CharaStudio) | base game |
| BepInEx | 5.4.x (64-bit) |
| KKCharaStudioVRPlugin | 0.0.3 (this plugin fixes its bugs) |

## Install

1. Install BepInEx 5.4 and the original KKCharaStudioVRPlugin 0.0.3
2. Drop KKCharaStudioVRFix.dll into BepInEx/plugins/
3. Start VR the same way you always do

## What it fixes

Bugs that came with KKCharaStudioVRPlugin 0.0.3:

- Load a scene card a second time, or switch maps several times in a row, and the camera ends up in a strange position, the UI and controllers vanish, IK/FK points get forced to stay visible permanently, and the screen goes grey (the filter becomes "none" — the morning/dusk/night one). The only way out was restarting the game. This bug will no longer trigger. How it is fixed: the game repeatedly moves the VR rig into the map scene, and when that scene unloads the rig gets dragged out with it, causing the Mode to die. It was changed to event-driven handling (VRCamera.OnAwake plus the four scene-unload API prefixes), so DontDestroyOnLoad is applied before the unload happens.
- If the controllers are started before the game, after entering the game only the ring/disk remains visible and VR cannot control the pointer; or the pointer works, but the moment you press the trigger the VR pointer teleports to the top-left corner. Now these bugs no longer occur whether the controllers start before or after the game. How it is fixed: SteamVR_RenderModel.UpdateModel only rebuilds the model when the name changes, and when the boot timing is wrong it fails silently and never retries.
- Fixed the issue where all windows inside the gear-icon System tab are invisible after entering the game — for example, the image effects menu cannot be seen but can still be clicked (blind clicking), and the dropdown menus that appear for various functions cannot be seen. Now they are always visible.
- Dragging the world is now automatically unlocked after entering the game. Drag with the side button.

## What it adds

- Fixed the issue where image effects could not be changed in real time. After the game starts, you could only keep using the effects of the default saved image preset; now they can be changed in real time, and you can also load the image effects saved inside a scene card.
- After loading a scene card, the camera now automatically returns to the correct camera rotation and position saved in the scene card. After clicking one of the 10 camera slots saved in it, the VR camera can also go to the corresponding correct rotation and position.
- Fixed the ME window teleporting to the center of the screen as soon as it is dragged, the ME dropdown menus being invisible, and the ME hover tooltip being misaligned. (The original UILib.MovableWindow uses real mouse coordinates as world coordinates, which is guaranteed to be misaligned in VR.)
- Added an F4 privacy-screen feature that blacks out the flat monitor.

## Not fixed

- The mirror reflection issue in some maps that was already present in KKCharaStudioVRPlugin 0.0.3 has not been fixed. Only one eye renders the mirror, and it has not been fixed for now because it would cause a framerate drop.
- The original VR first-person mode in KKCharaStudioVRPlugin 0.0.3 (press Ctrl+B, then Ctrl+C, then Ctrl+Shift+X) has been perfectly replaced by the VR first-person feature in the KK_PerspectiveX plugin that I co-developed, which has more complete and powerful features and a freer, better experience, so I am not fixing this part. Please do not press Ctrl+B to enter that mode; it triggers the pointer bug, and the only way to solve it is to restart the game. I don't know if anyone needs this feature, so I haven't disabled it for now.
- If you want to use the VR first-person view (strongly recommended), you can download the K_PerspectiveX plugin. For the VR feature documentation and download of KK_PerspectiveX, go to GitHub: https://github.com/HanYuanyi/KK_PerspectiveX

## Compatibility

Works with Ermin's "Timeline camera VR correction" plugin https://github.com/YukyoMoe/KK_VR_CameraSync — you can either use my KKCharaStudioVRFix + KK_VR_CameraSync, or continue using KK_VR 0.4.0 + KK_VR_CameraSync.

Compatibility with Ermin's KK_VR 0.4.0 is unknown, because KK_VR 0.4.0 bypasses the above bugs through its wrist-menu feature, but I have not tested exactly what it bypasses or how. However, this plugin requires KKCharaStudioVRPlugin 0.0.3, so there is a high probability they cannot be used together.

## License

MIT, see LICENSE.

# KKCharaStudioVRFix

KKCharaStudioVRPlugin_0.0.3的独立修复插件，给原版的一堆BUG修复和添加本该有的功能

## 前置依赖

| 依赖 | 版本 |
|---|---|
| Koikatu / Koikatsu Party（含 CharaStudio） | 本体 |
| BepInEx | 5.4.x（64 位） |
| KKCharaStudioVRPlugin | 0.0.3（插件是修它的BUG） |

## 安装

1. 装好 BepInEx 5.4 和原版 KKCharaStudioVRPlugin 0.0.3
2. 把 KKCharaStudioVRFix.dll 放进 BepInEx/plugins/
3. 用你原来的方式启动VR

## 修了什么

KKCharaStudioVRPlugin_0.0.3的BUG
- 第二次加载场景卡或连续切换地图后镜头跑到一个诡异位置，UI，手柄消失，IKFK点强制永久显示，画面变灰(属于滤镜变成无了，就是早晨黄昏夜晚那个) 只能重启游戏，现在不会在触发这个BUG了。修复方法：游戏反复把 VR rig 搬进地图场景，卸载时 rig 被带走导致 Mode 死亡。改为事件驱动（VRCamera.OnAwake 加四个场景卸载 API 前缀），在卸载前补齐 DontDestroyOnLoad。
- 手柄如果先于游戏启动，进入游戏手柄会只剩圆盘，VR无法控制指针，或者是手柄能控制指针但按下扳机键VR指针就瞬移到左上角，现在手柄先启动和后启动都不会出现这些BUG了，修复方法：SteamVR_RenderModel.UpdateModel 只在名字变化时重建模型，开机时序不对时会静默失败不重试。
- 修复进入游戏后齿轮图标的system栏里面的所有窗口看不见问题，比如画面效果菜单看不到但是又能点到(只能盲点)，和各种功能的下拉出现的菜单看不到，现在可以一直看到了。
- 现在游戏进入后会自动解锁拖动世界，按侧键拖动。
## 新添加的
- 修复画面效果无法实时更改问题，游戏启动后只能一直用默认保存的画面预设的效果，现在可以实时更改，也可以加载场景卡里面保存的画面效果了，原理：VR 相机上的效果是快照副本，改纹理类参数必须销毁重建组件。
- 现在加载场景卡以后，镜头会自动回到场景卡保存正确的镜头的旋转和位置，上面保存的10个镜头点击后VR相机也可以去到对应正确的旋转和位置了
- 修复ME窗口一拖动就瞬移到屏幕中央和ME下拉菜单看不到和ME悬浮说明框错位问题，(原UILib.MovableWindow 用真实鼠标坐标当世界坐标，VR 下必然错位。)

## 未修复的
- KKCharaStudioVRPlugin_0.0.3就有的部分地图里的镜子问题，考虑到浪费性能降低游戏帧数没有修复。
- KKCharaStudioVRPlugin_0.0.3原版的VR第一人称功能，先按ctrl+B，再ctrl+C，ctrl+shift+X的第一人称功能，已被我魔改开发出的KK_PerspectiveX插件里面的VR第一人称功能完美替代，且功能更完善更强大体验更自由更好，所以不修复这部分，请不要按CTRL+B进入那个模式，会触发指针BUG，只能重启游戏解决。
- 想使用VR第一人称视角(强烈建议)可以下载我魔改的KK_PerspectiveX插件，KK_PerspectiveX的VR功能说明和下载请前往ZOD或github，地址https://github.com/HanYuanyi/KK_PerspectiveX

## 兼容
和Ermin那边的"Timeline 镜头进行了 VR 视角补正"插件https://github.com/YukyoMoe/KK_VR_CameraSync 兼容，你可以选择用我的这个插件KKCharaStudioVRFix+KK_VR_CameraSync，也可以继续使用KK_VR0.4.0+KK_VR_CameraSync。
本插件和Ermin开发的KK_VR0.4.0兼容性未知，因为KK_VR0.4.0是通过手腕菜单功能绕过了上面的这些BUG，但绕的什么情况我没测试过，不过本插件需要有KKCharaStudioVRPlugin0.0.3，大概率无法一起使用。

## 许可
MIT，见 LICENSE。

-------------------------------------------------------------

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

- Load a scene card a second time, or switch maps a few times, and the camera ends up somewhere weird, the UI and controllers vanish, IK/FK points get stuck on permanently, and the screen goes grey (the filter ends up as "none" — the morning/dusk/night one). The only way out was restarting the game. It won't trigger anymore. How it's fixed: the game keeps shoving the VR rig into the map scene, and when that scene unloads the rig gets dragged out with it, which kills the Mode. Switched it to event-driven (VRCamera.OnAwake plus the four scene-unload API prefixes) so DontDestroyOnLoad is applied before the unload happens.
- If the controllers are started before the game, you'd end up with just the ring visible and no pointer control — or the pointer would work but snap to the top-left corner the moment you pull the trigger. Now it doesn't matter whether the controllers start first or last. How it's fixed: SteamVR_RenderModel.UpdateModel only rebuilds the model when the name changes, and when the boot timing is off it fails silently and never retries.
- Fixed the windows inside the gear-icon System tab being invisible — e.g. the image effects menu you could click but not see (blind clicking), plus every dropdown menu. They're always visible now.
- Dragging the world is unlocked automatically once you're in game. Drag with the side button.

## What it adds

- Image effects can be changed in real time now. Before, you were stuck with whatever preset was saved at launch; now you can change it live and even load the effects saved inside a scene card. Why it needed fixing: the effects on the VR camera are snapshot copies, so changing texture-type parameters means destroying and rebuilding the component.
- After loading a scene card, the camera returns to the exact rotation and position saved in that card. The 10 camera slots saved in it work too — click one and the VR camera goes to the right spot.
- Fixed the ME window teleporting to the middle of the screen the moment you drag it, the ME dropdown menus being invisible, and the ME tooltip being out of place. (The original UILib.MovableWindow treats real mouse coordinates as world coordinates, which is guaranteed to be off in VR.)

## Not fixed

- The mirror issue in some maps was already there in 0.0.3. I left it alone because fixing it costs performance and drops your framerate.
- The original 0.0.3 first-person mode (Ctrl+B, then Ctrl+C, then Ctrl+Shift+X) is not fixed. My own modded KK_PerspectiveX has a first-person mode that replaces it completely — better features, more freedom, just a nicer experience. So I'm not touching this one. Please don't press Ctrl+B to get into that mode; it triggers the pointer bug and you'll have to restart the game.
- If you want first-person VR (and I'd strongly recommend it), grab my modded KK_PerspectiveX. Docs and download are on ZOD or GitHub: https://github.com/HanYuanyi/KK_PerspectiveX

## Compatibility

Works alongside Ermin's "Timeline camera VR correction" plugin https://github.com/YukyoMoe/KK_VR_CameraSync — you can either run my KKCharaStudioVRFix + KK_VR_CameraSync, or keep using KK_VR 0.4.0 + KK_VR_CameraSync.

Compatibility with Ermin's KK_VR 0.4.0 is unknown. KK_VR 0.4.0 gets around these bugs through the wrist menu, but I never tested exactly how it does that. This plugin also needs KKCharaStudioVRPlugin 0.0.3, so most likely they can't run together.

## License

MIT, see LICENSE.

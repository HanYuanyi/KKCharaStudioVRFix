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
本插件和Ermin开发的KK_VR0.4.0兼容性未知，因为KK_VR0.4.0是通过手腕菜单功能绕过了上面的这些BUG，但绕的什么情况我没测试过，不过本插件需要有KCharaStudioVRPlugin0.0.3，大概率无法一起使用。

## 许可
MIT，见 LICENSE。

以下是英文翻译
----------------------------------------------------------------------------------------------------------------------------------------------------------


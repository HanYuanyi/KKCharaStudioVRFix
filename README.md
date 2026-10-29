# KKCharaStudioVRFix

Koikatu CharaStudio 的 VR 修复插件。它不替代原版，而是在原版 KKCharaStudioVRPlugin 0.0.3 之上修掉一批实际使用中遇到的问题。

## 前置依赖

| 依赖 | 版本 |
|---|---|
| Koikatu / Koikatsu Party（含 CharaStudio） | 本体 |
| BepInEx | 5.4.x（64 位） |
| KKCharaStudioVRPlugin | 0.0.3（必须先装，本插件是修它） |

## 安装

1. 装好 BepInEx 5.4 和原版 KKCharaStudioVRPlugin 0.0.3
2. 把 KKCharaStudioVRFix.dll 放进 BepInEx/plugins/
3. 用 CharaStudio.exe --studiovr 启动（与原版一致）

## 它修了什么

- 镜头跳变、蓝屏、手柄消失：游戏反复把 VR rig 搬进地图场景，卸载时 rig 被带走导致 Mode 死亡。改为事件驱动（VRCamera.OnAwake 加四个场景卸载 API 前缀），在卸载前补齐 DontDestroyOnLoad。
- 手柄只剩圆盘：SteamVR_RenderModel.UpdateModel 只在名字变化时重建模型，开机时序不对时会静默失败且永不重试。
- 画面效果（色彩分级、后处理）在 VR 里丢失或变灰：VR 相机上的效果是快照副本，改纹理类参数必须销毁重建组件。
- ME 窗口拖动瞬移：UILib.MovableWindow 用真实鼠标坐标当世界坐标，VR 下必然错位。
- ME 悬浮说明框错位与闪烁：定位方式改为跟随控件，并关掉面板的射线遮挡（否则会形成"显示-隐藏"死循环）。
- 平面模式下镜头卡死、UI 层级错乱、下拉菜单消失等若干项。

## 配置

按 F1 打开 BepInEx 配置界面，全部配置项都有中文说明。

## 自己编译

需要 .NET SDK 8，以及游戏本体（用来取参考程序集）。

```
dotnet build -c Release -p:GameDir="你的游戏目录"
```

GameDir 是 CharaStudio.exe 所在目录。不带 GameDir 会报大量 CS0246。

## 说明

- 本插件不修改游戏目录里的任何文件，只读游戏程序集做参考。
- 插件没有任何日志输出。遇到问题请先确认原版 0.0.3 是否正常工作。

## 许可

MIT，见 LICENSE。

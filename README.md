# Pico Vibe

[English](README.en.md)

让状态变成看得见的氛围。

面向 **Apple Silicon（M 系列）Mac**、带全尺寸 SD 卡槽的机型。把设备插入卡槽后，用本应用控制灯光与触摸操作。

<p align="center">
  <img src="docs/images/hero-in-use.jpg" alt="Pico Vibe 亮灯效果" width="720" />
</p>

<p align="center"><em>插上模块、打开应用，侧边灯光就会跟着状态和音乐变化。</em></p>

## 下载

请从 Releases 下载 **Apple Silicon** 安装包：

**[→ 打开 Releases](https://github.com/huangguimin/pico_vibe/releases/latest)**

文件名形如：`PicoVibe-*-osx-arm64.dmg`（当前仅支持 macOS M 系列）

## 安装

1. 打开 DMG，把 `Pico Vibe.app` 拖进「应用程序」
2. 插入设备，应用显示已连接即可使用
3. 若使用 Cursor / Codex / Claude Code：在应用里安装 AI 集成；Codex 还需在 `/hooks` 中信任一次
4. 音乐律动首次可能需要「屏幕与系统音频录制」权限（只计算音量，不录音、不取画面）
5. 触摸「下一曲」首次可能需要允许控制 Music / Spotify 的自动化权限

## 功能

### AI 氛围

跟随 Cursor / Codex / Claude Code：空闲、思考、执行、等待授权、完成、错误各自可配灯效。等待确认时黄灯闪烁，用卡尾触摸即可批准或拒绝。

![AI 氛围界面：按状态配置颜色与效果](docs/images/app-ai.png)

*AI 氛围：为每种 AI 状态单独设置颜色、亮度和灯效。*

### 系统状态

用灯光显示 CPU、内存、电量等读数，一眼扫到机身边就能读懂负载。

![系统状态界面：实时读数与灯光映射](docs/images/app-system.png)

*系统状态：选择要显示的指标，再调颜色与灯珠范围。*

### 音乐律动

灯光跟随系统正在播放的音乐呼吸、跳动；灵敏度与律动方式可在应用里调节。

![音乐律动界面：实时音量与灵敏度](docs/images/app-music.png)

*音乐律动：实时音量条 + 灵敏度，右侧可预览当前模式灯光。*

### 触摸

卡尾标有 **TOUCH** 的区域支持单击、双击、长按，可按模式分别设置，例如：

- **等待 AI 授权时**：单击允许、双击或长按拒绝（默认）
- **音乐律动时**：可映射为切歌（下一曲，支持 Apple Music / Spotify）、切换灯效样式、或切换主模式

具体动作在应用的触摸设置里改，改完点保存即可。

<p align="center">
  <img src="docs/images/hardware-inserted.jpg" alt="插入 SD 卡槽，TOUCH 与灯区外露" width="560" />
</p>

<p align="center"><em>插入卡槽后，露出的灯条与 TOUCH 区域可直接点按操作。</em></p>

# Pico Vibe

让状态变成看得见的氛围。

面向 **Apple Silicon（M 系列）Mac**、带全尺寸 SD 卡槽的机型。把设备插入卡槽后，用本应用控制灯光：AI 氛围、系统状态、音乐律动，以及触摸确认。

<p align="center">
  <img src="docs/images/hero-in-use.jpg" alt="Pico Vibe 亮灯效果" width="720" />
</p>

## 下载

请从 Releases 下载 **Apple Silicon** 安装包：

**[→ 打开 Releases](https://github.com/huangguimin/pico_vibe/releases/latest)**

文件名形如：`PicoVibe-*-osx-arm64.dmg`（当前仅支持 macOS M 系列）

## 安装

1. 打开 DMG，把 `Pico Vibe.app` 拖进「应用程序」
2. 插入设备，应用显示已连接即可使用
3. 若使用 Cursor / Codex / Claude Code：在应用里安装 AI 集成；Codex 还需在 `/hooks` 中信任一次
4. 音乐律动首次可能需要「屏幕与系统音频录制」权限（只计算音量，不录音、不取画面）

## 功能

| 模式 | 作用 |
|------|------|
| **AI 氛围** | 跟随 Cursor / Codex / Claude Code 的状态变灯，等待授权时可触摸确认 |
| **系统状态** | 用灯光显示 CPU、内存、电量等 |
| **音乐律动** | 灯光跟随正在播放的音乐 |

![AI 氛围](docs/images/app-ai.png)

![系统状态](docs/images/app-system.png)

![音乐律动](docs/images/app-music.png)

<p align="center">
  <img src="docs/images/hardware-inserted.jpg" alt="插入 SD 卡槽" width="560" />
</p>

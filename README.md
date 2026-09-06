# Pico Vibe

Pico Vibe 是面向 Cursor / Codex / Claude Code 的 RGB 状态灯与触控审批桌面应用。

第一版支持：

- **AI 氛围**：跟随 AI 会话状态切换灯光氛围
- **系统状态**：用灯光反映系统/任务运行状态
- **音乐律动**：跟随音乐节奏律动

## 下载安装包

安装包以 **GitHub Releases 附件**发布，**不会**放进本仓库 Git 历史。

请到最新 Release 下载对应平台安装包：

**[→ 打开 Releases 下载](https://github.com/huangguimin/pico_vibe/releases/latest)**

| 平台 | 附件名 | 说明 |
|------|--------|------|
| macOS (Intel/Apple Silicon x64 包) | `PicoVibe-1.1.20-osx-x64.dmg` | 当前已发布 |

> 克隆本仓库只会得到文档与配置说明，不会包含 `.dmg` / `.exe` 等安装包。

## macOS 安装

1. 打开 [Releases](https://github.com/huangguimin/pico_vibe/releases/latest)，下载 `PicoVibe-*.dmg`
2. 打开 DMG，将应用拖入「应用程序」
3. 首次启动后，在 Cursor 打开 **Settings → Hooks**，信任新增/变更的用户级 Hook
4. 确认 Pico 设备已连接，并在控制面板中选择模式（AI / 系统 / 音乐）、亮度与触控映射

## 使用说明（摘要）

- 请使用正式发布的安装包接入，不要手写 Hook JSON 或用多个进程去写设备 RAM 盘
- 只有真实的权限请求（如 `PermissionRequest`）才会进入触控审批；普通 running/tools 事件不应点亮黄色审批状态
- 设备不可用或触控超时时应拒绝该次审批，让 AI 回落到正常确认 UI
- 安装或升级后，请再打开一次 `/hooks` 并信任变更后的 Hook

## 诊断

- 确认设备卷标 `PICO_RGB` 上存在 `VIBE0.BIN`、`VIBE1.BIN`，且均为 512 字节
- 日志位置（macOS）：应用数据目录下的 `pico-vibe.log`（具体路径以安装版为准）

## 仓库说明

| 内容 | 存放位置 |
|------|----------|
| 产品说明 / README | 本仓库 |
| 安装包（`.dmg` 等） | [GitHub Releases](https://github.com/huangguimin/pico_vibe/releases) |

本地若保留安装包文件，已被 `.gitignore` 忽略，请勿 `git add` 进仓库。

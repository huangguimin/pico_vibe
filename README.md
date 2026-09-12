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
4. 音乐律动首次可能弹出**系统音频**权限（只计算响度，不录音、不保存）。**1.1.21 起不再需要「屏幕录制」权限**
5. 仅当触摸映射为「下一曲」时，首次可能需要允许控制 Music / Spotify 的自动化权限

普通 AI 氛围、系统状态和触摸读取不依赖上述权限。

## 功能

### AI 氛围

跟随 Cursor / Codex / Claude Code：空闲、思考、执行、等待授权、完成、错误、状态失联各自可配灯效。触摸授权窗口开启时默认黄灯闪烁，用卡尾触摸即可批准或拒绝；交回 AI 自带授权界面后，默认保持黄色常亮。

![AI 氛围界面：按状态配置颜色与效果](docs/images/app-ai.png)

*AI 氛围：为每种 AI 状态单独设置颜色、亮度和灯效。*

### 系统状态

用灯光显示 CPU、内存、电量等读数，一眼扫到机身边就能读懂负载。

![系统状态界面：实时读数与灯光映射](docs/images/app-system.png)

*系统状态：选择要显示的指标，再调颜色与灯珠范围。*

### 音乐律动

灯光跟随系统正在播放的音乐呼吸、跳动；灵敏度与律动方式可在应用里调节。  
新版本通过系统音频采集响度，**不录屏、不录音、不保存音频**。

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

## AI 状态监控与钩子原理

Pico Vibe 通过 Cursor / Codex / Claude Code 提供的 **Hook（钩子）事件**感知 AI 的工作进度。客户端在提交提示词、执行工具、请求授权、结束回复等节点调用 `PicoVibeHook`，桌面应用再把这些事件映射为灯效。这里的“思考”“执行”等状态代表客户端所处的工作阶段。

### 从 AI 事件到灯光

```text
Cursor / Codex / Claude Code 触发生命周期事件
  → PicoVibeHook 从标准输入读取事件 JSON
  → 通过本机 IPC 管道把统一事件发送给 Pico Vibe 桌面应用
  → 应用更新 AI 状态，选择对应的颜色、亮度和灯效
  → 应用向 PICO_RGB 设备写入灯光数据，由固件驱动 LED
```

钩子是短时运行的事件适配程序，常驻桌面应用负责统一管理状态、写入设备和读取触摸。钩子与应用通过名为 `PicoVibe.Agent.v1` 的本机管道通信，钩子本身不直接写设备，因此使用 AI 集成时需要保持 Pico Vibe 应用运行。

### 各个客户端怎样上报状态

下表对应当前应用源码中安装的钩子及最终状态映射：

| 工作节点 | Cursor 事件 | Codex / Claude Code 事件 | 灯光状态 |
|---|---|---|---|
| 会话开始或结束 | `sessionStart` / `sessionEnd` | `SessionStart` / `SessionEnd` | 空闲 |
| 提交提示词 | `beforeSubmitPrompt` | `UserPromptSubmit` | 思考 |
| 开始调用工具 | `preToolUse`、`beforeShellExecution`、`beforeMCPExecution` | `PreToolUse` | 执行；若进入触摸授权则显示等待授权 |
| 工具调用完成 | `postToolUse` | `PostToolUse` | 本会话仍有工具运行则保持执行，否则回到思考 |
| 工具调用失败 | `postToolUseFailure`，或完成事件中的结构化失败结果 | Claude：`PostToolUseFailure`；Codex：`PostToolUse` 中的结构化失败结果 | 结束对应工具计数；无其他活动工作时短暂显示错误 |
| 本轮回复结束 | `stop` | `Stop` | 短暂显示完成，底层状态回到空闲 |

“完成”和“错误”是临时提示，显示时长由配置控制。新一轮提示词或工具开始会清除旧提示；其他会话仍在思考或执行时，不会被某个会话的完成提示覆盖。Codex 的失败识别使用结构化退出码或错误标志；工具只返回非结构化文本时，不根据文字猜测成功或失败。

多个客户端共用灯区，但分别按平台、会话、轮次及可用的子代理标识维护状态，并按工具调用 ID 跟踪并发工具。Claude 的 `SubagentStart` / `SubagentStop` 单独更新对应子代理。汇总显示优先级为：等待授权 → 执行 → 思考 → 状态失联 → 错误 → 完成 → 空闲。事件按时间重放并去重，延迟送达的旧事件不会仅因到达较晚而覆盖新状态。

状态监控仍依赖客户端实际发出的钩子。原生授权事件可能不带工具调用 ID，此时用工具输入的 SHA-256 摘要辅助关联；缺少标识、或同一会话同时请求完全相同的操作时，关联精度受客户端提供的信息限制。

### 等待授权与触摸如何闭环

进入触摸授权时，钩子会等待应用返回决定。应用建立本次请求的触摸窗口，显示授权灯效（默认黄灯闪烁），读取 `TOUCH.TXT` 中的新触摸事件，再把允许或拒绝交回钩子，由钩子通过标准输出返回客户端要求的 JSON。授权决定也会更新状态：允许后转为执行，拒绝后结束能关联到的工具计数。

- **Codex / Claude Code**：启用触摸授权后，只在原生 `PermissionRequest` 事件到来时等待触摸。关闭触摸授权后仍会上报原生等待状态，保持授权色常亮；普通 `PreToolUse` 只更新执行状态，Claude 的 `Notification` 不会开启授权窗口。允许或拒绝通过原生授权结果返回，无需模拟键盘按键。
- **Cursor**：按应用中的授权范围，在执行前决定是否等待触摸。Shell 可选全部、仅非沙箱或不拦截；MCP 可选全部、不拦截或按工具名中的写入类关键词判断；文件工具可单独启用拦截。MCP 的关键词判断是工具名启发式匹配。
- **触摸只作用于当前请求**：开启窗口前先读取并消耗已有触摸记录，避免上一次触摸批准下一次操作；多个授权请求串行处理。默认单击允许，双击或长按拒绝，也可在设置中修改。
- **未取得触摸决定时**：应用不可用、设备断开或等待超时，Codex / Claude Code 的钩子不作决定，交回客户端正常授权流程，显示授权色常亮；此时触摸不能批准该请求。Cursor 对已经被 Pico Vibe 拦截的操作返回拒绝。

在 AI 自带界面作出决定后，如果客户端没有额外发出授权结果事件，灯光要等匹配的工具完成/失败、新一轮提示词或结束事件才能更新；这段间隔内保留尚未确认解除的授权提示。

### 事件丢失与应用重启

钩子在发送 IPC 前，将显示事件写入应用配置目录下的 `hook-events/`。日志只保留事件、标识和输入摘要，不保存提示词、命令原文或工具输出；最多保留 8192 条，应用运行时清理 24 小时前的记录。应用启动后及运行期间每秒读取新增记录，恢复未及时送达的状态，并对 IPC 和日志中的同一事件去重。

**恢复日志只恢复灯光，不重新开启历史触摸授权。** 活动会话连续 10 分钟没有新事件会显示“状态失联”（默认灰色呼吸），直到新事件确认后续状态。长任务也可能触发这一提示，它表示状态缺少新证据，并不表示任务失败或已经结束。

### 安装集成时写入了什么

在应用中安装 AI 集成，会将 `PicoVibeHook` 的调用配置合并到当前用户的以下文件，保留无关钩子和设置：

| 客户端 | 配置位置 |
|---|---|
| Cursor | `~/.cursor/hooks.json` |
| Codex | `~/.codex/hooks.json` |
| Claude Code | `~/.claude/settings.json` 中的 `hooks` |

安装还会放置 `pico-vibe-status` Codex Skill，供 AI 查阅集成的使用与排查说明；运行时状态由上述钩子事件上报。Codex 安装或升级后，需要在 `/hooks` 中检查并信任新增或变更的钩子。

更新应用后，请重新安装 AI 集成，让新增的完成、失败及子代理钩子生效。事件定义参考：[Cursor Hooks](https://prod.cursor.com/docs/hooks)、[Codex Hooks](https://learn.chatgpt.com/docs/hooks)、[Claude Code Hooks](https://code.claude.com/docs/en/hooks)。

## 进阶模式（不需要本应用）

不想装 Pico Vibe 也可以玩：打开磁盘 `/Volumes/PICO_RGB/`，改文件即可控灯。

| 文件 | 作用 |
|---|---|
| `RGB.INI` | 一行文本切换火焰 / 呼吸 / 彩虹等灯效 |
| `RGB.SEQ` | 多步骤自动序列 |
| `LED.BIN` | 自己灌 36 字节帧（适合脚本做律动） |
| `TOUCH.TXT` | 只读触摸事件（单击 / 双击 / 长按） |

说明、全部示例配置和 Python 例程见：

**[→ 进阶模式文档与例程](docs/advanced/README.md)**

```bash
# 火焰示例
cp docs/advanced/examples/rgb/13_fire.ini /Volumes/PICO_RGB/RGB.INI

# 彩虹滚动灌帧
python3 docs/advanced/examples/scripts/led_rainbow.py /Volumes/PICO_RGB/LED.BIN
```

玩进阶文件时建议先退出本应用，避免两边同时写盘。

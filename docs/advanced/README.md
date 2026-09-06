# 进阶模式（不需要 Pico Vibe 应用）

插上模块后，电脑会出现卷标类似 **`PICO_RGB`** 的小磁盘（旧固件可能是 `PICO_RAM`）。  
不用安装 Pico Vibe，也可以直接改盘里的文件控灯、读触摸、拷文件升级。

| 文件 | 方向 | 做什么 |
|---|---|---|
| `RGB.INI` | 电脑 → 设备 | 一行文本切换常驻灯效 |
| `RGB.SEQ` | 电脑 → 设备 | 多步骤自动序列 |
| `LED.BIN` | 电脑 → 设备 | 每帧 36 字节灌灯（12×RGB） |
| `TOUCH.TXT` | 设备 → 电脑 | 触摸手势事件（只读） |
| 官方升级包 | 电脑 → 设备 | 拷进磁盘完成 OTA |

**优先级（从高到低）：** `LED.BIN` → `RGB.SEQ` → `RGB.INI` → 默认彩虹  

建议：玩进阶文件时先退出 Pico Vibe，或关掉应用的灯光写入，避免两边抢写。

macOS 常见路径：`/Volumes/PICO_RGB/`

---

## 三步上手 `RGB.INI`

1. 插入模块，打开 `/Volumes/PICO_RGB/`
2. 把本仓库某个示例复制为根目录的 **`RGB.INI`**（名字必须是这个）
3. 保存后等约 0.1–0.5 秒，灯效应切换

示例：

```bash
cp docs/advanced/examples/rgb/13_fire.ini /Volumes/PICO_RGB/RGB.INI
```

也可用文本编辑器新建 `RGB.INI`，只写一行，例如：

```text
MODE=fire,R=255,G=160,B=40,BR=100,SPD=100
```

`#` 或 `;` 之后是注释。固件只解析第一行。

### 常用模式

| 模式 | 说明 |
|---|---|
| `solid` | 纯色 |
| `off` | 全关 |
| `blink` | 闪烁（可双闪 / 渐变闪） |
| `rainbow` | 彩虹流光（默认） |
| `rainbow_cycle` | 整条同色变色 |
| `chase` / `comet` / `theater` / `wave` / `scan` | 追逐 / 流星 / 跑马 / 波浪 / 扫描 |
| `sparkle` / `twinkle` / `breathe` | 闪星 / 多点闪 / 呼吸 |
| `gradient` / `fire` / `palette` | 双色渐变 / 火焰 / 色表 |

### 常用参数

| 键 | 含义 |
|---|---|
| `BR` | 亮度 0–255 |
| `SPD` | 速度 1–255（越大越快） |
| `DIR` | 方向：`0` 正向，非 0 反向 |
| `R G B` | 主色 |
| `R2 G2 B2` | 次色（`gradient`） |
| `N` / `START` | 灯数 / 起始灯号（分段） |
| `ON` `OFF` `PULSES` `GAP` `FADE` | `blink` 专用 |
| `PAL` | 色表 0–3（彩虹/暖/冰/霓虹） |
| `GAMMA` / `MAXMA` | Gamma 校正 / 电流上限 mA |

可直接复制的示例见 [`examples/rgb/`](examples/rgb/)。

---

## 多步骤序列 `RGB.SEQ`

把示例复制为根目录 **`RGB.SEQ`**：

```bash
cp docs/advanced/examples/rgb_seq/01_heartbeat.seq /Volumes/PICO_RGB/RGB.SEQ
```

```ini
# 0 = 无限循环
LOOP=0
STEP=blink,R=255,G=0,B=0,BR=90,ON=80,OFF=100,PULSES=2,GAP=700,MS=1060
```

| 键 | 含义 |
|---|---|
| `LOOP` | 完整序列循环次数；`0` 无限 |
| `MS` | 本步持续时间（默认 1000） |
| `REPEAT` | 本步重复次数 |
| `TRANS=fade` / `XFADE=` | 跨步淡入 |

最多约 16 步。删除 `RGB.SEQ` 后回到最近一次有效的 `RGB.INI`。

示例：

- [`01_heartbeat.seq`](examples/rgb_seq/01_heartbeat.seq) 心跳双闪
- [`02_status_cycle.seq`](examples/rgb_seq/02_status_cycle.seq) 状态循环
- [`03_segment_warning.seq`](examples/rgb_seq/03_segment_warning.seq) 局部警示闪

---

## 主机灌帧 `LED.BIN`

文件内容固定 **36 字节**：12 颗灯 × `R,G,B`。

存在 `LED.BIN` 时会盖过 `RGB.SEQ` / `RGB.INI`；删掉后自动恢复。

单帧测试（左半红、右半绿）：

```bash
python3 -c 'open("/Volumes/PICO_RGB/LED.BIN","wb").write(bytes([64,0,0]*6+[0,64,0]*6))'
```

彩虹滚动示例：

```bash
python3 docs/advanced/examples/scripts/led_rainbow.py /Volumes/PICO_RGB/LED.BIN
```

写完请 `flush` / `fsync`，否则系统缓存可能延迟生效。

---

## 触摸 `TOUCH.TXT`（只读）

设备写入固定 64 字节文本，主机只读轮询：

```text
seq=3
evt=click
t_ms=12540
```

| `evt` | 含义 |
|---|---|
| `none` | 无事件 |
| `click` | 单击 |
| `dblclick` | 双击 |
| `long` | 长按（约 ≥800 ms） |

只在 `seq` 变大时处理新事件。建议轮询间隔 200–500 ms。  
**不要删除**该文件当确认，也不要往里写。

监控示例：

```bash
python3 docs/advanced/examples/scripts/touch_monitor.py /Volumes/PICO_RGB/TOUCH.TXT
```

按下时设备会短暂显示水纹反馈，结束后恢复原先灯效。

---

## 拷文件升级（OTA）

将官方发布的升级包拷到磁盘根目录，设备会自动校验并升级。  
升级过程中勿断电、勿拔卡。失败时正式版有试运行保护可回退。  
请只用官方包，勿自行擦写安全区。

---

## 注意

- 这是小容量配置盘，**不是存储卡**，请勿存照片或重要资料。
- 断电/重插后日常文件可能按固件策略重建。
- 完整 AI / 系统状态 / 音乐律动体验仍建议使用 [Pico Vibe 应用](../../README.md)。
- 硬件兼容范围以产品说明为准（当前销售主推带 SD 卡槽的 MacBook Pro · M 系列）。

[English](README.en.md)

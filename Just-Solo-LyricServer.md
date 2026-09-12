# Just Solo LyricServer 协议 v1.2.0

## 简介

Just Solo LyricServer 协议 是 Just Solo 内置的实时歌词推送协议，基于 WebSocket（`ws://127.0.0.1:47290`），将播放器中当前歌曲的歌词时间轴、播放进度和实时音频频谱以 JSON 单向推送给本地客户端。适用于桌面歌词悬浮窗、频谱可视化等任意需实时同步播放数据的场景。

---

## 目录

1. [概述](#1-概述)
2. [连接规范](#2-连接规范)
3. [歌词数据源](#3-歌词数据源)
4. [消息类型](#4-消息类型)
   - [4.1 `init` — 歌词时间轴](#41-init--歌词时间轴)
   - [4.2 `progress` — 播放进度](#42-progress--播放进度)
   - [4.3 `playback` — 播放状态变更](#43-playback--播放状态变更)
   - [4.4 `spectrum` — 音频频谱](#44-spectrum--音频频谱)
5. [客户端状态机](#5-客户端状态机)
6. [客户端实现指南](#6-客户端实现指南)
   - [6.1 歌词行定位（二分查找）](#61-歌词行定位二分查找)
   - [6.2 平滑插值](#62-平滑插值)
   - [6.3 滚动行为](#63-滚动行为)
   - [6.4 重连策略](#64-重连策略)
   - [6.5 频谱渲染（协议v1.2.0+）](#65-频谱渲染协议v120)
7. [多语言客户端示例](#7-多语言客户端示例)
   - [7.1 JavaScript（浏览器）](#71-javascript浏览器)
   - [7.2 Python](#72-python)
   - [7.3 C#](#73-c)
8. [故障排查](#8-故障排查)
9. [版本历史](#9-版本历史)

---

## 1. 概述

Just Solo LyricServer 是 Just Solo 音乐播放器内置的 **单向 WebSocket 歌词推送服务**。它在本地回环地址上监听，将当前播放歌曲的歌词时间轴、播放进度和实时音频频谱推送给连接的客户端。

```
┌──────────────────┐      WebSocket JSON       ┌─────────────────┐
│   Just Solo      │ ◄══════════════════════►  │  第三方客户端    │
│   (播放器)        │     ws://127.0.0.1:47290   │  (桌面歌词等)    │
│                  │     单向推送 (→ only)       │                 │
│  ┌────────────┐  │                           └─────────────────┘
│  │MusicManager│──┼── signals ──► LyricServer
│  └────────────┘  │
└──────────────────┘
```

**核心设计原则：**

| 原则 | 说明 |
|------|------|
| 单向推送 | 客户端只接收消息，不发送任何指令；所有播放控制通过 Just Solo 本体 |
| 本地环回 | 仅监听 `127.0.0.1`，不暴露到局域网或公网 |
| 最小开销 | `progress` / `spectrum` 每帧约 40–100 字节 JSON；无客户端时自动停推 |
| 即连即用 | 连接后立即补推当前完整状态，零握手 |
| 无状态服务端 | 不维护客户端会话，任意数量客户端并发连接 |
| 客户端名称（协议v1.1.0+） | 客户端可在连接后发送可选的 `hello` 消息声明名称；旧客户端不发送也完全兼容 |
| 实时频谱（协议v1.2.0+） | 播放时以 100ms 周期推送 `spectrum` 消息（12 频段电平 0~1）；暂停 / 无客户端自动停推 |

---

## 2. 连接规范

### 2.1 连接参数

```
ws://127.0.0.1:47290
```

| 参数 | 值 |
|------|-----|
| 协议 | WebSocket（RFC 6455），非加密 `ws://` |
| 地址 | `127.0.0.1`（仅 IPv4 本地环回） |
| 端口 | `47290` |
| 数据格式 | UTF-8 编码的 JSON 文本帧（opcode 0x1） |
| 最大客户端数 | 无硬限制（由操作系统文件描述符决定） |
| 子协议 | 不要求（`Sec-WebSocket-Protocol` 头部忽略） |

### 2.2 握手

标准 WebSocket 握手，无需额外头部。服务端不验证 `Origin`。

**客户端请求示例：**
```
GET / HTTP/1.1
Host: 127.0.0.1:47290
Upgrade: websocket
Connection: Upgrade
Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==
Sec-WebSocket-Version: 13
```

### 2.3 客户端名称声明（协议v1.1.0+，可选）

客户端在 WebSocket 连接建立后，可发送一条 `hello` 消息声明自己的名称。Just Solo 收到后会弹出通知提示用户。

```json
{"type":"hello","client":"我的桌面歌词"}
```

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `type` | `string` | 是 | 固定 `"hello"` |
| `client` | `string` | 是 | 客户端名称，任意 UTF-8 文本 |

**向下兼容：** 旧客户端不发送 `hello` 消息也能正常使用所有推送功能。Just Solo 会在连接 500ms 后按"未知客户端"通知用户。

### 2.4 Ping / Pong

服务端 **不主动发送** Ping 帧，也不要求客户端发 Pong。断线检测依赖 TCP 层面的 `disconnected` 信号、操作系统 TCP keep-alive（默认 2 小时）及客户端自身的重连逻辑。

### 2.5 端口冲突

如果 `47290` 端口已被占用，`LyricServer::start()` 返回 `false`，Just Solo 仍正常运行但 `lyricServer.running === false`。设置页"关于"中将显示「未启用」。

---

## 3. 歌词数据源

LyricServer 推送的歌词来自 Just Solo 的 `MusicManager`，后者通过以下优先级加载歌词：

### 3.1 加载优先级

| 优先级 | 来源 | 说明 |
|--------|------|------|
| 1 | 外部 `.lrc` 文件 | 与音频文件同目录、同名的 `.lrc`（大小写不敏感） |
| 2 | 嵌入式歌词 | 音频文件内嵌的 `LYRICS` 标签（ID3v2 USLT / VorbisComment LYRICS），异步读取 |
| 3 | 纯文本降级 | 嵌入式歌词若无时间标签，按行拆分，依歌曲总时长均匀分配时间戳 |

### 3.2 LRC 格式解析规则

服务端按以下规则解析标准 LRC 文件（UTF-8 编码，忽略 BOM）：

**时间标签正则：**
```
\[(\d{1,3}):(\d{1,3})(?:\.(\d{1,3}))?\]
```
支持 `[mm:ss]` 和 `[mm:ss.xx]` 和 `[mm:ss.xxx]` 三种精度。

**元数据标签跳过：**
```
[ti:标题] [ar:艺术家] [al:专辑] [by:作者] [offset:偏移] [length:长度]
```
这些行不产生歌词条目。

**翻译识别：**
- 同一时间戳出现多行时，自动检测第二行是否为翻译（拉丁字母占主导 → 翻译；否则合并为一行，`\n` 分隔）。
- 翻译文本写入 `translation` 字段，不占用独立歌词行。

**多时间标签：**
```lrc
[00:15.00][00:30.00]重复的副歌
```
展开为两条独立条目。

**示例 LRC 文件：**
```lrc
[ti:光辉岁月]
[ar:Beyond]
[00:00.00]
[00:15.50]钟声响起归家的讯号
[00:15.50]The bell rings for home
[00:22.30]在他生命里
[00:22.30]In his life
[00:30.00]仿佛带点唏嘘
[00:30.00]As if with some sigh
```

解析结果（`init` 消息的 `lyrics` 数组）：
```json
[
  { "time": 15500, "text": "钟声响起归家的讯号", "translation": "The bell rings for home" },
  { "time": 22300, "text": "在他生命里",           "translation": "In his life" },
  { "time": 30000, "text": "仿佛带点唏嘘",         "translation": "As if with some sigh" }
]
```

### 3.3 歌词偏移

Just Solo 设置页提供「歌词预读偏移」滑块（范围 50–350ms，默认 130ms）。该偏移在 LyricServer 内部**已生效**——`init` 的 `time` 字段是原始 LRC 时间戳，客户端收到的 `progress.position` 是播放器实际位置；Just Solo 自身的歌词高亮用 `musicManager.lyricOffset` 做内部补偿，**客户端如需与 Just Solo 完全同步高亮，应在前端自行实现相同的偏移逻辑**。

> 偏移默认值 130ms 来自经验值——歌词显示稍早于人耳感知，阅读更舒适。

---

## 4. 消息类型

### 消息通用格式

每条消息是一个单行 JSON 对象（Compact，无缩进），顶层必含 `type` 字段。

```json
{"type":"...", ...}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `type` | `string` | 消息类型标识：`init` / `progress` / `playback` / `spectrum` |

---

### 4.1 `init` — 歌词时间轴

#### 触发条件

| 场景 | 行为 |
|------|------|
| 切歌（播放下一首、双击曲目等） | 当前歌词变为新歌 → 广播 `init` |
| 歌词异步加载完成（嵌入式） | `currentLyricsChanged` 信号触发 → 广播 `init` |
| 新客户端连接 | 立即单独推送当前歌词（不广播） |

#### 消息结构

```json
{
  "type": "init",
  "lyrics": [
    { "time": 0,     "text": "♪ 纯音乐前奏" },
    { "time": 12450, "text": "第一行歌词" },
    { "time": 18200, "text": "第二行歌词", "translation": "Line two translation" }
  ]
}
```

#### 字段规范

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `type` | `string` | 是 | 固定 `"init"` |
| `lyrics` | `array<object>` | 是 | 歌词行数组，按 `time` 严格升序 |
| `lyrics[].time` | `int` | 是 | 该行起始时间戳，单位毫秒，非负整数 |
| `lyrics[].text` | `string` | 是 | 歌词文本，可能为空字符串（纯间奏标记） |
| `lyrics[].translation` | `string` | 否 | 翻译文本，仅当 LRC 文件含翻译时存在 |

#### 边界情况

| 场景 | `lyrics` 内容 |
|------|---------------|
| 歌曲有完整 LRC 歌词 | 时间戳 + 文本 + 翻译（如有） |
| 歌曲只有嵌入式文本（无时间戳） | 按歌曲时长均匀分配时间戳 |
| 纯音乐（无任何歌词） | `[]`（空数组） |
| 歌词正在异步加载（嵌入式） | 先推 `[]`，加载完成后推完整数组 |

#### `translation` 字段

仅当 LRC 文件中同一时间戳存在第二行，且被判定为翻译时，该字段才出现。客户端可据此实现双语歌词显示。

```json
{
  "type": "init",
  "lyrics": [
    { "time": 1200,  "text": "あの日の景色が",    "translation": "那天的景色" },
    { "time": 3500,  "text": "今も胸に焼き付いて", "translation": "至今深印心中" }
  ]
}
```

---

### 4.2 `progress` — 播放进度

#### 触发条件

| 场景 | 行为 |
|------|------|
| 播放中 | 每 400ms（±5ms 系统调度误差）广播一帧 |
| 暂停 | 立即停止推送 |
| 停止 / 切歌 | 停止推送 |
| 恢复播放 | 立即推一帧 + 恢复 400ms 周期 |
| 无客户端连接 | 定时器自动停转 |

#### 消息结构

```json
{
  "type": "progress",
  "position": 15680
}
```

#### 字段规范

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `type` | `string` | 是 | 固定 `"progress"` |
| `position` | `int` | 是 | 当前播放位置，单位毫秒，自曲目开头起算 |

#### 精度说明

- 时间来源：Qt `QMediaPlayer::position()`，精度取决于后端（Windows Media Foundation 通常 ±10ms）
- 推送间隔：400ms（不是 position 本身的精度窗口）
- 首次恢复播放时，`progress` 在 `playback` 消息**之后**立即发出，不等 400ms 定时器
- `progress` 与 `spectrum`（见 [4.4](#44-spectrum--音频频谱)）各自独立推送、互不影响

---

### 4.3 `playback` — 播放状态变更

#### 触发条件

| 场景 | 行为 |
|------|------|
| 用户点击播放 / 暂停 / 快捷键 | 广播 `playback` |
| 曲目自然结束 → 下一首 | 先 `init`（新歌词），`playback` 状态保持 `"playing"` |
| 新客户端连接 | 立即单独推送当前状态 |

#### 消息结构

```json
{
  "type": "playback",
  "status": "playing"
}
```

#### 字段规范

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `type` | `string` | 是 | 固定 `"playback"` |
| `status` | `string` | 是 | `"playing"` 或 `"paused"` |

> 协议不包含 `"stopped"` 状态——Just Solo 不区分暂停与停止（停止 = 暂停 + 进度归零 + 从头播放）。

---

### 4.4 `spectrum` — 音频频谱（协议v1.2.0+）

#### 触发条件

| 场景 | 行为 |
|------|------|
| 播放中 | 每 100ms（±5ms 系统调度误差）广播一帧 |
| 暂停 / 停止 / 切歌 | 立即停止推送；客户端应在收到 `playback("paused")` 时清空频谱显示 |
| 恢复播放 | 立即推一帧 + 恢复 100ms 周期 |
| 无客户端连接 | 定时器自动停转 |

#### 消息结构

```json
{
  "type": "spectrum",
  "bands": [0.12, 0.35, 0.58, 0.71, 0.66, 0.49, 0.38, 0.29, 0.21, 0.15, 0.12, 0.08]
}
```

#### 字段规范

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `type` | `string` | 是 | 固定 `"spectrum"` |
| `bands` | `array<number>` | 是 | 12 个频段电平（0.0 ~ 1.0），从低频到高频排列；暂无数据时为空数组 `[]` |

#### 频段说明

- 数据来源：Just Solo `AudioEngine` 对实际输出帧做 FFT（1024 点 Hann 窗），按对数刻度聚合为 12 个频段（约 40Hz ~ 16kHz），各频段取峰值。
- 取值范围：0.0 ~ 1.0（满幅正弦 ≈ 1.0），保留 3 位小数。
- 数量固定：始终 12 个元素（v1.2.0 起固定），客户端可直接按 12 柱渲染。
- 与播放进度解耦：`spectrum` 与 `progress` 各自独立推送，客户端可分开使用。

#### 边界情况

| 场景 | `bands` 内容 |
|------|---------------|
| 播放中（有真实频谱数据） | 12 个 0.0 ~ 1.0 的数值 |
| 刚恢复播放（音频缓冲未填满） | 可能为空数组 `[]` |
| 暂停 / 停止 | 不再推送（客户端配合 `playback("paused")` 清空） |

---

## 5. 客户端状态机

```
                     ┌──────────┐
           ┌────────│  DISCONNECTED  │
           │        └──────┬─────────┘
           │               │ connect()
           │        ┌──────▼─────────┐
           │  ┌─────│  CONNECTING    │──── 连接失败 ──► DISCONNECTED
           │  │     └──────┬─────────┘
           │  │            │ onopen
           │  │     ┌──────▼─────────┐
           │  │     │   CONNECTED    │
           │  │     └──┬──────┬──────┘
           │  │        │      │
           │  │   ← init    ← playback     （立即收到）
           │  │   ← progress / spectrum（若播放中）
           │  │        │      │
           │  │   ┌────▼──────▼──────┐
           │  │   │    RUNNING       │◄── 每 400ms: progress / 每 100ms: spectrum
           │  │   └────┬──────┬──────┘◄── 事件驱动: init / playback
           │  │        │      │
           │  │   切歌: init  暂停: playback("paused")
           │  │   恢复: playback("playing") + progress + spectrum
           │  │        │      │
           │  │   ┌────▼──────▼──────┐
           │  └──►│   DISCONNECTED   │  （ws 断开 / 错误）
           │      └─────────────────┘
           │               │
           └── 重连（退避） ──┘
```

**状态转换触发条件：**

| 转换 | 触发 |
|------|------|
| DISCONNECTED → CONNECTING | 客户端主动 `new WebSocket(url)` |
| CONNECTING → CONNECTED | WebSocket `onopen` |
| CONNECTING → DISCONNECTED | 连接超时或 `onerror` |
| CONNECTED → RUNNING | 收到至少一条消息后 |
| RUNNING → DISCONNECTED | `onclose` / `onerror` |

---

## 6. 客户端实现指南

### 6.1 歌词行定位（二分查找）

服务端每 400ms 推送 `progress`。客户端需要在 `lyrics` 数组中定位当前位置对应的歌词行。

**算法：**二分查找最后一个 `time <= position` 的索引。

```
lyrics: [0, 5000, 10000, 15000, 20000]
position: 12500

lo=0 hi=4 mid=2: lyrics[2].time=10000 <= 12500 ✓ → ans=2, lo=3
lo=3 hi=4 mid=3: lyrics[3].time=15000 > 12500  ✗ → hi=2
lo > hi → 返回 ans=2（"10000: 第三行"）
```

**时间复杂度：** O(log n)，500 行的歌词约 9 次比较。

**伪代码：**
```
function findCurrentLine(lyrics, position):
    lo = 0, hi = lyrics.length - 1, ans = -1
    while lo <= hi:
        mid = (lo + hi) // 2
        if lyrics[mid].time <= position:
            ans = mid; lo = mid + 1
        else:
            hi = mid - 1
    return ans
```

> 若 `ans === -1`：歌曲尚未到达第一行歌词（前奏），高亮第一行或不亮均可。

### 6.2 平滑插值

`progress` 推送间隔 400ms。若 UI 需要更高帧率（如 60fps 的平滑滚动），客户端可对 `position` 做线性插值：

```
estimatedPosition = lastPosition + (Date.now() - lastTimestamp)
```

- `lastPosition`：最近一次 `progress` 的 `position` 值
- `lastTimestamp`：收到该 `progress` 时的 `performance.now()` 或 `Date.now()`
- 收到新 `progress` 时重置；`playback("paused")` 时冻结

### 6.3 滚动行为

歌词显示逐行滚动的推荐策略：

| 策略 | 适用场景 | 说明 |
|------|----------|------|
| 即时跳转 | 移动端 / 性能受限 | 当前行改变时 `scrollIntoView({ behavior: "smooth" })` |
| 匀速滚动 | 桌面端 / 高质量体验 | 用 `requestAnimationFrame` 驱动，基于 `position` 线性推进 |
| 居中锁定 | karaoke 风格 | 当前行始终固定在视口中央 |

### 6.4 重连策略

**推荐指数退避：**

```
初始延迟    1 秒
最大延迟   30 秒
退避因子   2×
```

```
连接断开 → 1s → 失败 → 2s → 失败 → 4s → ... → 30s → 30s...
连接成功 → 重置延迟为 1s
```

服务端不提供重连服务，重连完全由客户端负责。

### 6.5 频谱渲染（协议v1.2.0+）

`spectrum` 推送周期 100ms（10fps）。客户端渲染频谱柱的推荐策略：

| 场景 | 处理 |
|------|------|
| 收到 `spectrum` 且 `bands` 非空 | 将 12 个 0.0~1.0 数值直接映射为柱高（0.0=0%、1.0=100%） |
| 收到 `spectrum` 且 `bands` 为空 `[]` | 保持当前画面或短暂归零（刚恢复播放时缓冲未填满） |
| 收到 `playback("paused")` | 清空频谱（柱高归零），不依赖最后一帧残留 |
| 渲染帧率 | 频谱动画建议用 `requestAnimationFrame` 在 10fps 数据间做插值/衰减，避免柱高跳变 |

> 若客户端仅需要歌词，忽略 `spectrum` 消息即可，不影响其他功能。

---

## 7. 多语言客户端示例

### 7.1 JavaScript（浏览器）

```javascript
class LyricClient {
  constructor(url = 'ws://127.0.0.1:47290') {
    this.url = url;
    this.lyrics = [];
    this.position = 0;
    this.spectrum = [];
    this.isPlaying = false;
    this.reconnectDelay = 1000;
    this.connect();
  }

  connect() {
    this.ws = new WebSocket(this.url);
    this.ws.onopen = () => {
      this.reconnectDelay = 1000;
      this.onStatusChange('connected');
      // v1.1.0+: 声明客户端名称（可选，向下兼容）
      this.ws.send(JSON.stringify({ type: 'hello', client: '我的桌面歌词' }));
    };
    this.ws.onmessage = (ev) => {
      const msg = JSON.parse(ev.data);
      switch (msg.type) {
        case 'init':
          this.lyrics = msg.lyrics || [];
          this.onLyricsChanged(this.lyrics);
          break;
        case 'progress':
          this.position = msg.position;
          this.onProgress(this.position);
          break;
        case 'playback':
          this.isPlaying = (msg.status === 'playing');
          this.onPlaybackChange(this.isPlaying);
          break;
        case 'spectrum':   // v1.2.0+
          this.spectrum = msg.bands || [];
          this.onSpectrumChange(this.spectrum);
          break;
      }
    };
    this.ws.onclose = () => {
      this.onStatusChange('disconnected');
      setTimeout(() => this.connect(), this.reconnectDelay);
      this.reconnectDelay = Math.min(this.reconnectDelay * 2, 30000);
    };
    this.ws.onerror = () => this.ws.close();
  }

  // 二分查找当前歌词行
  getCurrentLineIndex() {
    let lo = 0, hi = this.lyrics.length - 1, ans = -1;
    while (lo <= hi) {
      const mid = (lo + hi) >> 1;
      if (this.lyrics[mid].time <= this.position)
        { ans = mid; lo = mid + 1; }
      else
        hi = mid - 1;
    }
    return ans;
  }

  // ---- 以下为回调，由子类或外部覆写 ----
  onLyricsChanged(lyrics) {}
  onProgress(position) {}
  onPlaybackChange(playing) {}
  onSpectrumChange(bands) {}   // v1.2.0+：12 个 0~1 频段电平
  onStatusChange(status) {}
}
```

### 7.2 Python

```python
import asyncio
import json
import websockets

class LyricClient:
    def __init__(self, url="ws://127.0.0.1:47290"):
        self.url = url
        self.lyrics = []
        self.position = 0
        self.spectrum = []
        self.is_playing = False
        self._reconnect_delay = 1

    async def connect(self):
        while True:
            try:
                async with websockets.connect(self.url) as ws:
                    self._reconnect_delay = 1
                    self.on_status_change("connected")
                    # v1.1.0+: 声明客户端名称（可选，向下兼容）
                    await ws.send(json.dumps({"type": "hello", "client": "我的桌面歌词"}))
                    async for raw in ws:
                        msg = json.loads(raw)
                        await self._handle(msg)
            except (OSError, websockets.ConnectionClosed):
                self.on_status_change("disconnected")
                await asyncio.sleep(self._reconnect_delay)
                self._reconnect_delay = min(self._reconnect_delay * 2, 30)

    async def _handle(self, msg):
        t = msg["type"]
        if t == "init":
            self.lyrics = msg.get("lyrics", [])
            await self.on_lyrics_changed(self.lyrics)
        elif t == "progress":
            self.position = msg["position"]
            await self.on_progress(self.position)
        elif t == "playback":
            self.is_playing = (msg["status"] == "playing")
            await self.on_playback_change(self.is_playing)
        elif t == "spectrum":  # v1.2.0+
            self.spectrum = msg.get("bands", [])
            await self.on_spectrum_change(self.spectrum)

    def current_line_index(self):
        lo, hi, ans = 0, len(self.lyrics) - 1, -1
        while lo <= hi:
            mid = (lo + hi) // 2
            if self.lyrics[mid]["time"] <= self.position:
                ans, lo = mid, mid + 1
            else:
                hi = mid - 1
        return ans

    async def on_lyrics_changed(self, lyrics): pass
    async def on_progress(self, position): pass
    async def on_playback_change(self, playing): pass
    async def on_spectrum_change(self, bands): pass
    def on_status_change(self, status): pass

# 使用
if __name__ == "__main__":
    client = LyricClient()
    asyncio.run(client.connect())
```

> 依赖：`pip install websockets`

### 7.3 C#

```csharp
using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public class LyricClient : IDisposable
{
    private readonly string _url;
    private ClientWebSocket _ws;
    private CancellationTokenSource _cts;

    public LyricEntry[] Lyrics { get; private set; } = Array.Empty<LyricEntry>();
    public int Position { get; private set; }
    public double[] Spectrum { get; private set; } = Array.Empty<double>();
    public bool IsPlaying { get; private set; }
    public bool Connected => _ws?.State == WebSocketState.Open;

    public event Action<LyricEntry[]> OnLyricsChanged;
    public event Action<int> OnProgress;
    public event Action<bool> OnPlaybackChanged;
    public event Action<double[]> OnSpectrumChanged;
    public event Action<bool> OnConnectionChanged;

    public LyricClient(string url = "ws://127.0.0.1:47290")
    {
        _url = url;
    }

    public async Task ConnectAsync()
    {
        var delay = 1000;
        while (!_cts?.IsCancellationRequested ?? true)
        {
            try
            {
                _cts = new CancellationTokenSource();
                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(new Uri(_url), _cts.Token);
                OnConnectionChanged?.Invoke(true);
                delay = 1000;

                // v1.1.0+: 声明客户端名称（可选，向下兼容）
                var hello = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new { type = "hello", client = "我的桌面歌词" }));
                await _ws.SendAsync(hello, WebSocketMessageType.Text, true, _cts.Token);

                var buffer = new byte[4096];
                var sb = new StringBuilder();

                while (_ws.State == WebSocketState.Open)
                {
                    var result = await _ws.ReceiveAsync(buffer, _cts.Token);
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (result.EndOfMessage)
                    {
                        HandleMessage(sb.ToString());
                        sb.Clear();
                    }
                }
            }
            catch
            {
                OnConnectionChanged?.Invoke(false);
                await Task.Delay(delay);
                delay = Math.Min(delay * 2, 30000);
            }
        }
    }

    private void HandleMessage(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var type = doc.RootElement.GetProperty("type").GetString();
        switch (type)
        {
            case "init":
                Lyrics = JsonSerializer.Deserialize<LyricEntry[]>(
                    doc.RootElement.GetProperty("lyrics").GetRawText());
                OnLyricsChanged?.Invoke(Lyrics);
                break;
            case "progress":
                Position = doc.RootElement.GetProperty("position").GetInt32();
                OnProgress?.Invoke(Position);
                break;
            case "playback":
                IsPlaying = doc.RootElement.GetProperty("status").GetString() == "playing";
                OnPlaybackChanged?.Invoke(IsPlaying);
                break;
            case "spectrum":  // v1.2.0+
                Spectrum = JsonSerializer.Deserialize<double[]>(
                    doc.RootElement.GetProperty("bands").GetRawText());
                OnSpectrumChanged?.Invoke(Spectrum);
                break;
        }
    }

    public int CurrentLineIndex()
    {
        int lo = 0, hi = Lyrics.Length - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Lyrics[mid].Time <= Position) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _ws?.Dispose();
    }
}

public class LyricEntry
{
    public int Time { get; set; }
    public string Text { get; set; }
    public string Translation { get; set; }
}
```

> 依赖：`System.Net.WebSockets`（.NET Framework 4.5+ / .NET Core 2.0+）

---

## 8. 故障排查

### 8.1 连接失败

| 症状 | 可能原因 | 解决 |
|------|----------|------|
| `WebSocket connection refused` | Just Solo 未运行，或 LyricServer 启动失败 | 检查任务管理器是否有 `JustSolo.exe` 进程；查看 Just Solo 设置 → 关于 → LyricServer 状态 |
| `WebSocket connection timeout` | 防火墙拦截本地回环 | Windows 防火墙通常不拦截 127.0.0.1；检查是否有第三方安全软件 |
| 连接成功但无消息 | 播放器未播放任何歌曲 | 播放一首歌后应收到 `init` |

### 8.2 端口占用

```powershell
# 查看 47290 端口占用
netstat -ano | findstr 47290
```

若被其他进程占用，LyricServer 自动降级（`running = false`），Just Solo 正常运行。如需更改端口，修改 `main.cpp` 中 `lyricServer->start(47290)` 的参数。

### 8.3 歌词不更新

| 症状 | 原因 |
|------|------|
| `init` 收到但 `lyrics` 为空 `[]` | 歌曲无 LRC 文件且无嵌入式歌词 |
| 切歌后 `init` 不变 | 异步加载嵌入式歌词中，等待 `currentLyricsChanged` 二次触发 |
| 进度超过最后一行后高亮消失 | 正常——歌曲结束后无对应行；客户端应保持最后一行高亮 |

---

## 9. 版本历史

> 除特殊说明，所有版本向下兼容。

| 版本 | 日期 | 变更 |
|------|------|------|
| v1.2.0 | 2026-08-14 | 新增 `spectrum` 消息：播放时以 100ms 周期推送 12 频段实时音频频谱（0.0~1.0，40Hz~16kHz 对数分布），用于解决独占音频时其他软件无法获取频谱的问题；`progress` 推送间隔按 v1.0.2 修订为 400ms；向下兼容旧客户端（未知消息类型忽略） |
| v1.1.0 | 2026-08-05 | 新增可选 `hello` 消息：客户端可声明名称，Just Solo 弹窗提示第三方连接；向下兼容旧客户端 |
| v1.0.2 | 2026-07-26 | 将`progress` 推送间隔从 300ms 变更为 400ms，修复进度更新过快问题 |
| v1.0.1 | 2026-07-25 | 修复Just Solo内置的LyricServer问题：将`progress` 推送间隔从 200ms 更改为 300ms，修复进度更新过快问题 |
| v1.0.0 | 2026-07-24 | 初始发布；`init`（lyrics 含 translation 字段）+ `progress`（200ms）+ `playback`（playing/paused） |

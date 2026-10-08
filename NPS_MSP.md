# NotchPeninsula MSP 集成

NotchPeninsula 的灵动岛是一个 MSP 节点：**`notchpeninsula`**。

接上它，你能做三件事：

- **让它显示** —— 弹一条通知到岛顶，可选带图标
- **让它干活** —— 读 / 控系统媒体，健康检查
- **听它说话** —— 系统通知、媒体变化会自动广播出来

共享目录**默认就是 `~/.msp/nodes`**，两边一致即可，不用配任何东西。

> ⚠️ **这个功能是实验性的，而且默认关闭。** 先在 **设置 → 通用设置 → 「MSP 信号总线（实验性）」** 里打开，
> 否则节点根本不会启动（设置里改完立刻生效，不用重启）。默认关是因为它会**监听一个本地端口**，
> 并让本机任何 MSP 程序都能弹通知、读控系统媒体 —— 这是对外暴露的接口面，得你自己点头。

> 协议本身（信号格式、握手、心跳、通配符规则）见 C# SDK 在 nuget.org 上的
> [包说明](https://www.nuget.org/packages/ModelSensingProtocol)，里面有一节「线上格式」讲得很清楚。
> 本文只讲 `notchpeninsula` 这一个节点怎么用。

---

## 一、连上它

先装库。

**C#**（需要 .NET 10 SDK）—— 包已发布在 nuget.org：

```bash
dotnet add package ModelSensingProtocol --version 0.1.0-alpha.1
```

> 目前只有 `0.1.0-alpha.1` 这一个预发布版本，所以要显式写 `--version`。

**Python**（需要 3.10 或更高）—— 参考实现暂未公开发布，`pip install msp-py` 装不到。
如果你在测试群里，用群文件里那个 whl：

```bash
pip install msp_py-0.1.0-py3-none-any.whl
```

> 分发包名是 `msp-py`，但 **import 的名字是 `msp`**（写代码时 `import msp`）。依赖 `watchdog` 会自动装上。

然后连线。共享目录**默认就是 `~/.msp/nodes`**，两边一致即可，不用配任何东西：

```python
import time
from msp import MSPNode, Preset

node = MSPNode("my_app", preset=Preset.LOCAL_AI)
node.start()

deadline = time.time() + 15
while time.time() < deadline and "notchpeninsula" not in node.connections():
    time.sleep(0.3)
time.sleep(1.5)   # 别急着调用，见第六节

print(node.call("notchpeninsula", "ping", {}).unwrap())
node.stop()
```

C#：

```csharp
using Msp.Core;

var node = new MspNode("my_app", Preset.LocalAi);
node.Start();
Thread.Sleep(3000);
Console.WriteLine(node.Call("notchpeninsula", "ping").Structured?.GetRawText());
node.Stop();
```

连上之后可以自省一下对端有哪些能力：

```python
peer = node.peer("notchpeninsula")
peer.tools()      # ['ping', 'notify', 'media_status', 'media_control']
peer.signals()    # 4 条广播信号，见第四节
peer.patterns()   # ['msp/request-reply', 'msp/publish-subscribe']
```

> 节点 id 要符合 `[a-z0-9_-]+`。

---

## 二、让它弹一条通知

```python
node.call("notchpeninsula", "notify", {
    "kind":  "我的应用",       # 显示成通知的「来源」
    "title": "构建完成",
    "body":  "3 个任务通过",
    "content": [{"type": "resource_link", "uri": "qq"}],   # 可选：图标
}, timeout=15)
```

返回：

```json
{ "ok": true, "kind": "我的应用", "source": "我的应用",
  "title": "构建完成", "body": "3 个任务通过", "icon": "qq", "blocks": 0 }
```

- `content` **可选** —— 纯文字通知不用给，这是最常见的情况。
- **立刻返回**，不会等通知显示完（`duration` 给 60 秒也是 0.6 ms）。回执语义是「已受理」。
- ⚠️ Python 的 `call(peer_id, tool, args, timeout=5.0)` 里 **`args` 是必填的**，没有形参的工具也要传 `{}`。

### 载荷约定

信号 payload 与 `notify` 工具形参**共用同一套形状**：

```json
{
  "kind":     "来源标识",
  "title":    "标题",
  "body":     "正文",
  "duration": 6,
  "content":  [ ...content block... ]
}
```

| 字段 | 说明 |
| --- | --- |
| `kind` | 显示成通知的「来源」标签。不给的话，工具调用显示 `MSP`，总线信号回退成发布者节点 id |
| `title` / `body` | 文字。两个都为空时，会拿 `content` 里第一个 `text` 块当正文 |
| `duration` | 显示秒数，1~60，默认 6 |
| `content` | MSP/MCP 的 content block 数组，见下 |

### 图标（可选）

**不给图标是最普遍的情况** —— 省略 `content` 就行，会用默认图标。

要给图标，就在 `content` 里放一个图像块：

```json
{ "type": "resource_link", "uri": "qq" }
```

```json
{ "type": "image", "data": "<base64>", "mimeType": "image/png" }
```

- `resource_link.uri` 直接当图标来源。认：**内置别名**（如 `qq`、`windows`）、图片链接、`file://`、本地路径。
- `image` 是内嵌 base64，不想依赖外部文件时用。
- 有多个图像块时，标了 `annotations.role = "icon"` 的那个优先；都没标就取第一个。

> 其余 content block（`text` / `audio` / `resource` / `blob_ref`）以及**不认识的 `type`**，宿主画不出来就跳过，**不会**让整条通知失败。

---

## 三、其余三个工具

| 工具 | 形参 | 说明 |
| --- | --- | --- |
| `ping` | — | 健康检查：节点信息、预设、能力、已连对端 |
| `media_status` | — | 只读媒体状态：`active` / `playing` / `title` / `artist` / `elapsed` / `progress` |
| `media_control` | `action` | `toggle` / `play` / `pause` / `next` / `prev` |

```python
node.call("notchpeninsula", "media_status", {}).unwrap()
node.call("notchpeninsula", "media_control", {"action": "next"})
```

---

## 四、听它广播

4 条信号，连上就能收到（广播只发给已连接的对端）：

| 信号 | 什么时候发 |
| --- | --- |
| `notify.toast.raised` | 系统通知（Windows Toast）到达 |
| `notify.notchpeninsula.reminder.raised` | 插件提醒（宿主内部） |
| `media.track.changed` | 换曲 |
| `media.playback.changed` | 播放 / 暂停 / 会话有无变化 |

```python
@node.on("notify.**")
def on_notify(sig):
    print(sig.path, sig.payload)

@node.on("media.**")
def on_media(sig):
    print(sig.path, sig.payload)
```

`notify.toast.raised` 长这样（`content` 里是通知图标）：

```json
{ "kind": "微信",
  "title": "张小明",
  "body": "在吗？",
  "content": [{"type": "image", "data": "<base64>", "mimeType": "image/png",
               "annotations": {"role": "icon"}}],
  "aumid": "WeChat.exe",
  "receivedAt": "2026-10-08T13:08:18Z" }
```

---

## 五、让它显示你自己的通知

往 `notify.**` 发一条就行，**任意深度**都可以：

```python
node.publish("notify.myapp.done", {
    "kind":  "我的应用",
    "title": "构建完成",
    "body":  "3 个任务通过",
})
```

它会按第二节那套载荷解析，然后显示在岛上。

**两条路别搞混**：

| 你想干什么 | 用哪个 |
| --- | --- |
| 只让这一台岛弹一下 | `notify` **工具**（点对点，不上总线） |
| 让所有订阅者都看得见 | `publish("notify.xxx")` |

> 别把收到的总线通知再转发出去 —— 两个都这么干的节点会把对方的通知无限互转。

---

## 六、常见问题

**`调用 notify 超时` / 日志里一条 `入站握手失败`** —— 同一个根因：刚连上就立刻调用，撞上了协议层的连接去重（两侧都是 eager，会关掉其中一条连接）。等 1~2 秒再调，或给首次调用加重试。

**失败不是 `is_error`** —— 工具失败返回 `{"ok": false, "error": "…"}`，而 `is_error` 一直是 `false`。判失败请看 `structured.ok`。

**图标没出来** —— 图标解析失败不会让通知失败，只会回退默认图标。看 `notify` 回执里的 `icon` 字段：显示别名说明认出来了，显示 `内联图(N 字符)` 说明走的是 base64，`(未提供)` 说明载荷里没找到图像块。

**没收到广播** —— 广播只扇出给**已连接**对端。用 `node.connections()` 确认真的连上了。

---

## 验收脚本

仓库根目录的 `notch_test.py` 是本集成的验收测试。它**一边连节点、一边读宿主的日志**，所以：

> 先启动 NotchPeninsula，**并且确认「MSP 信号总线（实验性）」已打开**（设置 → 通用设置，默认是关的）；
> 再在另一个终端跑脚本。两个进程要在**同一台机器**上。

```powershell
python notch_test.py                  # 默认不真实切换播放/暂停
python notch_test.py --media-toggle   # 连媒体广播一起测
```

`--media-toggle` 会真的切一次播放/暂停（为了触发 `media.playback.changed`），收尾再切回去；不传就完全不碰你的播放器。

它检查 9 组：

| # | 检查什么 |
| --- | --- |
| 1 | 发现与连接 |
| 2 | manifest 自省：预设、两种模式、四项能力、4 条已宣告信号 |
| 3 | `ping` / `media_status` 的往返 |
| 4 | 订阅总线：发一条 `notify.*`，岛上应显示 |
| 5 | 点对点：`notify` 工具应显示但**不**上总线（防回声） |
| 6 | 本机事件：本地 HTTP 推一条 → 应广播出 `notify.toast.raised` |
| 7 | 媒体广播（需 `--media-toggle`，否则整组跳过） |
| 8 | content block：两种图标形态、无图标主路径、未知块宽容跳过 |
| 9 | `notify` 不阻塞：`duration: 60` 与「要联网下载的图标」都必须立刻返回 |

预期输出是每项一行，末尾给汇总。真实的一小段：

```
[9] notify 不阻塞调用方（Duration 长 / 图标要下载，都不得拖住 RPC）
  [PASS] duration=60 仍然立刻返回 — 0.4 ms
  [PASS] 长通知挂着时 ping 仍然很快 — 0.5 ms
  [PASS] 需要下载的图标不拖住调用 — 0.3 ms

============================================================
跳过 1 项：['media.playback.changed 广播']
OK: 全部通过
```

三种结果：**PASS** 通过；**FAIL** 没过（末尾汇总成列表）；**SKIP** 没测（例如没传 `--media-toggle`，或者读不到宿主日志）。

**退出码**：全过 `0`，有 FAIL 就是 `1` —— 可以直接在批处理 / CI 里判断。

没过时：先从末尾那行 `FAIL：N 项未通过 -> [...]` 拿到项名，再去**同机**的宿主日志找上下文：

```
%LOCALAPPDATA%\NotchPeninsula\app.log
```

> 脚本对总线的断言全靠读这个日志，所以日志读不到时相关项会变成 **SKIP 而不是 FAIL**。
> 遇到这种 SKIP，先确认程序真的在跑。

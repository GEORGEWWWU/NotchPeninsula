"""NotchPeninsula 原生 MSP 接入验收测试（local software 级节点）。

用法：
    1. 先启动 NotchPeninsula（已内嵌 MSP 节点 "notchpeninsula"）：
       dotnet run --project NotchPeninsula.csproj
    2. 再运行本脚本：
       python notch_test.py [--media-toggle]

覆盖范围：
    [1] 发现与连接        节点以 LOCAL_SOFTWARE 预设宣告，Python 侧能发现并连上
    [2] manifest 自省     两模式权限 + pub/sub/req/rep 四能力 + 四条已宣告信号
    [3] request-reply     ping / media_status 程序化往返（只读）
    [4] 订阅 notify 域的信号  本脚本 publish notify.*，灵动岛应显示出来
    [5] 点对点不广播    notify **工具**必须不广播（否则跨节点回声），但仍要显示
    [6] 本机事件自动广播  经灵动岛本地 HTTP 接口投一条消息 → 应收到它广播的 notify.toast.raised
    [7] 媒体广播          仅 --media-toggle 才真实切换播放，且应收到 media.playback.changed
    [8] content block     notify 载荷的富内容通道：图标两种形态 / 无图标主路径 / 未知块宽容跳过
    [9] 不阻塞            notify 绝不能因为 Duration 长、或图标要联网下载而拖住调用方

关于 [5]：[4] 和 [6] 是**广播**语义（无人点单的事件）；notify 工具是**点对点**语义
（request-reply），它的效果不该再喷到广播里 —— 否则两个都订阅了通知的节点会互相转发到死。
详见 Msp/MspNotchBridge.cs 类注释里的「回声问题」。

关于 [8]：载荷约定见 Msp/MspNotchBridge.cs 类注释与 README 的「MSP 信号路径」。
要点是 title/body 是降级通道、content 是可选富内容通道，而**没有图标才是主路径**。
"""
import base64
import json
import os
import sys
import time
import urllib.request

from msp import MSPNode, Preset

NODE_ID = "py"
NOTCH_ID = "notchpeninsula"
DISCOVER_TIMEOUT = 20.0

# 灵动岛内置的本地 HTTP 推送接口（Notify/Toast.cs）。
HTTP_PUSH_URL = "http://127.0.0.1:47300/api/activities/"

# 本节点应宣告的信号（与 Msp/MspNotchBridge.cs 里的常量一一对应）。
EXPECTED_SIGNALS = [
    "notify.toast.raised",
    "notify.notchpeninsula.reminder.raised",
    "media.track.changed",
    "media.playback.changed",
]
EXPECTED_PATTERNS = ["msp/request-reply", "msp/publish-subscribe"]
EXPECTED_CAPS = ["pub", "sub", "req", "rep"]

# 收到的信号（on() 回调在协议线程上跑，用 list + GIL 足够；只做追加/切片）。
received: list = []

_failures: list = []
_skips: list = []


def check(ok: bool, label: str, detail: str = "") -> bool:
    print(f"  [{'PASS' if ok else 'FAIL'}] {label}" + (f" — {detail}" if detail else ""), flush=True)
    if not ok:
        _failures.append(label)
    return ok


def skip(label: str, why: str) -> None:
    print(f"  [SKIP] {label} — {why}", flush=True)
    _skips.append(label)


def section(title: str) -> None:
    print(f"\n{title}", flush=True)


def wait_for(predicate, timeout: float, interval: float = 0.2) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(interval)
    return predicate()


def log_path() -> str:
    base = os.environ.get("LOCALAPPDATA") or os.path.expanduser("~")
    return os.path.join(base, "NotchPeninsula", "app.log")


def log_contains(needle: str) -> bool | None:
    """在灵动岛日志里找一行。None = 日志读不到（当作跳过，不判失败）。"""
    path = log_path()
    if not os.path.isfile(path):
        return None
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            return needle in f.read()
    except OSError:
        return None


def signals_named(path: str) -> list:
    return [s for s in list(received) if s.path == path]


def http_push(title: str, body: str, kind: str = "MSP测试") -> bool:
    """往灵动岛的本地 HTTP 接口投一条消息（会走和系统通知同一条检测路径）。

    刻意用 json.dumps 的**默认**行为（非 ASCII 转义成 \\uXXXX）——这正是 Python / Go
    这类客户端的常见发法，顺带守住宿主 Notify/Toast.cs 的 ParseBody：它以前会把标准的
    \\uXXXX 当成字面文本，于是「MSP测试」到了岛上显示成 literal "MSP\\u6d4b\\u8bd5"。
    """
    payload = json.dumps({"kind": kind, "title": title, "subtitle": body}).encode("utf-8")
    req = urllib.request.Request(
        HTTP_PUSH_URL, data=payload, headers={"Content-Type": "application/json"}, method="POST"
    )
    try:
        with urllib.request.urlopen(req, timeout=5) as resp:
            return resp.status == 200
    except Exception as ex:  # noqa: BLE001 - 测试脚本，任何失败都只影响这一项
        print(f"  （HTTP 推送失败：{ex}）", flush=True)
        return False


def main() -> int:
    # 控制台可能是 GBK，中文标题打出来会乱码；本脚本输出全用 UTF-8。
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:  # noqa: BLE001
        pass

    node = MSPNode(NODE_ID, preset=Preset.LOCAL_AI)  # 与 notchpeninsula 共享 ~/.msp/nodes

    @node.on("notify.**")
    def _on_notify(sig):  # noqa: ANN001
        received.append(sig)

    @node.on("media.**")
    def _on_media(sig):  # noqa: ANN001
        received.append(sig)

    node.start()

    def call_retry(tool: str, args: dict | None = None, attempts: int = 3, timeout: float = 20.0):
        """首次调用带重试。

        两侧都是 eager，协议层要做一次连接去重、只保留规范方向的那条连接；对端「连上就立刻
        调用」有可能正好走在被关掉的那条上，报「连接断开」或超时。这是协议层的连接去重行为，
        **不是节点的问题**（节点是 eager 的「本机软件」角色，这个策略不能改），调用方重试一次
        就走通了。重试过会打一行 note —— 不藏起来，免得真出问题时有东西被悄悄吞掉。
        """
        res = None
        for i in range(1, attempts + 1):
            res = node.call(NOTCH_ID, tool, args or {}, timeout=timeout)
            if not res.is_error:
                if i > 1:
                    print(f"        （首次调用撞上连接去重，第 {i} 次才通：{tool}）", flush=True)
                return res
            time.sleep(1.0)
        return res

    try:
        # ---------------- [1] 发现与连接 ----------------
        section("[1] 发现与连接")
        print(f"  等待发现并连接 {NOTCH_ID} ...", flush=True)
        if not wait_for(lambda: NOTCH_ID in node.connections(), DISCOVER_TIMEOUT):
            check(False, "发现并连接", f"{DISCOVER_TIMEOUT:.0f}s 内未连上；"
                                     "请确认 ① NotchPeninsula 已启动，"
                                     "② 「设置 → 通用设置 → MSP 接入（实验性）」已打开（默认是关的），"
                                     "③ 两侧共享目录一致（~/.msp/nodes）")
            return 1
        check(True, "发现并连接", f"peers={node.connections()}")

        peer = node.peer(NOTCH_ID)

        # ---------------- [2] manifest 自省 ----------------
        section("[2] manifest 自省（预设 / 模式 / 能力 / 信号）")
        # manifest 是握手时交换的；刚连上可能还没到，给一点时间。
        wait_for(lambda: bool(peer.signals()), 5.0)

        patterns = peer.patterns()
        check(set(patterns) == set(EXPECTED_PATTERNS), "两模式权限齐全",
              f"patterns={sorted(patterns)}")

        caps = (peer.manifest or {}).get("capabilities", [])
        check(set(caps) == set(EXPECTED_CAPS), "四项能力齐全", f"capabilities={sorted(caps)}")

        sigs = peer.signals()
        for path in EXPECTED_SIGNALS:
            check(path in sigs, f"已宣告信号 {path}")
        check(peer.supports("msp/publish-subscribe"), "supports(msp/publish-subscribe)")

        # ---------------- [3] request-reply ----------------
        section("[3] request-reply：ping / media_status")
        r = call_retry("ping")
        if check(not r.is_error, "ping 无错", str(r.error if r.is_error else "")):
            info = r.unwrap() or {}
            check(info.get("preset") == "LOCAL_SOFTWARE", "预设 = LOCAL_SOFTWARE",
                  f"preset={info.get('preset')}")
            check(sorted(info.get("capabilities") or []) == sorted(EXPECTED_CAPS), "ping 报告四能力",
                  f"capabilities={info.get('capabilities')}")

        r = call_retry("media_status")
        if check(not r.is_error, "media_status 无错", str(r.error if r.is_error else "")):
            print(f"         -> {r.unwrap()}", flush=True)

        # ---------------- [4] 订阅 notify 域的信号 ----------------
        section("[4] 订阅 notify 域的信号：publish notify.* → 灵动岛应显示")
        probe_path = "notify.test.display"
        node.publish(probe_path, {"title": "订阅测试", "body": "这条来自 Python 的 notify.* 广播"})
        # 两级证据：MSP 侧「已受理」（载荷解析通过、请求接下），宿主侧「插件提醒已投递」（真进了展示通道）。
        # 措辞上是「受理」不是「显示」，因为投递走后台 —— 见 MspNotchBridge.ShowOnIsland。
        needle = f"[MSP] 对端通知已受理：{probe_path}"
        wait_for(lambda: log_contains(needle) is True, 8.0)  # 注意 `is True`：None 不能当已出现
        logged = log_contains(needle)
        if logged is None:
            skip("对端通知已被受理 notify.test.display", f"日志读不到：{log_path()}")
        else:
            check(bool(logged), "对端通知已被受理", "见 app.log 的 [MSP] 对端通知已受理")
        shown = wait_for(
            lambda: log_contains("[PluginHost] 插件提醒已投递: 订阅测试") is True, 5.0
        ) or log_contains("[PluginHost] 插件提醒已投递: 订阅测试")
        if shown is None:
            skip("对端通知真的进了展示通道", f"日志读不到：{log_path()}")
        else:
            check(bool(shown), "对端通知真的进了展示通道", "见 app.log 的 [PluginHost] 插件提醒已投递")
        # 来源标签：这条 payload 没给 kind，来源应回退成发布者节点 id，而不是宿主写死的
        # 「插件提醒」—— 那正是给 ReminderData 加 Source 字段要解决的问题。
        src = log_contains(f"{needle} ← py（订阅测试），来源: py")
        if src is None:
            skip("无 kind 时来源回退到节点 id", f"日志读不到：{log_path()}")
        else:
            check(bool(src), "无 kind 时来源回退到节点 id（不是「插件提醒」）", "见 app.log 的「，来源: py」")

        # ---------------- [5] 点对点不广播 ----------------
        section("[5] 点对点：notify 工具应显示但**不**广播")
        r = node.call(NOTCH_ID, "notify", {
            "kind": "MSP",
            "title": "点对点测试",
            "body": "这条只应显示在灵动岛上，不该出现在广播里",
        })
        check(not r.is_error, "notify 工具无错", str(r.error if r.is_error else ""))
        time.sleep(3.0)  # 留足广播往返的时间窗
        leaked = signals_named("notify.notchpeninsula.reminder.raised")
        check(not leaked, "notify 工具未广播 notify.notchpeninsula.reminder.raised",
              f"收到 {len(leaked)} 条（收到即为回声隐患）")
        # 精确断言：广播里不该出现任何携带这次 notify 内容的信号。
        # 这里**不能**断言「窗口内一条信号都没有」—— 这台机器上真实到达的系统通知
        # 会合法地广播 notify.toast.raised，那是「本机事件自动广播」的正常行为，
        # 与本项要验的「点对点不广播」无关，混在一起会变成随机失败。
        echoed = [s for s in list(received)
                  if "点对点测试" in json.dumps(s.payload, ensure_ascii=False)]
        check(not echoed, "notify 的内容没有出现在广播里",
              f"收到 {[s.path for s in echoed]}（出现即为回声隐患）")
        shown = log_contains("[PluginHost] 插件提醒已投递: 点对点测试")
        if shown is None:
            skip("notify 工具确实显示了", f"日志读不到：{log_path()}")
        else:
            check(bool(shown), "notify 工具确实显示了", "见 app.log 的 [PluginHost] 插件提醒已投递")

        # ---------------- [6] 本机事件自动广播 ----------------
        section("[6] 本机事件：本地 HTTP 推送 → 应广播 notify.toast.raised")
        marker = f"NPS-MSP-HTTP-{int(time.time())}"
        before = len(received)
        if check(http_push(marker, "经灵动岛本地接口投递，应被它广播到 notify 域"), "HTTP 推送成功"):
            hit = wait_for(
                lambda: any(marker in json.dumps(s.payload, ensure_ascii=False)
                            for s in signals_named("notify.toast.raised")),
                10.0,
            )
            got = [s for s in signals_named("notify.toast.raised")
                   if marker in json.dumps(s.payload, ensure_ascii=False)]
            check(hit, "收到 notify.toast.raised（本机事件自动广播）",
                  f"payload={got[0].payload if got else '无'}")
            if got:
                check(got[0].source == NOTCH_ID, "信号 source 是灵动岛", f"source={got[0].source}")
                payload = got[0].payload
                # 约定：kind 是来源标识（这条曾是 app，已统一到 notify 约定的字段名）。
                check(payload.get("kind") == "MSP测试", "载荷用约定的 kind 字段",
                      f"kind={payload.get('kind')!r}")
                # 守住 ParseBody：非 ASCII 经 \uXXXX 转义后应还原成真中文，而不是字面量。
                check(payload.get("kind") == "MSP测试" and "\\u" not in str(payload.get("kind")),
                      "非 ASCII 经 \\uXXXX 转义后正确还原", f"kind={payload.get('kind')!r}")
                # 出站也带图标块：本机系统通知的图标应作为 role=icon 的 image 块广播出去。
                content = payload.get("content") or []
                blk = content[0] if content else {}
                check(blk.get("type") == "image", "出站载荷带 image 图标块", f"content[0]={ {k: v for k, v in blk.items() if k != 'data'} }")
                check((blk.get("annotations") or {}).get("role") == "icon", "该块标了 role=icon")
                raw = blk.get("data") or ""
                try:
                    png = base64.b64decode(raw)
                    is_png = png[:8] == b"\x89PNG\r\n\x1a\n"
                except Exception:  # noqa: BLE001
                    png, is_png = b"", False
                check(is_png, "图标块是可解码的 PNG", f"{len(png)} 字节")

        # ---------------- [7] 媒体广播（默认只测只读） ----------------
        section("[7] 媒体广播")
        if "--media-toggle" not in sys.argv:
            skip("media.playback.changed 广播", "未传 --media-toggle（真实切换播放/暂停，默认不执行）")
        else:
            # 切一次 → 应广播；再切回来 → 应再广播一次，顺便把用户的播放状态恢复原样。
            r = node.call(NOTCH_ID, "media_control", {"action": "toggle"})
            check(not r.is_error, "media_control toggle 无错", str(r.error if r.is_error else ""))
            first = wait_for(lambda: len(signals_named("media.playback.changed")) >= 1, 8.0)
            got = signals_named("media.playback.changed")
            check(first, "第 1 次 toggle 收到 media.playback.changed",
                  f"payload={got[0].payload if got else '无'}")

            time.sleep(1.5)
            node.call(NOTCH_ID, "media_control", {"action": "toggle"})  # 恢复原状态
            second = wait_for(lambda: len(signals_named("media.playback.changed")) >= 2, 8.0)
            check(second, "切回后再收到一次（播放状态已恢复原样）",
                  f"共 {len(signals_named('media.playback.changed'))} 条")

        # ---------------- [8] content block ----------------
        section("[8] content block：图标两种形态 / 无图标主路径 / 未知块宽容跳过")

        # 8.1 工具 schema：content 必须是「可选」，不能进 required ——
        #     纯文本通知是最普遍的情况，不能因为支持块了就强制每次都给数组。
        #     manifest 里 tools 是个**列表**（每项带 name / inputSchema），不是按名字索引的字典。
        tools = (peer.manifest or {}).get("tools") or []
        schema = {}
        for t in tools:
            if isinstance(t, dict) and t.get("name") == "notify":
                schema = t.get("inputSchema") or {}
                break
        if not schema:
            skip("notify schema 检查", f"manifest 里找不到 notify 工具，tools={tools}")
        else:
            props = schema.get("properties") or {}
            required = schema.get("required") or []
            check("content" in props, "notify schema 里有 content", f"properties={sorted(props)}")
            check("content" not in required, "content 是可选的（不在 required 里）",
                  f"required={required}")

        # 8.2 主路径：完全不带 content，照样得显示、且走默认图标
        r = node.call(NOTCH_ID, "notify", {"kind": "CB", "title": "CB-无图标", "body": "最常见的情况"})
        if check(not r.is_error, "不带 content 调用无错", str(r.error if r.is_error else "")):
            got = r.unwrap() or {}
            check(got.get("ok") is True, "无 content 也成功", f"{got}")
            check(got.get("icon") == "(未提供)", "图标缺省（回退默认图标）", f"icon={got.get('icon')!r}")
            # 工具路径拿不到调用方身份（SDK 不注入），所以来源只能是调用方自己给的 kind。
            check(got.get("source") == "CB", "工具来源 = 调用方给的 kind", f"source={got.get('source')!r}")
        shown = log_contains("[PluginHost] 插件提醒已投递: CB-无图标")
        if shown is None:
            skip("无图标通知确实显示了", f"日志读不到：{log_path()}")
        else:
            check(bool(shown), "无图标通知确实显示了", "见 app.log")

        # 8.3 图标形态一：resource_link（uri 直接透传，宿主的 ToastIconProvider 认别名/链接/本地路径）
        r = node.call(NOTCH_ID, "notify", {
            "kind": "CB", "title": "CB-别名图标", "body": "用内置别名当图标",
            "content": [{"type": "resource_link", "uri": "qq", "name": "qq icon",
                         "annotations": {"role": "icon"}}],
        })
        if check(not r.is_error, "resource_link 图标调用无错", str(r.error if r.is_error else "")):
            got = r.unwrap() or {}
            check(got.get("icon") == "qq", "别名图标被识别为图标块", f"icon={got.get('icon')!r}")

        # 8.4 图标形态二：image（内嵌 base64 → 拼成 data: URI），**不给 annotations**。
        #     这是「没标 role=icon 时第一个图像块即图标」那条退化规则。
        #     用内置 qq-icon.png 的真实字节，保证 SkiaSharp 一定解得开。
        png_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "image", "qq-icon.png")
        if not os.path.isfile(png_path):
            skip("内嵌 image 图标", f"找不到素材：{png_path}")
            b64 = ""
        else:
            with open(png_path, "rb") as f:
                b64 = base64.b64encode(f.read()).decode("ascii")
            r = node.call(NOTCH_ID, "notify", {
                "kind": "CB", "title": "CB-内嵌图标", "body": "用 base64 当图标，不写 annotations",
                "content": [{"type": "image", "data": b64, "mimeType": "image/png"}],
            })
            if check(not r.is_error, "image 图标调用无错", str(r.error if r.is_error else "")):
                got = r.unwrap() or {}
                check(str(got.get("icon", "")).startswith("内联图("),
                      "没标 role=icon 时第一个图像块即图标", f"icon={got.get('icon')!r}")

        # 8.5 显式标记优先于「第一个图像块」：未标记的 image 在前，带标记的 resource_link 在后
        if b64:
            r = node.call(NOTCH_ID, "notify", {
                "kind": "CB", "title": "CB-标记优先", "body": "未标记的图在前，标记的在后",
                "content": [{"type": "image", "data": b64, "mimeType": "image/png"},
                            {"type": "resource_link", "uri": "wintoast",
                             "annotations": {"role": "icon"}}],
            })
            if check(not r.is_error, "标记优先调用无错", str(r.error if r.is_error else "")):
                got = r.unwrap() or {}
                check(got.get("icon") == "wintoast", "显式 role=icon 压过更早的未标记图像块",
                      f"icon={got.get('icon')!r}")
                check(got.get("blocks") == 1, "多余的图像块计入未渲染", f"blocks={got.get('blocks')}")

        # 8.6 宽容读取：不认识的 type、以及宿主画不出来的块（audio），都只计数不报错
        r = node.call(NOTCH_ID, "notify", {
            "kind": "CB", "title": "CB-未知块", "body": "带两个渲染不了的块",
            "content": [{"type": "totally_unknown", "x": 1},
                        {"type": "audio", "data": "AAAA", "mimeType": "audio/wav"}],
        })
        if check(not r.is_error, "带未知块调用无错", str(r.error if r.is_error else "")):
            got = r.unwrap() or {}
            check(got.get("ok") is True, "未知块不影响整条通知", f"{got}")
            check(got.get("blocks") == 2, "渲染不了的块被计数上报", f"blocks={got.get('blocks')}")
            check(got.get("icon") == "(未提供)", "没有图像块时不乱认图标",
                  f"icon={got.get('icon')!r}")

        # 8.7 信号路径的正文兜底：title/body 都空时，拿 content 里第一个 text 块当正文
        probe = "notify.test.textonly"
        node.publish(probe, {"content": [{"type": "text", "text": "只有内容块的正文"}]})
        needle = f"[MSP] 对端通知已受理：{probe}"
        line = wait_for(lambda: log_contains(needle) is True, 8.0) or log_contains(needle)
        if line is None:
            skip("text 块兜底当正文", f"日志读不到：{log_path()}")
        else:
            check(bool(line), "text 块兜底当正文（信号路径）", "见 app.log 的 [MSP] 对端通知已受理")

        # 8.8 信号路径也能带图标（走广播，不走工具），且日志会记下图标描述
        probe = "notify.test.icon"
        node.publish(probe, {
            "title": "CB-广播图标",
            "content": [{"type": "resource_link", "uri": "qq"}],
        })
        needle = f"[MSP] 对端通知已受理：{probe}"
        line = wait_for(lambda: log_contains(needle) is True, 8.0) or log_contains(needle)
        if line is None:
            skip("信号路径带图标", f"日志读不到：{log_path()}")
        else:
            check(log_contains(f"{needle} ← py（CB-广播图标），来源: py，图标: qq"),
                  "信号路径的图标被识别并记进日志", "见 app.log")

        # 8.9 duration 容错：非法值不该让整条通知丢掉
        node.publish("notify.test.badduration", {"title": "CB-坏duration", "duration": "不是数字"})
        line = wait_for(lambda: log_contains("[MSP] 对端通知已受理：notify.test.badduration") is True, 8.0) \
            or log_contains("[MSP] 对端通知已受理：notify.test.badduration")
        if line is None:
            skip("非法 duration 仍能显示", f"日志读不到：{log_path()}")
        else:
            check(bool(line), "非法 duration 仍能显示", "回退默认时长")

        # 8.10 来源标签：kind 就是「来源标识」，给了就用它，而不是一律显示节点 id
        probe = "notify.test.source"
        node.publish(probe, {"kind": "微信", "title": "CB-来源标签", "body": "来源应该显示微信"})
        needle = f"[MSP] 对端通知已受理：{probe} ← py（CB-来源标签），来源: 微信"
        line = wait_for(lambda: log_contains(needle) is True, 8.0) or log_contains(needle)
        if line is None:
            skip("广播的 kind 当来源标签", f"日志读不到：{log_path()}")
        else:
            check(bool(line), "广播的 kind 当来源标签（而不是节点 id）", "见 app.log 的「，来源: 微信」")

        # ---------------- [9] 不阻塞 ----------------
        section("[9] notify 不阻塞调用方（Duration 长 / 图标要下载，都不得拖住 RPC）")

        def rtt(tool, args, timeout=20.0):
            t0 = time.perf_counter()
            res = node.call(NOTCH_ID, tool, args, timeout=timeout)
            return (time.perf_counter() - t0) * 1000, res

        # 9.1 Duration 是「显示多久」，与「这次调用耗时多久」无关。
        #     给一个远超 RPC 默认超时（5s）的时长：如果哪天有人在处理链里等展示结束，
        #     这条会直接超时失败，而不会悄悄变慢。
        dt, r = rtt("notify", {"kind": "NB", "title": "NB-长时长",
                               "body": "duration=60", "duration": 60})
        if check(not r.is_error, "duration=60 调用无错", str(r.error if r.is_error else "")):
            check(dt < 1000, "duration=60 仍然立刻返回", f"{dt:.1f} ms")

        # 9.2 那条长通知还挂在屏幕上时，协议派发也不能被占住
        dt, r = rtt("ping", {})
        if check(not r.is_error, "长通知挂着时 ping 无错"):
            check(dt < 1000, "长通知挂着时 ping 仍然很快", f"{dt:.1f} ms")

        # 9.3 图标要联网下载：不可路由地址会让后台解析一直挂到 HttpClient 的 3s 超时。
        #     调用方不该跟着等 —— 解析本来就在 Task.Run 里。
        dt, r = rtt("notify", {
            "kind": "NB", "title": "NB-慢图标", "body": "不可路由地址",
            "content": [{"type": "resource_link", "uri": "https://10.255.255.1/x.png"}],
        })
        if check(not r.is_error, "联网图标调用无错", str(r.error if r.is_error else "")):
            check(dt < 1000, "需要下载的图标不拖住调用", f"{dt:.1f} ms")

        # 9.4 收尾：拿一条 1 秒的通知把屏幕上那条 60 秒的顶掉
        node.call(NOTCH_ID, "notify", {"kind": "NB", "title": "收尾", "body": "1 秒", "duration": 1})

    finally:
        node.stop()

    # ---------------- 汇总 ----------------
    print("\n" + "=" * 60, flush=True)
    if _skips:
        print(f"跳过 {len(_skips)} 项：{_skips}", flush=True)
    if _failures:
        print(f"FAIL：{len(_failures)} 项未通过 -> {_failures}", flush=True)
        return 1
    print("OK: 全部通过", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())

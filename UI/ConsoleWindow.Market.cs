using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using SkiaSharp;
using NotchPeninsula.Plugins;

namespace NotchPeninsula
{
    public partial class ConsoleWindow
    {
        // ---- 插件市场（tab 6 下方卡片）----
        // 行为约定（与用户确认的交互一致）：
        //   开关位置 = 详情按钮，点击弹出小窗显示插件介绍。

        private sealed class MarketPlugin
        {
            public string Id = "";
            public string Name = "";
            public string Author = "";
            public string Desc = "";
            public string Version = "";
            public string Updated = "";
            public string Category = "";    // market API 的 category（theme/media/utility/dev）
            public int Downloads;
            public bool Official;
            public double Rating;           // 平均分（0 = 还没人评）
            public int RatingCount;
            public string FileName = "";     // 主文件名（NpsMediaMixer.dll / NpsSettingsHub.zip）
            public string FileUrl = "";      // 绝对下载地址
            public bool IsZip;               // zip = 目录型插件包，解压成 plugins/<id>/ 安装
            public string Tags = "";         // 标签（" · " 连接，详情弹窗里显示）
        }

        private const string MarketApiUrl = "https://nps.georgewu.top/admin/api.php";
        private const string MarketApiBase = "https://nps.georgewu.top";

        private static readonly HttpClient _marketHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

        private List<MarketPlugin> _marketPlugins = new();   // 原始列表（API 返回）
        private List<MarketPlugin> _marketView = new();      // 过滤视图（分类 + 搜索），列表渲染/命中/点击都用它

        // 从岛上的通知点进来时要定位的插件 id：市场数据可能还在后台拉，先记下来，
        // 等 RenderTabMarket 重建过滤视图后再打开详情（见 TryOpenPendingMarketDetail）。
        private string _pendingMarketDetailId = "";

        private string _marketCategoryKey = "all";
        private bool _marketCategoryOpen;                    // 分类下拉展开态
        private int _hoveredMarketCategoryIndex = -1;        // 菜单内悬停项

        // ── 排序下拉（状态行，刷新按钮左侧）──
        //    官方插件无视排序关键字永远置顶；默认最热。
        private static readonly string[] MarketSortOptions = { "最热", "评分", "最新", "名称" };
        private int _marketSortIndex;                        // 0 = 最热
        private bool _marketSortOpen;
        private int _hoveredMarketSortIndex = -1;            // 菜单内悬停项；-2 = 框本体悬停
        private bool _hoveredMarketSortBox;

        private string _marketSearch = "";
        private bool _marketSearchFocused;
        private bool _marketSearchHovered;

        private int _marketSearchCaret;
        private int _marketSearchSelAnchor;
        private bool _marketSearchDragging;

        private int MarketSelStart => Math.Min(_marketSearchCaret, _marketSearchSelAnchor);
        private int MarketSelEnd => Math.Max(_marketSearchCaret, _marketSearchSelAnchor);
        private bool MarketHasSelection => _marketSearchCaret != _marketSearchSelAnchor;

        private const float MarketSearchTextX = MarketSearchX + 26f;
        private const float MarketSearchTextMax = MarketSearchW - 34f;

        // ── 搜索串的逐字符前缀宽度（缓存）──
        //    往搜索框里粘一整段文字再拖选会把界面直接拖卡。
        private string _searchMetricsSrc = "";
        private float[] _searchPrefix = new float[1];

        private float[] SearchPrefix()
        {
            if (ReferenceEquals(_searchMetricsSrc, _marketSearch)) return _searchPrefix;

            int n = _marketSearch.Length;
            if (_searchPrefix.Length < n + 1) _searchPrefix = new float[n + 1];
            _searchPrefix[0] = 0f;
            float acc = 0f;
            for (int i = 0; i < n; i++)
            {
                acc += _marketTextPaint.MeasureText(_marketSearch.AsSpan(i, 1));
                _searchPrefix[i + 1] = acc;
            }
            _searchMetricsSrc = _marketSearch;
            return _searchPrefix;
        }

        // 拖选期间冻结的可视窗口起点（-1 = 未冻结）。
        private int _marketSearchViewFrozen = -1;

        private int MarketSearchViewStart()
        {
            if (_marketSearchDragging && _marketSearchViewFrozen >= 0)
                return Math.Clamp(_marketSearchViewFrozen, 0, _marketSearch.Length);

            int caret = Math.Clamp(_marketSearchCaret, 0, _marketSearch.Length);
            float[] px = SearchPrefix();
            if (px[_marketSearch.Length] <= MarketSearchTextMax) return 0;

            int lo = 0, hi = caret;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (px[caret] - px[mid] <= MarketSearchTextMax) hi = mid;
                else lo = mid + 1;
            }
            return lo;
        }

        private float MarketSearchXAtIndex(int index)
        {
            float[] px = SearchPrefix();
            int start = MarketSearchViewStart();
            if (index <= start) return MarketSearchTextX;
            index = Math.Clamp(index, start, _marketSearch.Length);
            return MathF.Min(MarketSearchTextX + (px[index] - px[start]), MarketSearchTextX + MarketSearchTextMax);
        }

        private int MarketSearchIndexAtX(float x)
        {
            float[] px = SearchPrefix();
            int start = MarketSearchViewStart();
            int len = _marketSearch.Length;
            // 拖到可见文字右侧之外：直接给串尾。原生框也是这个手感。
            if (x >= MarketSearchTextX + (px[len] - px[start])) return len;

            float rel = x - MarketSearchTextX;
            int lo = start, hi = len;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (px[mid] - px[start] < rel) lo = mid + 1;
                else hi = mid;
            }
            if (lo > start && MathF.Abs(rel - (px[lo - 1] - px[start])) < MathF.Abs(rel - (px[lo] - px[start])))
                return lo - 1;
            return lo;
        }

        private bool MarketDeleteSelection()
        {
            if (!MarketHasSelection) return false;
            int s = MarketSelStart, e = MarketSelEnd;
            _marketSearch = _marketSearch[..s] + _marketSearch[e..];
            _marketSearchCaret = _marketSearchSelAnchor = s;
            return true;
        }

        private void MarketInsertText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            MarketDeleteSelection();
            int at = Math.Clamp(_marketSearchCaret, 0, _marketSearch.Length);
            _marketSearch = _marketSearch[..at] + text + _marketSearch[at..];
            _marketSearchCaret = _marketSearchSelAnchor = at + text.Length;
        }

        private void MarketCaretToEnd()
        {
            _marketSearchCaret = _marketSearchSelAnchor = _marketSearch.Length;
            _marketSearchDragging = false;
        }

        private bool _marketOnlyInstalled;
        private bool _hoveredMarketChk;
        private bool _hoveredMarketRefresh;   // 第二行「刷新」按钮悬停

        private bool _marketDataDirty;

        private bool _marketFetching;
        private bool _marketTriedFetch;                  // 只自动拉一次；失败后切回页签时重试
        private string _marketError = "";                // 非空 = 拉取失败（市场卡状态行红字提示）
        private string _marketBusyId = "";               // 正在下载/安装的市场插件 id（该行按钮置灰）
        private string _marketHint = "";                 // 状态行右侧红字：只用于安装 / 卸载失败，成功不提示
        private bool _marketHintIsError;
        private int _marketScroll;                       // 市场列表滚动首行（绝对条目下标）

        //   只有一颗「好的」，两个页签都要能画出来。
        private enum MarketDialog { None, Detail, Rate, LoadFailed, UninstallConfirm, UninstallData }

        private MarketDialog _marketDialog = MarketDialog.None;
        private int _marketDialogIndex = -1;             // 弹窗对应的市场条目下标
        private bool _hoveredDialogClose;                // 弹窗右上角 ❌ 是否悬停
        private int _hoveredDialogButton = -1;           // 弹窗内按钮下标（从左到右，0 起）

        // 卸载确认弹窗：所有卸载操作都先过这一关。
        // 第一级问「确定卸载吗」，插件登记过数据目录的才进第二级问「目录要不要一起删」。
        private PluginEntry? _uninstallEntry;
        private string _uninstallDir = "";               // 空 = 没登记 / 登记的不允许删
        private bool _uninstallFromMarket;               // 提示回哪个页签（市场页的状态行 / 插件页卡头）

        // 评分弹窗状态（进入时向服务端要一次「我评过没有」）
        private double _rateStars;                       // 0..5，鼠标预览中的分值（半星为 .5）
        private double _rateMine;                        // 服务端记录的「我的评分」，0 = 没评过
        private double _rateAverage;                     // 服务端返回的平均分
        private int _rateCount;
        private bool _rateLoading;                       // 正在读状态 / 正在提交
        private string _rateStatus = "";                 // 弹窗内状态文案（成功绿 / 失败红）
        private bool _rateStatusIsError;

        private int _hoveredMarketInstall = -1;
        private int _hoveredMarketUninstall = -1;
        private int _hoveredMarketDetail = -1;

        private void CloseMarketDialog(bool render = false)
        {
            if (_marketDialog == MarketDialog.None && _marketDialogIndex == -1) return;
            _marketDialog = MarketDialog.None;
            _marketDialogIndex = -1;
            _uninstallEntry = null;
            _uninstallDir = "";
            _hoveredDialogClose = false;
            _hoveredDialogButton = -1;
            _rateStatus = "";
            if (render) Render();
        }

        /// <summary>当前是否有「必须先处理」的弹窗：命中判定与点击分派都走它，别再去碰下面的列表。</summary>
        private bool IsConfirmDialogOpen =>
            _marketDialog is MarketDialog.LoadFailed or MarketDialog.UninstallConfirm or MarketDialog.UninstallData;

        /// <summary>
        /// 打开「插件加载失败」弹窗。<paramref name="subject"/> = 是哪个插件（可为空：启动期批量失败时
        /// 不指名更有用），有名字时正文会带一句，否则用户根本不知道是哪一次操作出的错。
        /// </summary>
        private void ShowPluginLoadFailedDialog(PluginFailureKind kind = PluginFailureKind.Other, string subject = "", bool render = true)
        {
            _marketDialog = MarketDialog.LoadFailed;
            _marketDialogIndex = -1;
            _hoveredDialogClose = false;
            _hoveredDialogButton = -1;
            _loadFailedLinkHovered = false;
            _loadFailedReason = subject.Length > 0
                ? $"「{subject}」：{ReasonTextOf(kind)}"
                : ReasonTextOf(kind);
            _loadFailedWithVersionHint = NeedsVersionHint(kind);
            BuildLoadFailedLayout();   // 版面（行数 + 群号位置）在打开时定一次，绘制与命中共用
            if (render) Render();
        }

        /// <summary>
        /// 所有「插件加载失败」的唯一出口 —— 市场安装、插件页导入 / 拖入 / 重载 / 启用，全都从这里走。
        /// 以前每处各弹一次、甚至有整条路径压根不弹，用户点完没动静也没人解释。
        /// </summary>
        private void ReportPluginLoadFailure(PluginFailureKind kind, string subject, string? marketHint = null, bool render = true)
        {
            if (!string.IsNullOrEmpty(marketHint)) SetMarketHint(marketHint!, true);
            ShowPluginLoadFailedDialog(kind, subject, render: false);
            if (render) Render();
        }

        /// <summary>把失败类型翻成一句用户能行动的话。全都弹「不兼容」等于什么都没说。</summary>
        private static string ReasonTextOf(PluginFailureKind kind) => kind switch
        {
            PluginFailureKind.NotAPlugin =>
                "这个 DLL 里没有找到插件入口（INotchPlugin），可能是按旧版接口编译的，也可能根本不是 NPS 插件。",
            PluginFailureKind.BadImage =>
                "这个 DLL 无法作为 .NET 程序集加载：可能是 32 / 64 位不匹配，或文件本身已损坏。",
            PluginFailureKind.MissingDependency =>
                "插件缺少依赖文件：请确认整个插件包（不只是那个 dll）都放进来了。",
            PluginFailureKind.HostMismatch =>
                "插件引用了当前 NPS 里不存在的接口，多半是按旧版 API 编译的。",
            PluginFailureKind.InitFailed =>
                "插件初始化时抛出了异常，通常是它与当前 NPS 版本不兼容。",
            _ => "可能是插件与 NPS 的兼容性问题。",
        };

        // ---- 布局真源 ----
        //             列表行起点 +92
        // 渲染 / 命中 / 滚轮三处共用，改一处必须同步。
        //    收到 50 后两张卡各多显示一行。
        // 48 而不是 50：列表区的可用高度只差一点点，"多放一行"就是靠这 2px 换来的
        // （市场页 580/50=11 行 → 580/48=12 行；行内元素最大偏移 44，48 仍放得下）。
        private const float PluginListRowH = 48f;

        private const float PluginTopCardH = 96f;   // 「我的插件」顶部操作卡高度

        private void GetPluginListCardTop(out float listY)
            => listY = TITLE_BAR_HEIGHT + CARD_TOP_Y + PluginTopCardH + CARD_GAP;   // 118

        // listY 到行区顶：44 → 40，首行离「已安装插件」标题太近，整体上移 4px。
        private const float PluginRowsTopPad = 40f;

        // ── 第一行：分类下拉（左）+ 搜索框 ──
        private const float MarketControlsY = TITLE_BAR_HEIGHT + 22f;   // 第一行顶（54，卡顶下留 10）
        private const float MarketControlH = 26f;                       // 控件行高（两行共用）
        private const float MarketCatBtnW = 132f;                       // 分类按钮宽（左起 CONTENT_TEXT_X）
        private const float MarketSearchX = CONTENT_TEXT_X + MarketCatBtnW + 16f;       // 350
        private const float MarketSearchW = WIDTH - MarketRightPad - MarketSearchX;     // 214

        private const float MarketStatusRowY = TITLE_BAR_HEIGHT + 58f;  // 第二行顶（90）
        private const float MarketStatusBaseline = MarketStatusRowY + 17f;
        private const float MarketRefreshW = 50f;
        private const float MarketRefreshX = WIDTH - MarketRightPad - MarketRefreshW;   // 514
        private const float MarketRowsTop = TITLE_BAR_HEIGHT + 96f;   // 92 → 96：首行离「只看已安装」那排太近，整体下移 4px

        private const float MarketChkX = CONTENT_TEXT_X;
        private const float MarketChkBoxSize = 16f;
        private const string MarketChkLabel = "只看已安装";
        private const float MarketChkLabelGap = 7f;
        private static float MarketChkLabelX => MarketChkX + MarketChkBoxSize + MarketChkLabelGap;
        private const float MarketChkY = MarketStatusRowY + 5f;

        private const float MarketBtnW = 50f;
        private const float MarketRightPad = CONTENT_TEXT_RM + 8f;                      // 市场行右侧基准（36）
        private const float MarketBtn3X = WIDTH - MarketRightPad - MarketBtnW;          // 514 详情
        private const float MarketBtn2X = MarketBtn3X - 6f - MarketBtnW;                // 458 卸载
        private const float MarketBtn1X = MarketBtn2X - 6f - MarketBtnW;                // 402 下载/更新/重装

        // 排序下拉：贴着刷新按钮左侧，同一行高。
        private const float MarketSortW = 76f;
        private const float MarketSortX = MarketRefreshX - 10f - MarketSortW;           // 428

        // ── 「一键更新全部插件」条：贴卡片底缘，仅有可更新插件时出现 ──
        // 出现时列表高度让位（GetMarketListLayout 扣掉 Reserve），渲染 / 命中 / 滚轮三处同源。
        private const int MarketUpdateBarH = 30;
        private const int MarketUpdateBarPad = 10;                       // 距卡片底缘
        private static float MarketUpdateBarTop => HEIGHT - 20f - MarketUpdateBarPad - MarketUpdateBarH;
        private static SKRect MarketUpdateBarRect
            => new SKRect(CONTENT_TEXT_X, MarketUpdateBarTop, WIDTH - CONTENT_TEXT_RM, MarketUpdateBarTop + MarketUpdateBarH);

        private static readonly (string Key, string Name)[] MarketCategories =
        {
            ("all", "全部插件"),
            ("theme", "主题外观"),
            ("media", "媒体增强"),
            ("utility", "效率工具"),
            ("dev", "开发者"),
        };

        private static string MarketCategoryName(string key)
        {
            foreach (var (k, n) in MarketCategories)
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return n;
            return "全部插件";
        }

        private static readonly string[] MarketCategoryNames = MarketCategories.Select(c => c.Name).ToArray();

        private void RefreshMarketFilter()
        {
            _marketView.Clear();
            string q = _marketSearch.Trim();
            bool filterCat = !string.Equals(_marketCategoryKey, "all", StringComparison.OrdinalIgnoreCase);
            foreach (var mp in _marketPlugins)
            {
                if (_marketOnlyInstalled && MatchLocalPlugin(mp) == null) continue;   // 只看已安装
                if (filterCat && !string.Equals(mp.Category, _marketCategoryKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (q.Length > 0
                    && !mp.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    && !mp.Author.Contains(q, StringComparison.OrdinalIgnoreCase)
                    && !mp.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                    && !mp.Tags.Contains(q, StringComparison.OrdinalIgnoreCase))
                    continue;
                _marketView.Add(mp);
            }

            _marketView.Sort(CompareMarketPlugins);
            _marketScroll = 0;
            // 过滤结果变了，原下标全部失效：只有靠下标的详情 / 评分弹窗要跟着收。
            // 提示类弹窗（加载失败 / 卸载确认）跟列表无关，不能被顺手关掉。
            if (!IsConfirmDialogOpen) CloseMarketDialog();
            ResetPluginHover();
        }

        // 排序比较器：官方插件无视关键字永远置顶（优先级最高），其余按所选关键字排。
        private int CompareMarketPlugins(MarketPlugin a, MarketPlugin b)
        {
            int byOfficial = b.Official.CompareTo(a.Official);
            if (byOfficial != 0) return byOfficial;

            int r = _marketSortIndex switch
            {
                1 => (b.Rating, b.RatingCount).CompareTo((a.Rating, a.RatingCount)),   // 评分：同分看评分人数
                2 => string.CompareOrdinal(b.Updated, a.Updated),                      // 最新：更新时间串倒排（ISO 式格式可直接比）
                3 => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture),  // 名称：本地化字典序
                _ => b.Downloads.CompareTo(a.Downloads),                               // 最热：下载量
            };
            if (r != 0) return r;

            int byName = string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);   // 平手兜底：保证顺序稳定
            return byName != 0 ? byName : string.CompareOrdinal(a.Id, b.Id);
        }

        private void GetMarketListLayout(out int visibleRows, out int maxFirstRow)
        {
            // 有更新时底部给「一键更新」条让位；没有就列表吃满，一行不多占。
            int reserve = MarketUpdateCount > 0 ? MarketUpdateBarH + MarketUpdateBarPad : 0;
            int maxRows = Math.Max(1, (int)((HEIGHT - 20 - MarketRowsTop - reserve) / PluginListRowH));
            int total = _marketView.Count;
            visibleRows = Math.Min(total, maxRows);
            maxFirstRow = Math.Max(0, total - visibleRows);
        }

        private void EnsureMarketData()
        {
            if (_marketTriedFetch || _marketFetching) return;
            _marketTriedFetch = true;
            _marketFetching = true;
            _marketError = "";

            Task.Run(async () =>
            {
                try
                {
                    string json = await _marketHttp.GetStringAsync(MarketApiUrl);
                    _marketPlugins = ParseMarket(json);
                }
                catch (Exception ex)
                {
                    _marketError = "市场加载失败，请点击刷新重试";
                    Logger.Error("[Market] 拉取插件市场失败", ex);
                }
                finally
                {
                    _marketFetching = false;
                    _marketDataDirty = true;   // 过滤视图重建放到 UI 线程（见 RenderTabMarket）
                    InvalidateMarketUpdateCount();   // 市场数据换了一批：可更新计数作废重算
                    PostAsyncRerender();
                }
            });
        }

        private static List<MarketPlugin> ParseMarket(string json)
        {
            var list = new List<MarketPlugin>();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var p in plugins.EnumerateArray())
            {
                var mp = new MarketPlugin
                {
                    Id = p.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Name = p.TryGetProperty("name", out var name) ? StripInvisible(name.GetString() ?? "") : "",
                    Author = p.TryGetProperty("author", out var author) ? StripInvisible(author.GetString() ?? "") : "",
                    Desc = p.TryGetProperty("desc", out var desc) ? StripInvisible(desc.GetString() ?? "") : "",
                    Version = p.TryGetProperty("version", out var ver) ? ver.GetString() ?? "" : "",
                    Updated = p.TryGetProperty("updated", out var upd) ? upd.GetString() ?? "" : "",
                    Official = p.TryGetProperty("official", out var off) && off.ValueKind == JsonValueKind.True,
                    Category = p.TryGetProperty("category", out var cat) ? cat.GetString() ?? "" : "",
                };
                if (p.TryGetProperty("downloads", out var dl) && dl.TryGetInt32(out int dlc)) mp.Downloads = dlc;
                if (p.TryGetProperty("rating", out var rt) && rt.TryGetDouble(out double rtv)) mp.Rating = rtv;
                if (p.TryGetProperty("ratingCount", out var rc) && rc.TryGetInt32(out int rcv)) mp.RatingCount = rcv;
                if (p.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                {
                    var parts = new List<string>();
                    foreach (var t in tags.EnumerateArray())
                    {
                        string s = StripInvisible(t.GetString() ?? "");
                        if (s.Length > 0) parts.Add(s);
                    }
                    mp.Tags = string.Join(" · ", parts);
                }

                if (p.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in files.EnumerateArray())
                    {
                        string fn = f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        string url = f.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                        if (fn.Length == 0 || url.Length == 0) continue;
                        mp.FileName = fn;
                        mp.FileUrl = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : MarketApiBase + url;
                        mp.IsZip = fn.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                        break;
                    }
                }

                if (mp.Id.Length > 0 && mp.FileUrl.Length > 0) list.Add(mp);
            }
            return list;
        }

        private PluginEntry? MatchLocalPlugin(MarketPlugin mp)
        {
            var entries = PluginManager.Instance.Entries;
            if (entries.Count == 0) return null;

            // ① pluginId 原样比较
            foreach (var e in entries)
                if (!string.IsNullOrEmpty(e.Id) && string.Equals(e.Id, mp.Id, StringComparison.OrdinalIgnoreCase))
                    return e;

            string normId = NormalizeKey(mp.Id);
            string normFile = NormalizeKey(Path.GetFileNameWithoutExtension(mp.FileName));
            string normName = NormalizeKey(mp.Name);

            foreach (var e in entries)
            {
                foreach (string cand in PluginIdentityStrings(e))
                {
                    string n = NormalizeKey(cand);
                    if (n.Length == 0) continue;
                    if (n == normId || (normFile.Length > 0 && n == normFile)) return e;
                }
            }

            // ③ 显示名相同（作者两边写的是同一个名字）
            if (normName.Length > 0)
            {
                foreach (var e in entries)
                {
                    if (NormalizeKey(e.FriendlyName) == normName) return e;
                    if (NormalizeKey(e.CachedName) == normName) return e;
                }
            }

            // ④ 目录型布局的目录名 == 市场 id
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.RootDir)) continue;
                string dir = NormalizeKey(Path.GetFileName(e.RootDir));
                if (dir.Length > 0 && (dir == normId || (normFile.Length > 0 && dir == normFile))) return e;
            }

            // ⑤ 归一化互相包含（带长度下限，防误判）
            const int MinLen = 4;
            if (normId.Length >= MinLen || normFile.Length >= MinLen)
            {
                foreach (var e in entries)
                {
                    foreach (string cand in PluginIdentityStrings(e))
                    {
                        string n = NormalizeKey(cand);
                        if (n.Length < MinLen) continue;
                        if (ContainsEither(n, normId) || ContainsEither(n, normFile)) return e;
                    }
                }
            }

            return null;
        }

        private static bool ContainsEither(string a, string b)
        {
            const int MinLen = 4;
            if (a.Length < MinLen || b.Length < MinLen) return false;
            return a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
        }

        private static IEnumerable<string> PluginIdentityStrings(PluginEntry e)
        {
            if (!string.IsNullOrEmpty(e.Id)) yield return e.Id;
            if (!string.IsNullOrEmpty(e.Key))
            {
                int slash = e.Key.IndexOf('/');
                if (slash > 0) yield return e.Key[..slash];
            }
            if (!string.IsNullOrEmpty(e.DllPath)) yield return Path.GetFileNameWithoutExtension(e.DllPath);
            if (!string.IsNullOrEmpty(e.CachedName)) yield return e.CachedName;
            if (!string.IsNullOrEmpty(e.FriendlyName)) yield return e.FriendlyName;
        }

        private static string NormalizeKey(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c is '-' or '_' or ' ' or '.') continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private MarketPlugin? GetMarketAt(int index)
            => index >= 0 && index < _marketView.Count ? _marketView[index] : null;

        // ---- 从通知跳转到某个插件的详情 ----

        /// <summary>
        /// 外部（岛上的推荐通知）入口：打开设置中心 → 插件市场，并弹出指定插件的详情弹窗。
        /// 市场数据是异步拉的，所以先记下 id，等数据就绪后再定位。
        /// </summary>
        public static void ShowMarketPluginDetail(string pluginId)
        {
            if (string.IsNullOrWhiteSpace(pluginId)) return;
            if (_instance == null) _instance = new ConsoleWindow();
            _instance.OpenMarketPluginDetail(pluginId);
            Toggle();
        }

        private void OpenMarketPluginDetail(string pluginId)
        {
            _pendingMarketDetailId = pluginId;

            // 定位不能受上一次的筛选影响：搜索 / 分类 / 只看已安装全部回到默认值。
            _marketCategoryKey = "all";
            _marketSearch = "";
            _marketSearchCaret = _marketSearchSelAnchor = 0;
            _marketSearchFocused = false;
            _marketSearchDragging = false;
            _marketSearchViewFrozen = -1;
            _marketImeComposing = "";
            _marketOnlyInstalled = false;
            _marketCategoryOpen = false;
            _marketSortOpen = false;
            CloseMarketDialog(render: false);

            // 上次拉取失败时放开幂等闸，和点侧栏进市场页的重试口径一致。
            if (_marketError.Length > 0 && !_marketFetching)
            {
                _marketError = "";
                _marketTriedFetch = false;
            }

            SwitchTab(7);
            EnsureMarketData();
            TryOpenPendingMarketDetail();
            Render();
        }

        /// <summary>
        /// 数据就绪后把待定位的插件打开成详情弹窗。绘制页时也会调用一次（见 RenderTabMarket），
        /// 所以首次进来时市场还没拉到也不会丢目标。
        /// </summary>
        private void TryOpenPendingMarketDetail()
        {
            if (_pendingMarketDetailId.Length == 0) return;
            if (_marketFetching || _marketDataDirty) return;   // 等数据落地

            if (_marketError.Length > 0)
            {
                _pendingMarketDetailId = "";
                SetMarketHint("插件市场加载失败，无法打开插件详情", true);
                PostAsyncRerender();   // 失败文案要再走一帧才画得出来
                return;
            }

            var mp = FindMarketPluginForDetail(_pendingMarketDetailId);
            _pendingMarketDetailId = "";
            if (mp == null)
            {
                SetMarketHint("插件市场里没有找到「网易酷狗时间轴获取插件」", true);
                PostAsyncRerender();
                return;
            }

            // 无条件重建一次过滤视图：搜索 / 分类 / 只看已安装在这之前可能已经被重置，
            // 但 _marketView 仍是旧的筛选结果 —— 不重建就会 IndexOfMarketView 找不到目标。
            _marketCategoryKey = "all";
            _marketSearch = "";
            _marketOnlyInstalled = false;
            RefreshMarketFilter();

            int idx = IndexOfMarketView(mp);
            if (idx < 0)
            {
                SetMarketHint("插件市场里没有找到「网易酷狗时间轴获取插件」", true);
                PostAsyncRerender();
                return;
            }

            // 详情是浮在列表上的：把对应行滚到可见范围中间，关掉弹窗后一眼能看到它。
            GetMarketListLayout(out int rows, out int maxFirst);
            _marketScroll = Math.Clamp(idx - Math.Max(0, rows / 2), 0, maxFirst);

            _marketDialog = MarketDialog.Detail;
            _marketDialogIndex = idx;
            _hoveredDialogClose = false;
            _hoveredDialogButton = -1;
            Logger.Info($"[Market] 已定位到插件详情：{mp.Id}（{mp.Name}）");
        }

        /// <summary>按 id 找市场条目：先精确匹配 id，再归一化后做包含匹配（兼容 zzjjack.mediacdp 这类带前缀的 id）。</summary>
        private MarketPlugin? FindMarketPluginForDetail(string key)
        {
            string norm = NormalizeKey(key);
            foreach (var mp in _marketPlugins)
                if (string.Equals(mp.Id, key, StringComparison.OrdinalIgnoreCase)) return mp;

            if (norm.Length == 0) return null;

            foreach (var mp in _marketPlugins)
                if (NormalizeKey(mp.Id).Contains(norm, StringComparison.Ordinal)) return mp;
            foreach (var mp in _marketPlugins)
                if (NormalizeKey(mp.FileName).Contains(norm, StringComparison.Ordinal)
                    || NormalizeKey(mp.Name).Contains(norm, StringComparison.Ordinal)) return mp;
            return null;
        }

        // 高度一律按「内容行数」现算，避免渲染与命中各算一份。

        private const float DialogW = 380f;
        private const float DialogPad = 16f;
        private const float DialogTitleH = 34f;      // 标题行高（❌ 也在这一行）
        private const float DialogLineH = 18f;
        private const float DialogBtnH = 26f;
        private const float DialogBtnW = 84f;

        private static float DialogInnerW => DialogW - DialogPad * 2;

        private static SKRect GetMarketDialogRect(float bodyLines, bool buttons)
        {
            // 标题行 → 正文区的起点与 DrawDialogHeader 的返回值严格一致（Top + DialogTitleH + 8），
            // 否则按钮会按一个高度算、正文按另一个高度画，留白忽大忽小。
            float ph = DialogTitleH + 8f;
            if (bodyLines > 0f) ph += bodyLines * DialogLineH;
            // 按钮区：正文末到按钮、按钮到弹窗底各留同样多。
            // 原来是「上沿 6 / 下沿 18」，按钮贴着正文、离底边老远，整块看着往下掉。
            if (buttons) ph += DialogBtnPad + DialogBtnH + DialogBtnPad;
            else ph += 14f;   // 无按钮弹窗（详情 / 评分）的底部呼吸，与原高度一致
            float px = (WIDTH - DialogW) / 2f;
            float py = Math.Max(TITLE_BAR_HEIGHT + 24f, (HEIGHT - ph) / 2f);
            return new SKRect(px, py, px + DialogW, py + ph);
        }

        private static SKRect GetMarketDialogCloseRect(SKRect popup)
            => new SKRect(popup.Right - DialogPad - 18f, popup.Top + 8f, popup.Right - DialogPad, popup.Top + 8f + 18f);

        private static SKRect GetRateStarsRect(SKRect popup)
        {
            float y = popup.Top + DialogTitleH + 8f + DialogLineH + 6f;
            float w = Math.Min(260f, DialogInnerW);
            return new SKRect(popup.Left + DialogPad, y, popup.Left + DialogPad + w, y + 26f);
        }

        private SKRect GetMarketDetailRect(MarketPlugin mp)
        {
            var descLines = WrapText(mp.Desc, _subTextPaint, DialogInnerW);
            const int MaxDescLines = 7;
            int lineCount = Math.Min(descLines.Count, MaxDescLines);
            float lines = 2f + lineCount + (mp.Tags.Length > 0 ? 1f : 0f);
            // 详情弹窗右下角常驻「安装 / 已安装」按钮：所有市场插件一视同仁。
            return GetMarketDialogRect(lines, true);
        }

        private static SKRect GetRateDialogRect() => GetMarketDialogRect(4f, false);

        // ---- 「插件加载失败」弹窗 ----
        //
        // 正文里有一串可点的群号：折行之后位置就散了，所以打开弹窗时按当前字体排一次版面，
        // 绘制与命中都读这份结果。绘制自己折一遍、命中再折一遍，是这类行内链接最常见的错位来源。

        private const float LoadFailedBodyDY = 12f;   // 正文首行基线距标题区末行（与其它弹窗同值）

        private const string LoadFailedLinkText = "1080730621";

        private const string LoadFailedQqUrl =
            "https://qm.qq.com/cgi-bin/qm/qr?k=i70z7rbl-VWpejQugvlXeARDUjwP7sIW&jump_from=webapi"
            + "&authKey=b6Pj6zLuuCINDhafPJRttePdy3D45vvtWzcZ109LWoWYXkcKo8bNWI7fMhr+yV87";

        private static readonly (string Text, bool IsLink)[] LoadFailedRuns =
        {
            // 具体原因那句是动态的（_loadFailedReason，按失败类型挑），这里只放固定的后半段
            ("欢迎加入 QQ 群 ", false),
            (LoadFailedLinkText, true),
            ("，获取最新 NPS 测试版、一起讨论。", false),
        };

        // 「去插件详情看它要求的 NPS 版本」这句只对「版本 / 接口对不上」有意义：
        // 缺依赖、文件损坏、位数不匹配时，用户去查版本号是白跑一趟。
        private const string LoadFailedVersionHint = "具体插件依赖的 NPS 版本，可前往插件市场中该插件的详情里查看。";

        private static bool NeedsVersionHint(PluginFailureKind kind)
            => kind is PluginFailureKind.NotAPlugin
                    or PluginFailureKind.HostMismatch
                    or PluginFailureKind.InitFailed;

        // 失败原因那句：由 ShowPluginLoadFailedDialog 按 PluginFailureKind 填
        private string _loadFailedReason = "";
        private bool _loadFailedWithVersionHint = false;

        // 折行结果：每行若干段，段内文本属性一致（是否链接）
        private readonly List<List<(string Text, bool IsLink)>> _loadFailedLines = new();

        private int _loadFailedLinkLine = -1;      // 链接所在行号（-1 = 文案里没排到）
        private float _loadFailedLinkOffsetX;      // 链接起点相对正文左缘的偏移
        private float _loadFailedLinkWidth;

        private bool _loadFailedLinkHovered = false;

        /// <summary>按当前字体把失败文案排成行。群号单独成段，且保证不跨行（跨行后命中区就散了）。</summary>
        private void BuildLoadFailedLayout()
        {
            _loadFailedLines.Clear();
            _loadFailedLinkLine = -1;
            _loadFailedLinkOffsetX = 0f;
            _loadFailedLinkWidth = 0f;

            float maxW = DialogInnerW;
            float linkW = _subTextPaint.MeasureText(LoadFailedLinkText);

            var line = new List<(string Text, bool IsLink)>();
            var sb = new System.Text.StringBuilder();
            float x = 0f;
            bool sbIsLink = false;

            void Flush()
            {
                if (sb.Length == 0) return;
                line.Add((sb.ToString(), sbIsLink));
                sb.Clear();
            }

            void BreakLine()
            {
                Flush();
                if (line.Count > 0) _loadFailedLines.Add(line);
                line = new List<(string, bool)>();
                x = 0f;
            }

            // 先排「为什么失败」那句，再接固定的群 / 版本提示
            var runs = new List<(string Text, bool IsLink)>(LoadFailedRuns.Length + 2);
            if (_loadFailedReason.Length > 0) runs.Add((_loadFailedReason, false));
            runs.AddRange(LoadFailedRuns);
            if (_loadFailedWithVersionHint) runs.Add((LoadFailedVersionHint, false));

            foreach (var (text, isLink) in runs)
            {
                // 整串群号必须落在同一行：当前行剩余宽度不够就先把这一行断掉
                if (isLink && x + linkW > maxW && (sb.Length > 0 || line.Count > 0)) BreakLine();

                foreach (char c in text)
                {
                    float w = _subTextPaint.MeasureText(c.ToString());

                    // 链接段内部绝不换行（上面已保证放得下），否则命中区会跨两行
                    if (!isLink && x + w > maxW && (sb.Length > 0 || line.Count > 0)) BreakLine();

                    if (sb.Length > 0 && sbIsLink != isLink)
                    {
                        line.Add((sb.ToString(), sbIsLink));
                        sb.Clear();
                    }
                    sbIsLink = isLink;

                    if (isLink && _loadFailedLinkLine < 0)
                    {
                        _loadFailedLinkLine = _loadFailedLines.Count;   // 当前正在凑的这一行
                        _loadFailedLinkOffsetX = x;
                    }

                    sb.Append(c);
                    x += w;
                }

                if (isLink)
                {
                    // 整段走完才定宽：逐字累加会被 kerning 吃掉一点，直接量整串更准
                    _loadFailedLinkWidth = linkW;
                }
            }

            BreakLine();
        }

        // 失败弹窗的行数由文案折行结果决定，不能写死 2 行。
        // 顺手兜底：绘制 / 悬停 / 点击三条路都要经过这里，万一哪条没走 ShowPluginLoadFailedDialog，
        // 版面就在这里补出来 —— 空版面会让正文整块消失、链接也永远点不中。
        private SKRect GetNoticeDialogRect()
        {
            if (_loadFailedLines.Count == 0) BuildLoadFailedLayout();
            return GetMarketDialogRect(Math.Max(2, _loadFailedLines.Count), true);
        }

        /// <summary>失败弹窗正文里群号的命中区：行号与横向偏移都来自打开时算好的版面。</summary>
        private bool HitLoadFailedLink(SKRect rect, float x, float y)
        {
            if (_loadFailedLinkLine < 0) return false;

            float bodyY = rect.Top + DialogTitleH + 8f + LoadFailedBodyDY;
            float lineY = bodyY + _loadFailedLinkLine * DialogLineH;
            float x0 = rect.Left + DialogPad + _loadFailedLinkOffsetX;

            // 纵向按 12pt 中文的上下沿放宽（基线 -14 ~ +6），横向各留 2px —— 别让用户非得点正字心
            return x >= x0 - 2f && x <= x0 + _loadFailedLinkWidth + 2f
                && y >= lineY - 14f && y <= lineY + 6f;
        }

        private void OpenLoadFailedQqLink()
        {
            try
            {
                Process.Start(new ProcessStartInfo(LoadFailedQqUrl) { UseShellExecute = true });
            }
            catch
            {
                // 没装浏览器 / 被安全软件拦：弹窗不能因此卡住
            }
        }

        private const float DialogBtnGap = 10f;

        private const float DialogBtnPad = 14f;   // 正文末 → 按钮 / 按钮 → 弹窗底，两边同值

        /// <summary>
        /// 弹窗按钮统一几何：一组按钮<b>靠右</b>排（与右上角 ❌ 同侧，Windows 惯例），
        /// index 从左到右 0 起、最右那个是主操作。绘制与命中都从这里取，别再各算一套。
        /// 居中摆一个按钮在 380 宽的弹窗里两侧各空 130，重心是散的；右对齐后和标题、❌ 同一条竖线收口。
        /// </summary>
        private static SKRect GetDialogButtonRect(SKRect popup, int index, int count)
        {
            float right = popup.Right - DialogPad;
            float x = right - (count - index) * DialogBtnW - (count - index - 1) * DialogBtnGap;
            float y = popup.Bottom - DialogBtnPad - DialogBtnH;
            return new SKRect(x, y, x + DialogBtnW, y + DialogBtnH);
        }

        private static int DialogButtonCount(MarketDialog kind) => kind switch
        {
            MarketDialog.UninstallData => 3,   // 打开目录 / 保留 / 删除
            _ => 1,
        };

        private int HitDialogButton(SKRect popup, float x, float y)
        {
            int count = DialogButtonCount(_marketDialog);
            for (int i = 0; i < count; i++)
                if (GetDialogButtonRect(popup, i, count).Contains(x, y)) return i;
            return -1;
        }

        /// <summary>卸载第一级：只问「确定卸载吗」。</summary>
        private static SKRect GetUninstallConfirmDialogRect() => GetMarketDialogRect(2f, true);

        /// <summary>卸载第二级：多一行路径，所以多一行高度。</summary>
        private static SKRect GetUninstallDirDialogRect() => GetMarketDialogRect(3f, true);

        private SKRect GetCurrentDialogRect()
        {
            if (_marketDialog == MarketDialog.LoadFailed) return GetNoticeDialogRect();
            if (_marketDialog == MarketDialog.UninstallConfirm) return GetUninstallConfirmDialogRect();
            if (_marketDialog == MarketDialog.UninstallData) return GetUninstallDirDialogRect();

            var mp = GetMarketAt(_marketDialogIndex);
            if (mp == null) return SKRect.Empty;
            return _marketDialog switch
            {
                MarketDialog.Detail => GetMarketDetailRect(mp),
                MarketDialog.Rate => GetRateDialogRect(),
                _ => SKRect.Empty,
            };
        }

        // 评分粒度：半星（0.5 ~ 5.0）。

        private static readonly HttpClient _rateHttp = new(new HttpClientHandler
        {
            CookieContainer = new System.Net.CookieContainer(),
            UseCookies = true,
        })
        { Timeout = TimeSpan.FromSeconds(12) };

        private void OpenRateDialog(MarketPlugin mp, bool render = true)
        {
            _marketDialog = MarketDialog.Rate;
            // 漏了它弹窗会因为「拿不到插件」被当成失效而直接关掉。
            _marketDialogIndex = IndexOfMarketView(mp);
            if (_marketDialogIndex < 0) return;
            _rateStars = 0;
            _rateMine = 0;
            _rateAverage = mp.Rating;
            _rateCount = mp.RatingCount;
            _rateStatus = "";
            _rateStatusIsError = false;
            _rateLoading = true;
            if (render) Render();

            Task.Run(async () =>
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get,
                        $"{MarketApiUrl}?action=rating&id={Uri.EscapeDataString(mp.Id)}");
                    req.Headers.Add("X-Requested-With", "XMLHttpRequest");
                    using var resp = await _rateHttp.SendAsync(req);
                    string body = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                    {
                        if (root.TryGetProperty("rating", out var r) && r.TryGetDouble(out double rv)) _rateAverage = rv;
                        if (root.TryGetProperty("count", out var c) && c.TryGetInt32(out int cv)) _rateCount = cv;
                        if (root.TryGetProperty("mine", out var m) && m.TryGetDouble(out double mv)) _rateMine = mv;
                        mp.Rating = _rateAverage;   // 同步回列表
                        mp.RatingCount = _rateCount;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("[Market] 读取评分状态失败", ex);
                    _rateStatus = "评分状态读不到，稍后可重试";
                    _rateStatusIsError = true;
                }
                finally
                {
                    _rateLoading = false;
                    PostAsyncRerender();
                }
            });
        }

        private void SubmitRating(MarketPlugin mp, double stars)
        {
            if (_rateLoading || _rateMine > 0) return;
            _rateLoading = true;
            _rateStatus = "正在提交评分…";
            _rateStatusIsError = false;
            Render();

            Task.Run(async () =>
            {
                try
                {
                    using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["id"] = mp.Id,
                        ["rating"] = stars.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                    });
                    using var req = new HttpRequestMessage(HttpMethod.Post, $"{MarketApiUrl}?action=rate") { Content = content };
                    req.Headers.Add("X-Requested-With", "XMLHttpRequest");
                    using var resp = await _rateHttp.SendAsync(req);
                    string body = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;

                    bool ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                    if (root.TryGetProperty("rating", out var r) && r.TryGetDouble(out double rv)) _rateAverage = rv;
                    if (root.TryGetProperty("count", out var c) && c.TryGetInt32(out int cv)) _rateCount = cv;
                    if (root.TryGetProperty("mine", out var m) && m.TryGetDouble(out double mv)) _rateMine = mv;

                    if (ok)
                    {
                        _rateStatus = $"已记录您的评分：{FormatScore(_rateMine)} 分，感谢反馈！";
                        _rateStatusIsError = false;
                    }
                    else
                    {
                        string err = root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
                        _rateStatus = err switch
                        {
                            "already_rated" => _rateMine > 0
                                ? $"您已经给此插件打了 {FormatScore(_rateMine)} 分，感谢您的参与"
                                : "您已经给此插件打过分了，感谢您的参与",
                            "too_many" => "提交太频繁了，请过一会儿再试。",
                            _ => "评分提交失败，请稍后再试。",
                        };
                        _rateStatusIsError = err != "already_rated";
                    }
                    mp.Rating = _rateAverage;
                    mp.RatingCount = _rateCount;
                }
                catch (Exception ex)
                {
                    Logger.Error("[Market] 提交评分失败", ex);
                    _rateStatus = "评分提交失败，请稍后再试。";
                    _rateStatusIsError = true;
                }
                finally
                {
                    _rateLoading = false;
                    PostAsyncRerender();
                }
            });
        }

        private static int CompareVersions(string a, string b)
        {
            bool oa = Version.TryParse(a.TrimStart('v', 'V'), out var va);
            bool ob = Version.TryParse(b.TrimStart('v', 'V'), out var vb);
            if (oa && ob) return va!.CompareTo(vb);
            return string.CompareOrdinal(a, b);
        }

        private static string MarketInstallLabel(MarketPlugin mp, PluginEntry? local, bool disabled)
        {
            // 已装但没在跑：主按钮不再是「重装」，而是把插件重新跑起来的「启用」。
            if (local != null && disabled) return "启用";
            if (local == null) return "下载";
            if (local.Version.Length == 0) return "重装";
            return CompareVersions(mp.Version, local.Version) > 0 ? "更新" : "重装";
        }

        // ---- 可更新统计（侧栏「插件市场」角标 + 一键更新按钮共用同一套判据）----
        //   判据与行内「更新」按钮严格一致：已安装、在跑、本地版本可解析、市场版本更新。
        //   计数带脏标记：MatchLocalPlugin 全量跑一遍有字符串归一化开销，不能每帧算。

        private int _marketUpdateCount;
        private bool _marketUpdateCountDirty = true;

        private void InvalidateMarketUpdateCount() => _marketUpdateCountDirty = true;

        private int MarketUpdateCount
        {
            get
            {
                if (_marketUpdateCountDirty)
                {
                    _marketUpdateCountDirty = false;
                    int n = 0;
                    foreach (var mp in _marketPlugins)
                        if (IsUpdatable(mp)) n++;
                    _marketUpdateCount = n;
                }
                return _marketUpdateCount;
            }
        }

        private bool IsUpdatable(MarketPlugin mp)
        {
            var local = MatchLocalPlugin(mp);
            if (local == null || PluginManager.Instance.IsDisabled(local)) return false;
            if (local.Version.Length == 0) return false;
            return CompareVersions(mp.Version, local.Version) > 0;
        }

        // ---- 一键更新全部 ----
        //   StartMarketInstall 的 _marketBusyId 闸一口只吃一个下载 ⇒ 队列逐个走：
        //   上一份安装任务的 finally 会 PostAsyncRerender，渲染入口（Render 里）接着踢下一条 ——
        //   不直接在后台线程里连环启动，保持「安装动作只在 UI 线程发生」的老规矩。

        private readonly List<MarketPlugin> _updateAllQueue = new();
        private bool _updateAllRunning;
        private int _updateAllTotal;
        private bool _hoveredMarketUpdateAll;

        private string UpdateAllProgressText
        {
            get
            {
                int busy = _marketBusyId.Length > 0 ? 1 : 0;
                int done = Math.Max(0, _updateAllTotal - _updateAllQueue.Count - busy);
                return $"正在更新（{done}/{_updateAllTotal}）…";
            }
        }

        private void StartUpdateAll()
        {
            if (_updateAllRunning || _marketBusyId.Length > 0) return;
            _updateAllQueue.Clear();
            foreach (var mp in _marketPlugins)
                if (IsUpdatable(mp)) _updateAllQueue.Add(mp);
            if (_updateAllQueue.Count == 0) return;
            _updateAllTotal = _updateAllQueue.Count;
            _updateAllRunning = true;
            KickUpdateAllQueue();
        }

        /// <summary>只在 UI 线程调（渲染入口）。队列空了收尾；队头失效（中途被卸载 / 已是最新）就地跳过。</summary>
        private void KickUpdateAllQueue()
        {
            if (!_updateAllRunning || _marketBusyId.Length > 0) return;
            while (_updateAllQueue.Count > 0)
            {
                var mp = _updateAllQueue[0];
                _updateAllQueue.RemoveAt(0);
                if (!IsUpdatable(mp)) continue;
                StartMarketInstall(mp);
                return;
            }
            _updateAllRunning = false;
            _updateAllTotal = 0;
        }

        private void PostAsyncRerender()
        {
            var inst = _instance;
            if (inst != null && inst._hwnd != IntPtr.Zero)
                Win32.PostMessage(inst._hwnd, WM_ASYNC_RERENDER, IntPtr.Zero, IntPtr.Zero);
        }

        private bool IsInMarketSearchBox()
        {
            if (!TryGetCursorClientPos(out int x, out int y)) return false;
            return x >= MarketSearchX && x <= MarketSearchX + MarketSearchW
                && y >= MarketControlsY && y <= MarketControlsY + MarketControlH;
        }

        private bool HandleMarketSearchKey(int vk, char ch)
        {
            if (_selectedTab != 7 || !_marketSearchFocused) return false;

            if (vk == Win32.VK_ESCAPE || vk == Win32.VK_RETURN)
            {
                _marketSearchFocused = false;
                MarketCaretToEnd();               // 失去焦点不留选区（高亮只在聚焦时画，这里顺手清干净）
                Render();
                return true;
            }

            bool shift = (Win32.GetKeyState(Win32.VK_SHIFT) & 0x8000) != 0;
            bool ctrl = (Win32.GetKeyState(Win32.VK_CONTROL) & 0x8000) != 0;

            int MoveCaret(int target)
            {
                target = Math.Clamp(target, 0, _marketSearch.Length);
                if (target == _marketSearchCaret) return target;
                _marketSearchCaret = target;
                if (!shift) _marketSearchSelAnchor = target;
                Render();
                return target;
            }

            switch (vk)
            {
                case Win32.VK_LEFT: MoveCaret((shift ? _marketSearchCaret : MarketSelStart) - 1); return true;
                case Win32.VK_RIGHT: MoveCaret((shift ? _marketSearchCaret : MarketSelEnd) + 1); return true;
                case Win32.VK_HOME: MoveCaret(0); return true;
                case Win32.VK_END: MoveCaret(_marketSearch.Length); return true;
                case Win32.VK_DELETE:
                    if (!MarketDeleteSelection())
                    {
                        // 没有选区：删插入点右边那个字符；已经在末尾就什么都不做
                        int at = Math.Clamp(_marketSearchCaret, 0, _marketSearch.Length);
                        if (at >= _marketSearch.Length) return true;
                        _marketSearch = _marketSearch[..at] + _marketSearch[(at + 1)..];
                    }
                    RefreshMarketFilter();
                    Render();
                    return true;
            }
            if (ctrl && (vk == Win32.VK_A || vk == Win32.VK_A + 32))       // Ctrl+A 全选
            {
                _marketSearchSelAnchor = 0;
                _marketSearchCaret = _marketSearch.Length;
                Render();
                return true;
            }
            if (vk == Win32.VK_BACK)                                                   // Backspace
            {
                if (!MarketDeleteSelection())
                {
                    int at = Math.Clamp(_marketSearchCaret, 0, _marketSearch.Length);
                    if (at == 0) return true;
                    _marketSearch = _marketSearch[..(at - 1)] + _marketSearch[at..];
                    _marketSearchCaret = _marketSearchSelAnchor = at - 1;
                }
                RefreshMarketFilter();
                Render();
                return true;
            }
            if (vk != -1) return true;   // 其它非字符键（方向键等）吞掉，不落进搜索串

            if (ch >= ' ' && ch != 0x7F)
            {
                MarketInsertText(ch.ToString());
                RefreshMarketFilter();
                Render();
            }
            return true;
        }

        // ---- 输入法（IME）----
        //   候选窗仍钉在搜索框下方（选词靠它）。

        private string _marketImeComposing = "";

        private bool HandleMarketIme(int msg, IntPtr lParam)
        {
            if (_selectedTab != 7 || !_marketSearchFocused || _hwnd == IntPtr.Zero) return false;

            switch (msg)
            {
                case Win32.WM_IME_STARTCOMPOSITION:
                    _marketImeComposing = "";
                    PositionImeWindows();
                    return false;

                case Win32.WM_IME_COMPOSITION:
                {
                    int flags = Win32.Low32(lParam);
                    if ((flags & Win32.GCS_RESULTSTR) != 0)
                    {
                        string result = ReadImeString(Win32.GCS_RESULTSTR);
                        if (result.Length > 0)
                        {
                            MarketInsertText(result);
                            _marketImeComposing = "";
                            RefreshMarketFilter();
                            Render();
                        }
                        return true;   // 结果串自己消化掉，别再让 DefWindowProc 转成 WM_CHAR（会重复一次）
                    }
                    if ((flags & Win32.GCS_COMPSTR) != 0)
                    {
                        string comp = ReadImeString(Win32.GCS_COMPSTR);
                        if (comp != _marketImeComposing)
                        {
                            _marketImeComposing = comp;
                            Render();
                        }
                        return false;  // 组字串交给默认处理，IME 才会推进组字状态（但不再画组字窗）
                    }
                    return false;
                }

                case Win32.WM_IME_ENDCOMPOSITION:
                    if (_marketImeComposing.Length > 0) { _marketImeComposing = ""; Render(); }
                    return false;
            }
            return false;
        }

        private string ReadImeString(int index)
        {
            IntPtr himc = Win32.ImmGetContext(_hwnd);
            if (himc == IntPtr.Zero) return "";
            try
            {
                int bytes = Win32.ImmGetCompositionStringW(himc, index, null, 0);
                if (bytes <= 0) return "";
                var buf = new byte[bytes];
                int got = Win32.ImmGetCompositionStringW(himc, index, buf, bytes);
                if (got <= 0) return "";
                return System.Text.Encoding.Unicode.GetString(buf, 0, got);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Market] 读取输入法字符串失败: {ex.Message}");
                return "";
            }
            finally
            {
                Win32.ImmReleaseContext(_hwnd, himc);
            }
        }

        private void PositionImeWindows()
        {
            IntPtr himc = Win32.ImmGetContext(_hwnd);
            if (himc == IntPtr.Zero) return;
            try
            {
                int px = (int)(MarketSearchX * _dpiScale);
                int py = (int)((MarketControlsY + MarketControlH) * _dpiScale);
                var pt = new Win32.POINT(px, py);

                // 组字串由宿主自己画在搜索框里（_marketImeComposing），所以用 CFS_FORCE_POSITION
                // 让输入法别再浮一个白色组字小窗；CFS_POINT 只会把它摆到这里照样画。
                var cf = new Win32.COMPOSITIONFORM { dwStyle = Win32.CFS_FORCE_POSITION, ptCurrentPos = pt };
                Win32.ImmSetCompositionWindow(himc, ref cf);

                var cand = new Win32.CANDIDATEFORM
                {
                    dwIndex = 0,
                    dwStyle = Win32.CFS_CANDIDATEPOS,
                    ptCurrentPos = pt,
                    rcArea = new Win32.RECT { Left = px, Top = py, Right = px + 1, Bottom = py + 1 },
                };
                Win32.ImmSetCandidateWindow(himc, ref cand);
            }
            catch (Exception ex) { Logger.Warn($"[Market] 设置输入法窗口位置失败: {ex.Message}"); }
            finally { Win32.ImmReleaseContext(_hwnd, himc); }
        }

        // ---- 提示自动消失 ----

        private static readonly IntPtr MARKET_HINT_TIMER_ID = new IntPtr(0x4E52);   // "NR"
        private const int MARKET_HINT_MS = 4000;
        private bool _marketHintTimerOn;
        private long _marketHintDeadline;    // Environment.TickCount64 的到期时刻

        private void SetMarketHint(string msg, bool isError)
        {
            _marketHint = msg;
            _marketHintIsError = isError;
            _marketHintDeadline = Environment.TickCount64 + MARKET_HINT_MS;
            if (!_marketHintTimerOn && _hwnd != IntPtr.Zero &&
                Win32.SetTimer(_hwnd, MARKET_HINT_TIMER_ID, 500, IntPtr.Zero) != IntPtr.Zero)
                _marketHintTimerOn = true;
        }

        private bool TickMarketHint(IntPtr timerId)
        {
            if (timerId != MARKET_HINT_TIMER_ID || !_marketHintTimerOn) return false;
            if (Environment.TickCount64 < _marketHintDeadline) return true;
            _marketHint = "";
            _marketHintIsError = false;
            StopMarketHintTimer();
            Render();
            return true;
        }

        private void StopMarketHintTimer()
        {
            if (!_marketHintTimerOn) return;
            if (_hwnd != IntPtr.Zero) Win32.KillTimer(_hwnd, MARKET_HINT_TIMER_ID);
            _marketHintTimerOn = false;
        }

        // ---- 安装 / 卸载 ----

        private void StartMarketInstall(MarketPlugin mp)
        {
            if (_marketBusyId.Length > 0) return;   // 同一时刻只允许一个下载任务

            // 本机已装但没在跑（禁用 / 上次加载失败）：主按钮写的是「启用」，点它就该把插件跑起来。
            // 再下一遍同样的包只是把网络、覆盖安装、重载全白做一遍，用户还得等进度。
            var local = MatchLocalPlugin(mp);
            if (local != null && PluginManager.Instance.IsDisabled(local))
            {
                bool started = PluginManager.Instance.SetEnabled(local, true);
                ResetPluginHover();
                RefreshPluginView();
                if (_marketOnlyInstalled) RefreshMarketFilter();
                if (!started) ReportPluginLoadFailure(local.Failure, local.FriendlyName, "启用失败");   // 启用 = 再加载一次，失败照样要说
                else Render();
                return;
            }

            _marketBusyId = mp.Id;
            // 停了表但不清文案，那条旧提示就会永远挂在状态行上。
            PostAsyncRerender();

            Task.Run(async () =>
            {
                bool ok = false;
                try
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "NotchPeninsula", "market");
                    string dlDir = Path.Combine(tempDir, mp.Id);
                    Directory.CreateDirectory(dlDir);
                    string tempFile = Path.Combine(dlDir, mp.FileName);

                    using (var resp = await _marketHttp.GetAsync(mp.FileUrl, HttpCompletionOption.ResponseHeadersRead))
                    {
                        resp.EnsureSuccessStatusCode();
                        await using var src = await resp.Content.ReadAsStreamAsync();
                        await using var dst = File.Create(tempFile);
                        await src.CopyToAsync(dst);
                    }

                    ok = mp.IsZip ? InstallZipPlugin(mp, tempFile) : InstallDllPlugin(mp, tempFile);
                }
                catch (Exception ex)
                {
                    Logger.Error("[Market] 安装插件失败", ex);
                    _marketPendingHint = $"安装 {mp.Name} 失败：{ex.Message}";
                    _marketPendingHintIsError = true;
                    // 下载 / 解压炸了跟插件本身没关系，不能沿用上一轮的失败类型去解释
                    _marketPendingFailure = PluginFailureKind.Other;
                    _marketPendingPluginName = mp.Name;
                }
                finally
                {
                    _marketBusyId = "";
                    _marketPendingHintDone = true;
                    _marketPendingRateIndex = ok ? IndexOfMarketView(mp) : -1;
                    PostAsyncRerender();
                }
            });
        }

        private string _marketPendingHint = "";
        private bool _marketPendingHintIsError;
        // 市场安装失败的类型：稍后弹窗按它挑说明句（安装是异步的，状态得先存着）
        private PluginFailureKind _marketPendingFailure = PluginFailureKind.Other;
        // 失败的是哪个插件：弹窗正文要指名，否则用户不知道刚才哪一步炸了
        private string _marketPendingPluginName = "";
        private bool _marketPendingHintDone;
        private int _marketPendingRateIndex = -1;

        private void ApplyPendingMarketResult()
        {
            if (!_marketPendingHintDone) return;
            _marketPendingHintDone = false;

            int rateIdx = _marketPendingRateIndex;
            _marketPendingRateIndex = -1;
            var rateTarget = rateIdx >= 0 && rateIdx < _marketView.Count ? _marketView[rateIdx] : null;

            RefreshPluginView();          // 已安装列表 + 市场行按钮文案都靠它重算
            if (_marketOnlyInstalled) RefreshMarketFilter();

            // 后台装完就得当场把失败弹出来，不管用户此刻在看哪一页 ——
            // 这里以前只在市场页的渲染里消费，切走一次结果就直接丢了。
            if (_marketPendingHintIsError)
                ReportPluginLoadFailure(_marketPendingFailure, _marketPendingPluginName, _marketPendingHint, render: false);

            _marketPendingHint = "";
            _marketPendingHintIsError = false;
            _marketPendingFailure = PluginFailureKind.Other;
            _marketPendingPluginName = "";

            // 评分弹窗只在市场页问：它靠点击市场行来交互，画在别的页签上既挡视线又没人点得中。
            // 失败弹窗相反 —— 必须不分页签当场弹，所以两者的「什么时候出现」不能共用一套判断。
            // 一键更新批量进行中也不问：连装 N 个弹 N 次「给个好评」等于骚扰，装完再说。
            // render: false —— 这里本身就在 Render() 里面，再叫一次会把整帧重画一遍叠在当前画布上。
            if (rateTarget != null && _selectedTab == 7 && !_updateAllRunning)
                OpenRateDialog(rateTarget, render: false);   // 装完自动问一句评分
        }

        private int IndexOfMarketView(MarketPlugin mp)
        {
            for (int i = 0; i < _marketView.Count; i++)
                if (ReferenceEquals(_marketView[i], mp)) return i;
            return -1;
        }

        private bool InstallDllPlugin(MarketPlugin mp, string tempFile)
        {
            var (ok, msg, failure) = PluginManager.Instance.Import(tempFile);   // 同 Id 旧版本由 Import 自动清掉
            _marketPendingHint = ok ? "" : msg;
            _marketPendingHintIsError = !ok;
            _marketPendingFailure = failure;
            _marketPendingPluginName = mp.Name;
            return ok;
        }

        private bool InstallZipPlugin(MarketPlugin mp, string zipPath)
        {
            _marketPendingPluginName = mp.Name;

            string extractDir = Path.Combine(Path.GetTempPath(), "NotchPeninsula", "market", "x_" + mp.Id);
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            var old = MatchLocalPlugin(mp);
            if (old != null) PluginManager.Instance.Remove(old);   // 内部含卸载 + 文件移入 _recycle

            string target = Path.Combine(PluginManager.Instance.PluginsRoot, mp.Id);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(target);
            foreach (var f in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
            {
                string dst = Path.Combine(target, Path.GetFileName(f));
                if (!string.Equals(f, dst, StringComparison.OrdinalIgnoreCase)) File.Copy(f, dst, true);
            }
            Directory.Delete(extractDir, true);

            PluginManager.Instance.Refresh();
            var fresh = PluginManager.Instance.Entries.FirstOrDefault(e =>
                string.Equals(e.RootDir, target, StringComparison.OrdinalIgnoreCase));
            if (fresh == null)
            {
                _marketPendingHint = $"安装 {mp.Name} 失败：包内未找到可加载的插件";
                _marketPendingHintIsError = true;
                _marketPendingFailure = PluginFailureKind.NotAPlugin;
                return false;
            }
            if (PluginManager.Instance.Load(fresh))
            {
                _marketPendingHint = "";   // 成功不提示（同上）
                _marketPendingHintIsError = false;
                _marketPendingFailure = PluginFailureKind.None;
                return true;
            }
            _marketPendingHint = $"已安装但加载失败：{fresh.Error}";
            _marketPendingHintIsError = true;
            _marketPendingFailure = fresh.Failure;
            return false;
        }

        // ---- 卸载确认弹窗 ----
        //    插件页的「卸载」和市场页的「卸载」都先过这里：
        //    第一级只问「确定卸载吗」；第二级仅在插件登记过数据目录时出现，问目录要不要一起删。
        //    没登记的插件看不到第二级 —— 老插件零改动，只是多一道确认。

        private void OpenUninstallDialog(PluginEntry e, bool fromMarket)
        {
            string? registered = PluginManager.Instance.GetUninstallDirectory(e);
            string dir = registered != null && IsSafeDeleteTarget(registered) ? registered : "";
            if (registered != null && dir.Length == 0)
                Logger.Warn($"[PluginCenter] {e.Key} 登记的卸载目录不可删，已跳过删除选项：{registered}");

            _uninstallEntry = e;
            _uninstallDir = dir;
            _uninstallFromMarket = fromMarket;
            _marketDialog = MarketDialog.UninstallConfirm;
            _marketDialogIndex = -1;
            _hoveredDialogClose = false;
            _hoveredDialogButton = -1;
            Render();
        }

        /// <summary>
        /// 确认类弹窗的按钮分派。点 ❌ / 点弹窗外都由这里先收掉（什么都不执行）。
        /// 点弹窗外<b>不关</b>：这类弹窗是「必须先看一眼」的结论，糊掉就等于把提示吃了 ——
        /// 要退出只能点弹窗上的按钮（❌ 或底部按钮）。
        /// </summary>
        private void HandleConfirmDialogClick()
        {
            var dlgRect = GetCurrentDialogRect();
            // 几何算不出来（弹窗状态坏了）就别拦：宁可放行，也不能把人困在一个看不见的模态里。
            if (dlgRect.Width <= 0f) { CloseMarketDialog(true); return; }

            bool inside = TryGetCursorClientPos(out int px, out int py) && dlgRect.Contains(px, py);
            if (!inside) return;

            // 关闭键、按钮、链接一律按落点重算：hover 由 WM_MOUSEMOVE 维护，
            // 拖放刚结束 / 光标从别处移进来时它还是旧值 —— 只信 hover 就是「点了没反应」。
            var closeRect = GetMarketDialogCloseRect(dlgRect);
            closeRect.Inflate(4f, 4f);
            _hoveredDialogClose = closeRect.Contains(px, py);
            if (_hoveredDialogClose) { CloseMarketDialog(true); return; }

            _hoveredDialogButton = HitDialogButton(dlgRect, px, py);
            if (_marketDialog == MarketDialog.LoadFailed)
            {
                _loadFailedLinkHovered = HitLoadFailedLink(dlgRect, px, py);
                Logger.Debug($"[弹窗] 链接判定 line={_loadFailedLinkLine} dx={_loadFailedLinkOffsetX:F0} "
                           + $"w={_loadFailedLinkWidth:F0} 点击=({px},{py}) 命中={_loadFailedLinkHovered}");
            }

            switch (_marketDialog)
            {
                case MarketDialog.LoadFailed:
                    if (_loadFailedLinkHovered) { OpenLoadFailedQqLink(); break; }   // 点链接不关弹窗
                    if (_hoveredDialogButton == 0) CloseMarketDialog(true);
                    break;

                case MarketDialog.UninstallConfirm:
                    if (_hoveredDialogButton != 0) break;      // 唯一按钮就是「卸载」
                    if (_uninstallDir.Length > 0)
                    {
                        _marketDialog = MarketDialog.UninstallData;
                        _hoveredDialogButton = -1;
                        Render();
                    }
                    else PerformUninstall(deleteDir: false);
                    break;

                case MarketDialog.UninstallData:
                    if (_hoveredDialogButton == 0) OpenUninstallDir();            // 打开目录：弹窗不关
                    else if (_hoveredDialogButton == 1) PerformUninstall(false);  // 保留目录
                    else if (_hoveredDialogButton == 2) PerformUninstall(true);   // 连目录一起删
                    break;
            }
        }

        private void OpenUninstallDir()
        {
            if (_uninstallDir.Length == 0) return;
            try
            {
                // 目录已经不在了就打开父级：比什么都不发生强。
                string target = Directory.Exists(_uninstallDir)
                    ? _uninstallDir
                    : Path.GetDirectoryName(_uninstallDir) ?? _uninstallDir;
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex) { Logger.Warn($"[PluginCenter] 打开插件目录失败：{_uninstallDir} — {ex.Message}"); }
        }

        private void PerformUninstall(bool deleteDir)
        {
            var e = _uninstallEntry;
            string dir = _uninstallDir;
            bool fromMarket = _uninstallFromMarket;

            CloseMarketDialog(render: false);
            if (e == null) { Render(); return; }

            // 先卸载再动目录：插件 Dispose、影子目录与文件句柄都放开了，目录才可能删得掉。
            PluginManager.Instance.Remove(e);

            if (deleteDir && dir.Length > 0)
            {
                if (TryDeletePluginDataDir(dir, out string error))
                {
                    Logger.Info($"[PluginCenter] 已删除插件目录：{dir}");
                }
                else
                {
                    Logger.Warn($"[PluginCenter] 删除插件目录失败：{dir} — {error}");
                    string msg = $"删除目录失败：{error}";
                    if (fromMarket) SetMarketHint(msg, true);
                    else { _pluginHint = msg; _pluginHintIsError = true; }
                }
            }

            ResetPluginHover();
            RefreshPluginView();
            if (_marketOnlyInstalled) RefreshMarketFilter();
            Render();
        }

        private static bool TryDeletePluginDataDir(string dir, out string error)
        {
            error = "";
            if (!IsSafeDeleteTarget(dir)) { error = "该目录不允许删除"; return false; }
            try { Directory.Delete(dir, true); return true; }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>
        /// 删除前的兜底校验：插件登记什么路径都有可能，不能让一条写错的注册把用户目录连锅端。
        /// 盘符根、宿主程序目录、系统 / 用户关键目录、以及这些目录的祖先一律拒绝。
        /// </summary>
        private static bool IsSafeDeleteTarget(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            try
            {
                string full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (full.Length <= 3 || !Directory.Exists(full)) return false;

                foreach (var special in new[]
                {
                    Environment.SpecialFolder.UserProfile,
                    Environment.SpecialFolder.ApplicationData,
                    Environment.SpecialFolder.LocalApplicationData,
                    Environment.SpecialFolder.DesktopDirectory,
                    Environment.SpecialFolder.MyDocuments,
                    Environment.SpecialFolder.Windows,
                    Environment.SpecialFolder.System,
                    Environment.SpecialFolder.ProgramFiles,
                    Environment.SpecialFolder.ProgramFilesX86,
                })
                {
                    string root = Environment.GetFolderPath(special);
                    if (root.Length == 0) continue;
                    if (IsSameOrAncestorOf(full, root)) return false;
                }

                return !IsSameOrAncestorOf(full, AppContext.BaseDirectory)
                    && !IsSameOrAncestorOf(full, PluginManager.Instance.PluginsRoot);
            }
            catch { return false; }
        }

        /// <summary>candidate 就是 root，或是它的祖先 —— 这两种情况删掉 candidate 等于把 root 一起带走。</summary>
        private static bool IsSameOrAncestorOf(string candidate, string root)
        {
            try
            {
                string c = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (c.Length == 0 || r.Length == 0) return false;
                if (string.Equals(c, r, StringComparison.OrdinalIgnoreCase)) return true;
                return r.StartsWith(c + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private void UninstallMarketPlugin(MarketPlugin mp)
        {
            var local = MatchLocalPlugin(mp);
            if (local == null) return;   // 理论上到不了（按钮置灰），兜一层
            OpenUninstallDialog(local, fromMarket: true);
        }

        // ---- 详情弹窗 ----

        private static List<string> WrapText(string text, SKPaint paint, float maxWidth)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;
            var cur = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                cur.Append(c);
                if (paint.MeasureText(cur.ToString()) > maxWidth && cur.Length > 1)
                {
                    lines.Add(cur.ToString(0, cur.Length - 1));
                    cur.Clear();
                    cur.Append(c);
                }
            }
            if (cur.Length > 0) lines.Add(cur.ToString());
            return lines;
        }
    }
}

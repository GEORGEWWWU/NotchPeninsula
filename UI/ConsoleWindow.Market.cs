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
        // 数据源：nps.georgewu.top 的市场 API；拉一次缓存到内存，进入页签时触发。
        // 行为约定（与用户确认的交互一致）：
        //   第一个按钮 = 下载（未安装）/ 更新（本地版本落后）/ 重装（本地已是最新）；
        //   第二个按钮 = 卸载（本地没有这个插件时置灰不可点）；
        //   开关位置 = 详情按钮，点击弹出小窗显示插件介绍。
        // 本地匹配判据：先按插件自报的 pluginId（INotchPlugin.Id），再按入口 DLL 文件名 ——
        //   市场条目的 files[0].name 就是插件发布时的 DLL/ZIP 主文件名，与 plugins 目录里的
        //   文件名一致（如 OneSaying.dll / FileStation.dll），比显示名可靠得多。

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

        // 分类筛选（与官网 market 的 categories 一致：all/theme/media/utility/dev）
        private string _marketCategoryKey = "all";
        private bool _marketCategoryOpen;                    // 分类下拉展开态
        private int _hoveredMarketCategoryIndex = -1;        // 菜单内悬停项

        // 搜索框（顶栏右侧）：点击聚焦，WM_CHAR 输入，ESC 退出，点别处失焦
        private string _marketSearch = "";
        private bool _marketSearchFocused;
        private bool _marketSearchHovered;

        // 搜索框的编辑模型（插入点 / 框选）——自绘框没有原生 EDIT，这两样得自己维护：
        //   _marketSearchCaret     = 插入点（0..串长）
        //   _marketSearchSelAnchor = 框选锚点；与插入点相同 = 没有选区，选区 = [min, max)
        //   _marketSearchDragging  = 左键在框里按住拖动中（松开由 WM_MOUSEMOVE 的 leftDown 判掉）
        private int _marketSearchCaret;
        private int _marketSearchSelAnchor;
        private bool _marketSearchDragging;

        private int MarketSelStart => Math.Min(_marketSearchCaret, _marketSearchSelAnchor);
        private int MarketSelEnd => Math.Max(_marketSearchCaret, _marketSearchSelAnchor);
        private bool MarketHasSelection => _marketSearchCaret != _marketSearchSelAnchor;

        /// <summary>搜索框里文字的起点（含左侧内边距）——渲染与命中必须同一个数。</summary>
        private const float MarketSearchTextX = MarketSearchX + 26f;
        /// <summary>文字可用宽度（右边留给内边距）——渲染截断与命中换算共用。</summary>
        private const float MarketSearchTextMax = MarketSearchW - 34f;

        // ── 搜索串的逐字符前缀宽度（缓存）──
        // MeasureText 是 O(串长)，而「插入点 ↔ x」的换算天然要对每个字符边界各量一次：
        //    逐字符扫一遍就是 O(n²)，拖选时每帧要跑四次（选区两端 + 光标 + 可视起点），
        //    往搜索框里粘一整段文字再拖选会把界面直接拖卡。
        // 这里按「串的引用变了才重建」缓存一份前缀和，之后渲染改差分、命中改二分，都是 O(log n)。
        //    string 不可变 ⇒ 引用没换就是内容没换（改串必然产生新对象），拿引用当键是安全的。
        private string _searchMetricsSrc = "";
        private float[] _searchPrefix = new float[1];

        /// <summary>取前缀宽度表（[i] = 前 i 个字符的总宽，长度 ≥ 串长 + 1）。串没换就直接复用。</summary>
        private float[] SearchPrefix()
        {
            if (ReferenceEquals(_searchMetricsSrc, _marketSearch)) return _searchPrefix;

            int n = _marketSearch.Length;
            if (_searchPrefix.Length < n + 1) _searchPrefix = new float[n + 1];
            _searchPrefix[0] = 0f;
            float acc = 0f;
            for (int i = 0; i < n; i++)
            {
                // 逐「UTF-16 单位」量（与原逐字符扫描同一套口径）；Span 重载不分配 ——
                //    2.88 没有 MeasureText(string, int, int)，只有 ReadOnlySpan<char>
                acc += _marketTextPaint.MeasureText(_marketSearch.AsSpan(i, 1));
                _searchPrefix[i + 1] = acc;
            }
            _searchMetricsSrc = _marketSearch;
            return _searchPrefix;
        }

        // 拖选期间冻结的可视窗口起点（-1 = 未冻结）。
        //   不冻结的话：窗口跟着插入点往右挪 → 同一个鼠标 x 命中到另一个字符 → 插入点往回跳 →
        //   窗口再挪回来，一帧之内自激来回，长串里拖着像卡住。冻结后「鼠标 ↔ 字符」全程一一对应。
        //   只在 _marketSearchDragging 为真时生效，所以松手（拖选结束）即自动恢复正常跟随，不必额外解冻。
        private int _marketSearchViewFrozen = -1;

        /// <summary>
        /// 可见窗口的起始下标。整串放得下就是 0；放不下时把左端右移到「插入点贴着右边界」，
        /// 打字时插入点始终可见（和原生编辑框一个体感）。渲染与命中都走这一个函数，窗口才不会错位。
        /// </summary>
        private int MarketSearchViewStart()
        {
            if (_marketSearchDragging && _marketSearchViewFrozen >= 0)
                return Math.Clamp(_marketSearchViewFrozen, 0, _marketSearch.Length);

            int caret = Math.Clamp(_marketSearchCaret, 0, _marketSearch.Length);
            float[] px = SearchPrefix();
            if (px[_marketSearch.Length] <= MarketSearchTextMax) return 0;

            // 二分找最小的 start 使 [start, caret) 塞得进框（px 单调递增，可二分）
            int lo = 0, hi = caret;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (px[caret] - px[mid] <= MarketSearchTextMax) hi = mid;
                else lo = mid + 1;
            }
            return lo;
        }

        /// <summary>插入点下标 → 客户区 x（DIP）。画光标 / 铺选区底色、以及命中选择起止都用它。</summary>
        private float MarketSearchXAtIndex(int index)
        {
            float[] px = SearchPrefix();
            int start = MarketSearchViewStart();
            if (index <= start) return MarketSearchTextX;
            index = Math.Clamp(index, start, _marketSearch.Length);
            // 右端必须钳在框内：串尾可能落在可视窗口之外（全选时插入点在串尾、
            //    而窗口起点又被推到很靠右），不钳的话高亮与光标会直接画到搜索框外面去。
            return MathF.Min(MarketSearchTextX + (px[index] - px[start]), MarketSearchTextX + MarketSearchTextMax);
        }

        /// <summary>客户区 x（DIP）→ 插入点下标。取「最近的字符边界」，与原生框手感一致。</summary>
        private int MarketSearchIndexAtX(float x)
        {
            float[] px = SearchPrefix();
            int start = MarketSearchViewStart();
            int len = _marketSearch.Length;
            // 拖到可见文字右侧之外：直接给串尾。原生框也是这个手感。
            if (x >= MarketSearchTextX + (px[len] - px[start])) return len;

            // 二分找「第一个宽度 ≥ 目标」的字符边界，再跟它左边那个比谁更近 —— 与原先逐字符扫描同样的取舍。
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

        /// <summary>删掉选区（没有选区则返回 false），插入点落到选区左端。</summary>
        private bool MarketDeleteSelection()
        {
            if (!MarketHasSelection) return false;
            int s = MarketSelStart, e = MarketSelEnd;
            _marketSearch = _marketSearch[..s] + _marketSearch[e..];
            _marketSearchCaret = _marketSearchSelAnchor = s;
            return true;
        }

        /// <summary>在插入点插入文本（先删选区），插入完光标落在新内容末尾。</summary>
        private void MarketInsertText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            MarketDeleteSelection();
            int at = Math.Clamp(_marketSearchCaret, 0, _marketSearch.Length);
            _marketSearch = _marketSearch[..at] + text + _marketSearch[at..];
            _marketSearchCaret = _marketSearchSelAnchor = at + text.Length;
        }

        /// <summary>插入点 / 选区一起挪到末尾（外部改串、聚焦收尾统一走这里）。</summary>
        private void MarketCaretToEnd()
        {
            _marketSearchCaret = _marketSearchSelAnchor = _marketSearch.Length;
            _marketSearchDragging = false;
        }

        /// <summary>「只看已安装」复选框（勾选后列表只留本机已装的插件；与搜索、分类叠加过滤）。</summary>
        private bool _marketOnlyInstalled;
        private bool _hoveredMarketChk;
        private bool _hoveredMarketRefresh;   // 第二行「刷新」按钮悬停

        /// <summary>数据刚拉到，需要在 UI 线程重建过滤视图（后台线程只置标志）。</summary>
        private bool _marketDataDirty;

        private bool _marketFetching;
        private bool _marketTriedFetch;                  // 只自动拉一次；失败后切回页签时重试
        private string _marketError = "";                // 非空 = 拉取失败（市场卡状态行红字提示）
        private string _marketBusyId = "";               // 正在下载/安装的市场插件 id（该行按钮置灰）
        private string _marketHint = "";                 // 状态行右侧红字：只用于安装 / 卸载失败，成功不提示
        private bool _marketHintIsError;
        private int _marketScroll;                       // 市场列表滚动首行（绝对条目下标）

        // 弹窗（同一套外观：左上标题 + 右上关闭按钮 + 可选正文/按钮行）
        //   详情 / 评分 / 卸载确认 / 本地插件卸载确认 / 加载失败提示五种弹窗互斥，同一时刻最多一个。
        //   ConfirmRemoveLocal 是「我的插件」列表的卸载确认：目标不是市场条目而是本地
        //   PluginEntry（存 _dialogRemoveEntry），矩形与交互复用市场确认弹窗同一套模板。
        //   LoadFailed 是纯告知（导入 / 安装失败时给「确认宿主是最新版 + QQ 群下载」的引导），
        //   只有一颗「好的」，两个页签都要能画出来。
        private enum MarketDialog { None, Detail, Rate, ConfirmUninstall, ConfirmRemoveLocal, LoadFailed }

        private MarketDialog _marketDialog = MarketDialog.None;
        private int _marketDialogIndex = -1;             // 弹窗对应的市场条目下标
        private PluginEntry? _dialogRemoveEntry;         // ConfirmRemoveLocal 弹窗对应的本地插件
        private bool _hoveredDialogClose;                // 弹窗右上角 ❌ 是否悬停
        private int _hoveredDialogButton = -1;           // 弹窗内按钮：0 = 主按钮，1 = 取消

        // 评分弹窗状态（进入时向服务端要一次「我评过没有」）
        private double _rateStars;                       // 0..5，鼠标预览中的分值（半星为 .5）
        private double _rateMine;                        // 服务端记录的「我的评分」，0 = 没评过
        private double _rateAverage;                     // 服务端返回的平均分
        private int _rateCount;
        private bool _rateLoading;                       // 正在读状态 / 正在提交
        private string _rateStatus = "";                 // 弹窗内状态文案（成功绿 / 失败红）
        private bool _rateStatusIsError;

        // 市场行内按钮悬停（绝对条目下标；与 _hoveredPluginXxx 同一套约定）
        private int _hoveredMarketInstall = -1;
        private int _hoveredMarketUninstall = -1;
        private int _hoveredMarketDetail = -1;

        /// <summary>关闭所有市场弹窗（切页签 / 弹窗外点击 /关闭按钮 都走这里）。</summary>
        private void CloseMarketDialog(bool render = false)
        {
            if (_marketDialog == MarketDialog.None && _marketDialogIndex == -1) return;
            _marketDialog = MarketDialog.None;
            _marketDialogIndex = -1;
            _dialogRemoveEntry = null;
            _hoveredDialogClose = false;
            _hoveredDialogButton = -1;
            _rateStatus = "";
            if (render) Render();
        }

        /// <summary>
        /// 插件加载失败提示（导入 DLL / 市场安装失败都走这里）。
        ///
        /// 为什么是固定文案而不是把底层异常原样甩出来：绝大多数失败都是「插件是按新版宿主 API
        /// 编译的，宿主还旧」这一种，用户能做的只有升级宿主；把 TypeLoadException 之类的栈给用户
        /// 看没有意义。具体原因照样进 app.log 与状态行红字，排查不受影响。
        ///
        /// render:false 供「渲染过程中发现失败」的调用点使用（ApplyPendingMarketResult），
        /// 避免在 Render 里再套一层 Render。
        /// </summary>
        private void ShowPluginLoadFailedDialog(bool render = true)
        {
            _marketDialog = MarketDialog.LoadFailed;
            // 市场那条路进来时没有市场条目：下标复位，渲染 / 命中靠 GetCurrentDialogRect
            //    单独回答 LoadFailed 的矩形（见那里的注释）。
            _marketDialogIndex = -1;
            _dialogRemoveEntry = null;
            _hoveredDialogClose = false;
            _hoveredDialogButton = -1;
            if (render) Render();
        }

        // ---- 布局真源 ----
        // 「我的插件」（tab 6）与「插件市场」（tab 7）各一张整页高卡片，行布局同一套：
        //   我的插件  顶卡 TITLE_BAR_HEIGHT+12..+108，列表卡 listY = TITLE_BAR_HEIGHT+122，行起点 +44
        //   插件市场  第一行（分类下拉 + 搜索框）      TITLE_BAR_HEIGHT+22..+48
        //             第二行（只看已安装 + 计数｜刷新）TITLE_BAR_HEIGHT+54..+80
        //             列表行起点 +92
        // 渲染 / 命中 / 滚轮三处共用，改一处必须同步。
        // 两行是**同款 26 高控件行**、行距 6px；行内所有元素的垂直位置统一按
        //    「行顶 + 17」= 文字基线、「行顶 + 5」= 16px 方框顶 —— 这是「每行每个东西上下对齐」的落点；
        //    字号一律 13px（_marketTextPaint）= 「字体一样大」。改行高必须两行一起改。
        // 行高 50：一行为「名称 + 信息/按钮」两段，46px 内容 + 4px 呼吸。
        //    56 时市场卡底部会剩十几像素、我的插件卡剩近 50px（放不下整行却也不显示）——
        //    收到 50 后两张卡各多显示一行。
        private const float PluginListRowH = 50f;

        private void GetPluginListCardTop(out float listY)
            => listY = TITLE_BAR_HEIGHT + 122f;

        // ── 第一行：分类下拉（左）+ 搜索框 ──
        private const float MarketControlsY = TITLE_BAR_HEIGHT + 22f;   // 第一行顶（54，卡顶下留 10）
        private const float MarketControlH = 26f;                       // 控件行高（两行共用）
        private const float MarketCatBtnW = 132f;                       // 分类按钮宽（左起 CONTENT_TEXT_X）
        // 搜索框：紧接分类按钮右侧 16px，右端一直铺到第二行「刷新」按钮的右边界基准线
        //    （WIDTH - MarketRightPad）—— 第一行右侧不留空，两行左右两端严格对齐。
        private const float MarketSearchX = CONTENT_TEXT_X + MarketCatBtnW + 16f;       // 350
        private const float MarketSearchW = WIDTH - MarketRightPad - MarketSearchX;     // 214

        // ── 第二行：只看已安装 + 计数（左）｜刷新（右）──
        //    与第一行同高、间距 10px（比 6 松一点，两行贴太近像挤在一起）。
        //    文字基线 = 行顶 + 17（与第一行同一套），16px 方框顶 = 行顶 + 5。
        private const float MarketStatusRowY = TITLE_BAR_HEIGHT + 58f;  // 第二行顶（90）
        private const float MarketStatusBaseline = MarketStatusRowY + 17f;
        // 刷新按钮：第二行最右，右边界与列表行按钮组同基准线（点击重新拉取市场数据，加载失败后的重试入口）
        private const float MarketRefreshW = 50f;
        private const float MarketRefreshX = WIDTH - MarketRightPad - MarketRefreshW;   // 514
        // 列表行起点：第二行底（+84）再留 8px。比原来的 +98 上移了，可见行数仍是满 10 行
        //    （GetMarketListLayout 的算式随本常量走）。
        private const float MarketRowsTop = TITLE_BAR_HEIGHT + 92f;

        // 「只看已安装」复选框：第二行左端（第一行放分类 + 搜索框，第二行放复选框 + 计数 + 刷新）。
        //    勾上后列表只留本机已装的插件（与搜索、分类叠加）。
        //    第二行左侧 = 复选框 + 标签，中间 = 计数 / 加载状态，右端 = 刷新按钮。
        private const float MarketChkX = CONTENT_TEXT_X;
        private const float MarketChkBoxSize = 16f;
        private const string MarketChkLabel = "只看已安装";
        private const float MarketChkLabelGap = 7f;
        private static float MarketChkLabelX => MarketChkX + MarketChkBoxSize + MarketChkLabelGap;
        /// <summary>复选框方框的 y：16px 方框在 26 高行里垂直居中（行顶 + 5），与同行文字同一中线。</summary>
        private const float MarketChkY = MarketStatusRowY + 5f;

        // 市场行三个按钮：各自独立（互不相连）、统一 50 宽、右对齐、间隔 6px。
        //    三颗一样大、文字各自居中绘制（见 Render.cs，测量与绘制必须同一个画笔，
        //    否则 13px 画、12px 量会让文字整体偏左）。
        //    配色：主操作按状态三色（下载绿 / 更新橙 / 重装灰蓝）；卸载灰底、悬停转红；详情空心。
        private const float MarketBtnW = 50f;
        private const float MarketRightPad = CONTENT_TEXT_RM + 8f;                      // 市场行右侧基准（36）
        private const float MarketBtn3X = WIDTH - MarketRightPad - MarketBtnW;          // 514 详情
        private const float MarketBtn2X = MarketBtn3X - 6f - MarketBtnW;                // 458 卸载
        private const float MarketBtn1X = MarketBtn2X - 6f - MarketBtnW;                // 402 下载/更新/重装

        /// <summary>市场分类（key 对应 API 的 category 字段），顺序即菜单顺序。</summary>
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

        /// <summary>
        /// 重建过滤视图：分类 + 搜索（名称 / 作者 / id / 标签，忽略大小写）。
        /// 过滤结果变了，原下标全部失效 —— 滚动、悬停、详情弹窗一起复位。
        /// </summary>
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
            _marketScroll = 0;
            CloseMarketDialog();   // 过滤结果变了，原下标全部失效 —— 弹窗一起收掉
            ResetPluginHover();
        }

        private void GetMarketListLayout(out int visibleRows, out int maxFirstRow)
        {
            int maxRows = Math.Max(1, (int)((HEIGHT - 20 - MarketRowsTop - 8) / PluginListRowH));
            int total = _marketView.Count;
            visibleRows = Math.Min(total, maxRows);
            maxFirstRow = Math.Max(0, total - visibleRows);
        }

        /// <summary>进入页签时触发一次市场拉取；失败后再进页签会重试。</summary>
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
                    // 服务端文案里的零宽字符（如描述里夹的 U+200B）在这里就摘掉：
                    //   它是给网页排版用的，进了自绘画笔就是一个豆腐块（见 StripInvisible）。
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

                // 主文件：取 files[0]（当前市场每个插件只发一个主文件包）
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

        /// <summary>
        /// 市场条目在本地是否已安装。
        ///
        /// 判据由强到弱五级 —— 单靠「文件名相等」是不够的（用户实测：装了 8 个只认出 4 个）：
        ///   ① 插件自报的 pluginId 与市场 id 相同；
        ///   ② 归一化后相等：去掉 - _ 空格并转小写再比。市场 slug 常写成 nps-media-mixer，
        ///      而插件声明的 Id 是 NpsMediaMixer —— 原样比永远不相等，归一化后是同一个词；
        ///   ③ 显示名相同：市场条目名与插件自报名出自同一作者，通常逐字一致（最稳的一档）；
        ///   ④ 目录名 / Key 首段 == 市场 id（zip 包装出来的 plugins/&lt;id&gt;/ 走这条）；
        ///   ⑤ 归一化后互相包含（较短者 ≥ 4 字符，避免 nps 之类短词误判）——
        ///      兜住「本地文件名带时间戳后缀」「市场包名比插件名多几个词」这类情况。
        ///
        /// 归一化 + 包含这两层是必须的：本地文件名可能是 NpsMediaMixer_20261001133637.dll，
        /// 而市场发布的是 NpsMediaMixer.dll，拿全名或主干比都匹配不上。
        /// </summary>
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

            // ② 归一化相等（Id / 目录名 / Key 首段 / DLL 主干 任一命中即可）
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
            // 注意：本方法在渲染路径上（每帧每行都调），所以这里刻意不分配临时数组/集合。
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

        /// <summary>归一化后的「互相包含」判定：较短者必须 ≥ 4 字符，避免短词乱命中。</summary>
        private static bool ContainsEither(string a, string b)
        {
            const int MinLen = 4;
            if (a.Length < MinLen || b.Length < MinLen) return false;
            return a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
        }

        /// <summary>一个本地插件可用于身份比对的若干串（Id / Key 首段 / DLL 文件名主干 / 显示名）。</summary>
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

        /// <summary>
        /// 身份串归一化：去掉 - _ 空格与点，统一小写。
        /// 目的就是让 nps-media-mixer / NpsMediaMixer / Nps_Media_Mixer 归到同一个词上。
        /// </summary>
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

        // ---- 弹窗几何（三种弹窗共用同一套外观与命中口径）----
        // 版式：标题行（左上标题 + 右上关闭按钮）→ 可选正文 → 可选按钮行。
        // 高度一律按「内容行数」现算，避免渲染与命中各算一份。

        private const float DialogW = 380f;
        private const float DialogPad = 16f;
        private const float DialogTitleH = 34f;      // 标题行高（❌ 也在这一行）
        private const float DialogLineH = 18f;
        private const float DialogBtnH = 26f;
        private const float DialogBtnW = 84f;

        private static float DialogInnerW => DialogW - DialogPad * 2;

        /// <summary>弹窗矩形。bodyLines = 正文行数（0 表示无正文），buttons = 是否有按钮行。</summary>
        private static SKRect GetMarketDialogRect(float bodyLines, bool buttons)
        {
            float ph = DialogTitleH + 8f;
            if (bodyLines > 0f) ph += bodyLines * DialogLineH + 8f;
            if (buttons) ph += DialogBtnH + 18f;
            ph += 6f;
            float px = (WIDTH - DialogW) / 2f;
            float py = Math.Max(TITLE_BAR_HEIGHT + 24f, (HEIGHT - ph) / 2f);
            return new SKRect(px, py, px + DialogW, py + ph);
        }

        /// <summary>右上角关闭按钮 的矩形（渲染与命中同源）。</summary>
        private static SKRect GetMarketDialogCloseRect(SKRect popup)
            => new SKRect(popup.Right - DialogPad - 18f, popup.Top + 8f, popup.Right - DialogPad, popup.Top + 8f + 18f);

        /// <summary>弹窗内按钮矩形。index：0 = 主按钮（右），1 = 取消（主按钮左侧）。</summary>
        private static SKRect GetMarketDialogButtonRect(SKRect popup, int index)
        {
            float y = popup.Bottom - 6f - DialogBtnH - 12f;
            float x0 = index == 0 ? popup.Right - DialogPad - DialogBtnW : popup.Right - DialogPad - DialogBtnW * 2 - 8f;
            return new SKRect(x0, y, x0 + DialogBtnW, y + DialogBtnH);
        }

        /// <summary>评分弹窗里五颗星的行矩形（与 DrawDialogRate 的排版共用同一套偏移）。</summary>
        private static SKRect GetRateStarsRect(SKRect popup)
        {
            // 与 DrawDialogRate 一致：正文起点 = 标题行 + 8，说明行占 DialogLineH，星行再下移 6
            float y = popup.Top + DialogTitleH + 8f + DialogLineH + 6f;
            float w = Math.Min(260f, DialogInnerW);
            return new SKRect(popup.Left + DialogPad, y, popup.Left + DialogPad + w, y + 26f);
        }

        /// <summary>详情弹窗（正文 = 插件介绍）的矩形。</summary>
        private SKRect GetMarketDetailRect(MarketPlugin mp)
        {
            var descLines = WrapText(mp.Desc, _subTextPaint, DialogInnerW);
            const int MaxDescLines = 7;
            int lineCount = Math.Min(descLines.Count, MaxDescLines);
            // 元数据行(1) + 分隔线行(1) + 介绍行 + 可选标签行
            float lines = 2f + lineCount + (mp.Tags.Length > 0 ? 1f : 0f);
            return GetMarketDialogRect(lines, false);
        }

        /// <summary>
        /// 评分弹窗矩形。按 4 行正文预留高度：说明行 + 星行（26px）+ 状态行 + 余量 ——
        /// 星行比普通文字行高，所以不能按「3 行文字」算，否则状态行会被挤到弹窗外面。
        /// </summary>
        private static SKRect GetRateDialogRect() => GetMarketDialogRect(4f, false);

        /// <summary>卸载确认弹窗矩形：标题 + 两行正文 + 按钮行。（加载失败提示同款高度）</summary>
        private static SKRect GetConfirmDialogRect() => GetMarketDialogRect(2f, true);

        /// <summary>
        /// 单按钮告知弹窗里那颗按钮的矩形：水平居中。
        /// 与 GetMarketDialogButtonRect 的「右对齐主按钮 + 左取消」不同 —— 没有可取消的动作时，
        /// 居中的单按钮才是用户期待的位置（Windows 消息框也是居中）。
        /// </summary>
        private static SKRect GetDialogSingleButtonRect(SKRect popup)
        {
            float y = popup.Bottom - 6f - DialogBtnH - 12f;
            float x = popup.MidX - DialogBtnW / 2f;
            return new SKRect(x, y, x + DialogBtnW, y + DialogBtnH);
        }

        /// <summary>当前打开着的弹窗矩形（没有弹窗返回空）。渲染 / 命中 / 点击三处共用。</summary>
        private SKRect GetCurrentDialogRect()
        {
            // 这两种弹窗都不依赖市场条目：本地卸载确认（目标在 _dialogRemoveEntry 里）与加载失败提示
            if (_marketDialog == MarketDialog.ConfirmRemoveLocal || _marketDialog == MarketDialog.LoadFailed)
                return GetConfirmDialogRect();
            var mp = GetMarketAt(_marketDialogIndex);
            if (mp == null) return SKRect.Empty;
            return _marketDialog switch
            {
                MarketDialog.Detail => GetMarketDetailRect(mp),
                MarketDialog.Rate => GetRateDialogRect(),
                MarketDialog.ConfirmUninstall => GetConfirmDialogRect(),
                _ => SKRect.Empty,
            };
        }

        // ---- 评分（nps.georgewu.top 的 rating API）----
        //   GET  ?action=rating&id=<slug>   → {ok, rating, count, mine}
        //   POST ?action=rate   form: id=&rating=   → {ok, rating, count, mine} / {ok:false, error:"already_rated"}
        // 「我评过没有」由服务端按访客 cookie 判定，所以这里的 HttpClient 必须带 CookieContainer 保持会话。
        // 评分粒度：半星（0.5 ~ 5.0）。

        private static readonly HttpClient _rateHttp = new(new HttpClientHandler
        {
            CookieContainer = new System.Net.CookieContainer(),
            UseCookies = true,
        })
        { Timeout = TimeSpan.FromSeconds(12) };

        /// <summary>打开评分弹窗：先复位本地状态，再向服务端要一次「我评过没有」。</summary>
        private void OpenRateDialog(MarketPlugin mp)
        {
            _marketDialog = MarketDialog.Rate;
            // 下标必须一起设：弹窗几何 / 命中 / 渲染都靠它反查 MarketPlugin（GetCurrentDialogRect）。
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
            Render();

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

        /// <summary>提交评分（半星粒度）。成功后把新平均分同步回列表与当前弹窗。</summary>
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
                        // already_rated 也带回了 mine（服务端记录的「您打过的分」），
                        //    所以这里能把真实分数报出来 —— 比干巴巴一句「您已经评过分」有用得多。
                        _rateStatus = err switch
                        {
                            "already_rated" => _rateMine > 0
                                ? $"您已经给此插件打了 {FormatScore(_rateMine)} 分，感谢您的参与"
                                : "您已经给此插件打过分了，感谢您的参与",
                            "too_many" => "提交太频繁了，请过一会儿再试。",
                            _ => "评分提交失败，请稍后再试。",
                        };
                        // already_rated 是「正常告知」而非失败：用常规灰而不是报错红，
                        //    并且把星级锁定到已评的分数上（_rateMine > 0 后星星不再跟随悬停）
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

        /// <summary>版本号比较（v 前缀容错；解析失败退回字符串序）。</summary>
        private static int CompareVersions(string a, string b)
        {
            bool oa = Version.TryParse(a.TrimStart('v', 'V'), out var va);
            bool ob = Version.TryParse(b.TrimStart('v', 'V'), out var vb);
            if (oa && ob) return va!.CompareTo(vb);
            return string.CompareOrdinal(a, b);
        }

        /// <summary>
        /// 第一个按钮的文案：未安装 → 下载；本地落后 → 更新；其余 → 重装。
        /// 本地版本读不到时（插件被禁用后重启，不会加载）一律走「重装」——
        /// 无从比较版本，说「更新」是假信息。
        /// </summary>
        private static string MarketInstallLabel(MarketPlugin mp, PluginEntry? local)
        {
            if (local == null) return "下载";
            if (local.Version.Length == 0) return "重装";
            return CompareVersions(mp.Version, local.Version) > 0 ? "更新" : "重装";
        }

        /// <summary>后台线程完成 / 状态变化后请 UI 线程重绘（与显示器枚举的回调同一通道）。</summary>
        private void PostAsyncRerender()
        {
            var inst = _instance;
            if (inst != null && inst._hwnd != IntPtr.Zero)
                Win32.PostMessage(inst._hwnd, WM_ASYNC_RERENDER, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>光标是否落在搜索框内（点击聚焦判据，与渲染坐标同源）。</summary>
        private bool IsInMarketSearchBox()
        {
            if (!TryGetCursorClientPos(out int x, out int y)) return false;
            return x >= MarketSearchX && x <= MarketSearchX + MarketSearchW
                && y >= MarketControlsY && y <= MarketControlsY + MarketControlH;
        }

        /// <summary>
        /// 搜索框键盘输入：可见字符在插入点插入、退格 / Delete 删除（先吃选区）、
        /// ← → Home End 移光标、按住 Shift 移光标 = 框选、Ctrl+A 全选，ESC / 回车取消聚焦。
        /// 只有市场页且搜索框聚焦时才吃掉按键（见 WndProc 的 WM_CHAR / WM_KEYDOWN）。
        /// </summary>
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

            // 移动光标：不按 Shift 就顺手把锚点带走（= 取消选区），按了 Shift 锚点不动（= 拉选区）
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
                // 先吃选区；没选区才删插入点左边那一个（光标可能在串中间，不是删串尾）
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
        // 自绘搜索框没有原生编辑框，中文输入必须自己接 IMM32 的三条消息：
        //   STARTCOMPOSITION / COMPOSITION / ENDCOMPOSITION。
        // 关键一条：WM_IME_COMPOSITION 带 GCS_RESULTSTR 时把「已上屏」的串取回来追加，
        //   否则用户打完中文按空格选词后，字符串只进了 IME，搜索框里什么都没有。
        // GCS_COMPSTR 时把「正在组字」的串存下来，画在搜索框里当预览（灰字 + 下划线），
        //   不然用户看不到自己正在拼什么。组字串只在搜索框里画一份 —— IME 自带的组字窗
        //   在 WndProc 的 WM_IME_SETCONTEXT 里被关掉了（否则同一个拼音会画两遍），
        //   候选窗仍钉在搜索框下方（选词靠它）。

        /// <summary>正在组字（未上屏）的串，仅用于显示预览。</summary>
        private string _marketImeComposing = "";

        /// <summary>处理 IME 消息。返回 true 表示本消息已被消费。仅市场页 + 搜索框聚焦时生效。</summary>
        private bool HandleMarketIme(int msg, IntPtr lParam)
        {
            if (_selectedTab != 7 || !_marketSearchFocused || _hwnd == IntPtr.Zero) return false;

            switch (msg)
            {
                case Win32.WM_IME_STARTCOMPOSITION:
                    _marketImeComposing = "";
                    PositionImeWindows();
                    // 组字开始：把消息交给默认处理，IME 才能正常画出组字串
                    return false;

                case Win32.WM_IME_COMPOSITION:
                {
                    int flags = Win32.Low32(lParam);
                    if ((flags & Win32.GCS_RESULTSTR) != 0)
                    {
                        string result = ReadImeString(Win32.GCS_RESULTSTR);
                        if (result.Length > 0)
                        {
                            // 插到插入点（有选区先替换掉）—— 不是无脑往串尾拼，与打字同一套
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

        /// <summary>取 IME 的组字串 / 结果串（UTF-16）。</summary>
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

        /// <summary>把 IME 的候选窗钉到搜索框左下角，并顺手把组字窗的「锚点」也指过去
        /// （组字窗本身已关，但 IME 靠它知道插入点在哪儿，不设的话某些输入法会把光标相关操作算到窗口左上角）。</summary>
        private void PositionImeWindows()
        {
            IntPtr himc = Win32.ImmGetContext(_hwnd);
            if (himc == IntPtr.Zero) return;
            try
            {
                int px = (int)(MarketSearchX * _dpiScale);
                int py = (int)((MarketControlsY + MarketControlH) * _dpiScale);
                var pt = new Win32.POINT(px, py);

                var cf = new Win32.COMPOSITIONFORM { dwStyle = Win32.CFS_POINT, ptCurrentPos = pt };
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
        // 安装 / 卸载 / 评分的结果提示（绿字红字）不该常驻：4 秒后自动清掉。
        // 用窗口定时器（回调天然在 UI 线程），跑完 KillTimer，不留空转。

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

        /// <summary>WM_TIMER 回调：到期就清提示并停表。返回 true 表示本次定时器属于本模块。</summary>
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

        /// <summary>
        /// 下载并安装 / 更新 / 重装一个市场插件。整个下载在后台线程跑，期间该行按钮置灰；
        /// 完成后（无论成败）经 WM_ASYNC_RERENDER 回 UI 线程刷新。
        /// 成功后自动弹出评分弹窗（用户要求「下载完给个评分」）。
        /// </summary>
        private void StartMarketInstall(MarketPlugin mp)
        {
            if (_marketBusyId.Length > 0) return;   // 同一时刻只允许一个下载任务
            _marketBusyId = mp.Id;
            // 状态行不再写「正在下载…」：进度已经由该行的「正在下载安装…」表达（见 Render）。
            // 上一轮遗留的提示交给它自己的自动消失计时器收尾，这里不手动停表 ——
            // 停了表但不清文案，那条旧提示就会永远挂在状态行上。
            PostAsyncRerender();

            Task.Run(async () =>
            {
                bool ok = false;
                try
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "NotchPeninsula", "market");
                    // 临时文件的文件名必须是市场发布的原名（mp.FileName）：
                    //    Import() 是按「传入路径的文件名」把 dll 复制进 plugins 的，
                    //    早先这里拼成 mp.Id + "_" + mp.FileName，结果插件在 plugins 里被存成
                    //    「onesaying_OneSaying.dll」这种带市场 id 前缀的乱名（本地已实测到），
                    //    既难看又会让后续的身份匹配、同 Id 去重都变复杂。
                    //    用 id 建子目录来隔离（而不是改文件名），既避免重名又保住原名。
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

                    ok = mp.IsZip ? InstallZipPlugin(mp, tempFile) : InstallDllPlugin(tempFile);
                }
                catch (Exception ex)
                {
                    Logger.Error("[Market] 安装插件失败", ex);
                    _marketPendingHint = $"安装 {mp.Name} 失败：{ex.Message}";
                    _marketPendingHintIsError = true;
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

        // 后台线程不能直接碰 UI 状态（提示文案 / 弹窗），只把结果放到这三个字段，
        // 由 UI 线程在 RenderTabMarket 里取用 —— 与 _marketDataDirty 同一套约定。
        private string _marketPendingHint = "";
        private bool _marketPendingHintIsError;
        private bool _marketPendingHintDone;
        private int _marketPendingRateIndex = -1;

        /// <summary>
        /// 在 UI 线程消费后台安装结果：刷新两处列表 + 失败时写红字提示 + 按需弹出评分弹窗。
        /// 刷新是必须的 —— 装完后「我的插件」多了一行、市场那行的按钮也要从「下载」变「重装」，
        /// 所以这里无条件 RefreshPluginView()（它内部按变更序号缓存，没变时是空操作）。
        /// </summary>
        private void ApplyPendingMarketResult()
        {
            if (!_marketPendingHintDone) return;
            _marketPendingHintDone = false;

            // 评分目标是用「旧视图下标」记下来的，而下面可能重建过滤视图让下标整体错位，
            // 所以先把条目引用摘出来，重建之后再交给 OpenRateDialog（它自己会按引用重查下标）。
            int rateIdx = _marketPendingRateIndex;
            _marketPendingRateIndex = -1;
            var rateTarget = rateIdx >= 0 && rateIdx < _marketView.Count ? _marketView[rateIdx] : null;

            RefreshPluginView();          // 已安装列表 + 市场行按钮文案都靠它重算
            // 勾着「只看已安装」时过滤视图是固定住的：刚装上的插件要立刻出现在列表里
            // （卸载一侧同理，见 UninstallMarketPlugin）
            if (_marketOnlyInstalled) RefreshMarketFilter();

            // 成功不提示 —— 列表本身已经说明结果了；只有失败给红字 + 引导弹窗。
            if (_marketPendingHintIsError && _marketPendingHint.Length > 0)
            {
                SetMarketHint(_marketPendingHint, true);
                // 这里正在 Render 里（RenderTabMarket → 本方法），所以不能再套一层 Render
                ShowPluginLoadFailedDialog(render: false);
            }
            _marketPendingHint = "";
            _marketPendingHintIsError = false;

            if (rateTarget != null)
                OpenRateDialog(rateTarget);   // 装完自动问一句评分
        }

        private int IndexOfMarketView(MarketPlugin mp)
        {
            for (int i = 0; i < _marketView.Count; i++)
                if (ReferenceEquals(_marketView[i], mp)) return i;
            return -1;
        }

        private bool InstallDllPlugin(string tempFile)
        {
            var (ok, msg) = PluginManager.Instance.Import(tempFile);   // 同 Id 旧版本由 Import 自动清掉
            // 成功不提示：装没装上，看列表那行的主按钮从「下载」变成「重装」就知道；只有失败给红字
            _marketPendingHint = ok ? "" : msg;
            _marketPendingHintIsError = !ok;
            return ok;
        }

        /// <summary>
        /// zip 包安装：解压 → 移走本地旧版本（同 Id 不能共存）→ 落成 plugins/&lt;id&gt;/ 目录型布局 →
        /// Refresh 后按 RootDir 找到新条目加载。目录型布局能带上 zip 里的依赖文件。
        /// 在后台线程跑，所以结果只写 _marketPendingHint*，由 UI 线程消费（见 ApplyPendingMarketResult）。
        /// </summary>
        private bool InstallZipPlugin(MarketPlugin mp, string zipPath)
        {
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
                // 全部摊平到目标目录：zip 里可能有一层同名子目录（打包工具的习惯），
                // 保持原相对层级会在 plugins/<id>/<id>/ 下再套一层，PickEntryDll 就找不到了。
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
                return false;
            }
            if (PluginManager.Instance.Load(fresh))
            {
                _marketPendingHint = "";   // 成功不提示（同上）
                _marketPendingHintIsError = false;
                return true;
            }
            _marketPendingHint = $"已安装但加载失败：{fresh.Error}";
            _marketPendingHintIsError = true;
            return false;
        }

        /// <summary>卸载一个市场插件对应的本地版本（默认路径；UI 上必经确认弹窗，见 Click.cs）。</summary>
        private void UninstallMarketPlugin(MarketPlugin mp)
        {
            var local = MatchLocalPlugin(mp);
            if (local == null) return;   // 理论上到不了（按钮置灰），兜一层
            PluginManager.Instance.Remove(local);
            ResetPluginHover();
            RefreshPluginView();
            // 勾着「只看已安装」时，刚卸载掉的那条必须立刻从列表消失 —— 过滤视图是按本地安装状态
            // 算好就固定住的，不重建的话这一行会留到下次筛选变化为止（行内明细本身每帧现算，
            // 所以没勾选时不必重建，也就不会把列表滚动位置顶回顶部）。
            if (_marketOnlyInstalled) RefreshMarketFilter();
            Render();
        }

        // ---- 详情弹窗 ----

        /// <summary>把长文案按像素宽度折行（CJK 逐字断行足够用，介绍文本以中文为主）。</summary>
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

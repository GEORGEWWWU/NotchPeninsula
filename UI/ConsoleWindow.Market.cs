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

        /// <summary>「只看已安装」复选框（勾选后列表只留本机已装的插件；与搜索、分类叠加过滤）。</summary>
        private bool _marketOnlyInstalled;
        private bool _hoveredMarketChk;

        /// <summary>数据刚拉到，需要在 UI 线程重建过滤视图（后台线程只置标志）。</summary>
        private bool _marketDataDirty;

        private bool _marketFetching;
        private bool _marketTriedFetch;                  // 只自动拉一次；失败后切回页签时重试
        private string _marketError = "";                // 非空 = 拉取失败（市场卡状态行红字提示）
        private string _marketBusyId = "";               // 正在下载/安装的市场插件 id（该行按钮置灰）
        private string _marketHint = "";                 // 最近一次安装 / 卸载的结果提示（状态行右侧）
        private bool _marketHintIsError;
        private int _marketScroll;                       // 市场列表滚动首行（绝对条目下标）

        // 弹窗（同一套外观：左上标题 + 右上 ❌ + 可选正文/按钮行）
        //   详情 / 评分 / 卸载确认三种弹窗互斥，同一时刻最多一个。
        private enum MarketDialog { None, Detail, Rate, ConfirmUninstall }

        private MarketDialog _marketDialog = MarketDialog.None;
        private int _marketDialogIndex = -1;             // 弹窗对应的市场条目下标
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

        /// <summary>关闭所有市场弹窗（切页签 / 弹窗外点击 / ❌ 都走这里）。</summary>
        private void CloseMarketDialog(bool render = false)
        {
            if (_marketDialog == MarketDialog.None && _marketDialogIndex == -1) return;
            _marketDialog = MarketDialog.None;
            _marketDialogIndex = -1;
            _hoveredDialogClose = false;
            _hoveredDialogButton = -1;
            _rateStatus = "";
            if (render) Render();
        }

        // ---- 布局真源 ----
        // 「我的插件」（tab 6）与「插件市场」（tab 7）各一张整页高卡片，行布局同一套：
        //   我的插件  顶卡 TITLE_BAR_HEIGHT+12..+108，列表卡 listY = TITLE_BAR_HEIGHT+122，行起点 +44
        //   插件市场  顶栏（分类下拉 + 搜索框）TITLE_BAR_HEIGHT+22..+48，状态行基线 +90，行起点 +98
        // 渲染 / 命中 / 滚轮三处共用，改一处必须同步。
        // 行高 50：一行为「名称 + 信息/按钮」两段，46px 内容 + 4px 呼吸。
        //    56 时市场卡底部会剩十几像素、我的插件卡剩近 50px（放不下整行却也不显示）——
        //    收到 50 后两张卡各多显示一行。
        private const float PluginListRowH = 50f;

        private void GetPluginListCardTop(out float listY)
            => listY = TITLE_BAR_HEIGHT + 122f;

        private const float MarketControlsY = TITLE_BAR_HEIGHT + 22f;   // 顶栏控件行顶（54，卡顶下留 10）
        private const float MarketControlH = 26f;
        private const float MarketCatBtnW = 132f;                       // 分类按钮宽（左起 CONTENT_TEXT_X）
        // 搜索框：紧接分类按钮右侧 16px，右边界与下方按钮组同一条基准线（MarketRightPad）。
        //    顶栏这一行只放「分类 + 搜索」两个控件，所以搜索框能吃满中间的空白。
        private const float MarketSearchX = CONTENT_TEXT_X + MarketCatBtnW + 16f;       // 350
        private const float MarketSearchW = (WIDTH - MarketRightPad) - MarketSearchX;   // 214
        private const float MarketStatusBaseline = TITLE_BAR_HEIGHT + 90f;
        private const float MarketRowsTop = TITLE_BAR_HEIGHT + 98f;     // 行起点（与命中 / 滚轮严格同源）

        // 「只看已安装」复选框：**放在状态行**（顶栏那行只留分类 + 搜索，避免三个控件挤在一起）。
        //    勾上后列表只留本机已装的插件（与搜索、分类叠加）。
        //    状态行左侧 = 复选框，右侧 = 计数 / 加载状态 / 安装结果提示（右对齐）。
        private const float MarketChkX = CONTENT_TEXT_X;
        private const float MarketChkBoxSize = 16f;
        private const string MarketChkLabel = "只看已安装";
        private const float MarketChkLabelGap = 7f;
        private static float MarketChkLabelX => MarketChkX + MarketChkBoxSize + MarketChkLabelGap;
        /// <summary>复选框方框的 y（在状态行里与文字视觉居中；比文字中线略高一点看着更稳）。</summary>
        private const float MarketChkY = MarketStatusBaseline - 15f;

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
                    _marketError = "市场加载失败，切走再切回来可重试";
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
                    Name = p.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    Author = p.TryGetProperty("author", out var author) ? author.GetString() ?? "" : "",
                    Desc = p.TryGetProperty("desc", out var desc) ? desc.GetString() ?? "" : "",
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
                        string s = t.GetString() ?? "";
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
        ///   ② **归一化后相等**：去掉 - _ 空格并转小写再比。市场 slug 常写成 nps-media-mixer，
        ///      而插件声明的 Id 是 NpsMediaMixer —— 原样比永远不相等，归一化后是同一个词；
        ///   ③ **显示名相同**：市场条目名与插件自报名出自同一作者，通常逐字一致（最稳的一档）；
        ///   ④ 目录名 / Key 首段 == 市场 id（zip 包装出来的 plugins/&lt;id&gt;/ 走这条）；
        ///   ⑤ 归一化后**互相包含**（较短者 ≥ 4 字符，避免 nps 之类短词误判）——
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
        // 版式：标题行（左上标题 + 右上 ❌）→ 可选正文 → 可选按钮行。
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

        /// <summary>右上角 ❌ 的矩形（渲染与命中同源）。</summary>
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

        /// <summary>卸载确认弹窗矩形：标题 + 两行正文 + 按钮行。</summary>
        private static SKRect GetConfirmDialogRect() => GetMarketDialogRect(2f, true);

        /// <summary>当前打开着的弹窗矩形（没有弹窗返回空）。渲染 / 命中 / 点击三处共用。</summary>
        private SKRect GetCurrentDialogRect()
        {
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
                        _rateStatus = $"已记录你的评分：{FormatScore(_rateMine)} 分，感谢反馈！";
                        _rateStatusIsError = false;
                    }
                    else
                    {
                        string err = root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
                        // already_rated 也带回了 mine（服务端记录的「你打过的分」），
                        //    所以这里能把真实分数报出来 —— 比干巴巴一句「你已经评过分」有用得多。
                        _rateStatus = err switch
                        {
                            "already_rated" => _rateMine > 0
                                ? $"你已经给此插件打了 {FormatScore(_rateMine)} 分，感谢您的参与"
                                : "你已经给此插件打过分了，感谢您的参与",
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
        /// 搜索框键盘输入：可见字符追加、退格删除、ESC 取消聚焦、回车取消聚焦。
        /// 只有市场页且搜索框聚焦时才吃掉按键（见 WndProc 的 WM_CHAR / WM_KEYDOWN）。
        /// </summary>
        private bool HandleMarketSearchKey(int vk, char ch)
        {
            if (_selectedTab != 7 || !_marketSearchFocused) return false;

            if (vk == Win32.VK_ESCAPE) { _marketSearchFocused = false; Render(); return true; }
            if (vk == Win32.VK_RETURN) { _marketSearchFocused = false; Render(); return true; }
            if (vk == Win32.VK_BACK)                                                   // Backspace
            {
                if (_marketSearch.Length > 0)
                {
                    _marketSearch = _marketSearch[..^1];
                    RefreshMarketFilter();
                    Render();
                }
                return true;
            }
            if (vk != -1) return true;   // 其它非字符键（方向键等）吞掉，不落进搜索串

            if (ch >= ' ' && ch != 0x7F)
            {
                _marketSearch += ch;
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
        // GCS_COMPSTR 时把「正在组字」的串存下来，画在搜索框里当预览（灰字），
        //   不然用户看不到自己正在拼什么。组字窗与候选窗都钉到搜索框下方。

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
                    int flags = lParam.ToInt32();
                    if ((flags & Win32.GCS_RESULTSTR) != 0)
                    {
                        string result = ReadImeString(Win32.GCS_RESULTSTR);
                        if (result.Length > 0)
                        {
                            _marketSearch += result;
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
                        return false;  // 组字串仍交给默认处理，IME 自己要画
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

        /// <summary>把 IME 的组字窗与候选窗钉到搜索框左下角（DIP → 物理像素）。</summary>
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
            _marketHint = $"正在下载 {mp.Name}…";
            _marketHintIsError = false;
            // 「正在下载」不设过期（装完再按结果重新计时）。上一轮的自动消失表要先停掉 ——
            // 否则它的到期时刻早就在眼前，一触发就会把「正在下载…」提前清掉。
            StopMarketHintTimer();
            PostAsyncRerender();

            Task.Run(async () =>
            {
                bool ok = false;
                try
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "NotchPeninsula", "market");
                    // ⚠️ 临时文件的**文件名必须是市场发布的原名**（mp.FileName）：
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
        /// 在 UI 线程消费后台安装结果：刷新两处列表 + 写提示（含自动消失计时）+ 按需弹出评分弹窗。
        /// 刷新是必须的 —— 装完后「我的插件」多了一行、市场那行的按钮也要从「下载」变「重装」，
        /// 所以这里无条件 RefreshPluginView()（它内部按变更序号缓存，没变时是空操作）。
        /// </summary>
        private void ApplyPendingMarketResult()
        {
            if (!_marketPendingHintDone) return;
            _marketPendingHintDone = false;

            RefreshPluginView();          // 已安装列表 + 市场行按钮文案都靠它重算

            if (_marketPendingHint.Length > 0)
            {
                SetMarketHint(_marketPendingHint, _marketPendingHintIsError);
                _marketPendingHint = "";
            }

            int rateIdx = _marketPendingRateIndex;
            _marketPendingRateIndex = -1;
            if (rateIdx >= 0 && rateIdx < _marketView.Count)
                OpenRateDialog(_marketView[rateIdx]);   // 装完自动问一句评分
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
            _marketPendingHint = msg;
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
                _marketPendingHint = $"已安装并加载：{fresh.FriendlyName}";
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
            SetMarketHint($"已卸载：{mp.Name}", false);
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

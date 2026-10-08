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

        private string _marketCategoryKey = "all";
        private bool _marketCategoryOpen;                    // 分类下拉展开态
        private int _hoveredMarketCategoryIndex = -1;        // 菜单内悬停项

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
        private enum MarketDialog { None, Detail, Rate, LoadFailed }

        private MarketDialog _marketDialog = MarketDialog.None;
        private int _marketDialogIndex = -1;             // 弹窗对应的市场条目下标
        private bool _hoveredDialogClose;                // 弹窗右上角 ❌ 是否悬停
        private int _hoveredDialogButton = -1;           // 弹窗内按钮：0 = 主按钮（加载失败提示的「好的」）

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
            _hoveredDialogClose = false;
            _hoveredDialogButton = -1;
            _rateStatus = "";
            if (render) Render();
        }

        private void ShowPluginLoadFailedDialog(bool render = true)
        {
            _marketDialog = MarketDialog.LoadFailed;
            _marketDialogIndex = -1;
            _hoveredDialogClose = false;
            _hoveredDialogButton = -1;
            if (render) Render();
        }

        // ---- 布局真源 ----
        //             列表行起点 +92
        // 渲染 / 命中 / 滚轮三处共用，改一处必须同步。
        //    收到 50 后两张卡各多显示一行。
        private const float PluginListRowH = 50f;

        private void GetPluginListCardTop(out float listY)
            => listY = TITLE_BAR_HEIGHT + 122f;

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
        private const float MarketRowsTop = TITLE_BAR_HEIGHT + 92f;

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
            float ph = DialogTitleH + 8f;
            if (bodyLines > 0f) ph += bodyLines * DialogLineH + 8f;
            if (buttons) ph += DialogBtnH + 18f;
            ph += 6f;
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
            return GetMarketDialogRect(lines, false);
        }

        private static SKRect GetRateDialogRect() => GetMarketDialogRect(4f, false);

        private static SKRect GetNoticeDialogRect() => GetMarketDialogRect(2f, true);

        private static SKRect GetDialogSingleButtonRect(SKRect popup)
        {
            float y = popup.Bottom - 6f - DialogBtnH - 12f;
            float x = popup.MidX - DialogBtnW / 2f;
            return new SKRect(x, y, x + DialogBtnW, y + DialogBtnH);
        }

        private SKRect GetCurrentDialogRect()
        {
            if (_marketDialog == MarketDialog.LoadFailed) return GetNoticeDialogRect();

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

        private void OpenRateDialog(MarketPlugin mp)
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

        private static string MarketInstallLabel(MarketPlugin mp, PluginEntry? local)
        {
            if (local == null) return "下载";
            if (local.Version.Length == 0) return "重装";
            return CompareVersions(mp.Version, local.Version) > 0 ? "更新" : "重装";
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

        private string _marketPendingHint = "";
        private bool _marketPendingHintIsError;
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

            if (_marketPendingHintIsError && _marketPendingHint.Length > 0)
            {
                SetMarketHint(_marketPendingHint, true);
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
            _marketPendingHint = ok ? "" : msg;
            _marketPendingHintIsError = !ok;
            return ok;
        }

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

        private void UninstallMarketPlugin(MarketPlugin mp)
        {
            var local = MatchLocalPlugin(mp);
            if (local == null) return;   // 理论上到不了（按钮置灰），兜一层
            PluginManager.Instance.Remove(local);
            ResetPluginHover();
            RefreshPluginView();
            if (_marketOnlyInstalled) RefreshMarketFilter();
            Render();
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

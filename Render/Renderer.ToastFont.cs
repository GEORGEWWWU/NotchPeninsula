using System;
using System.Globalization;
using Microsoft.Win32;

namespace NotchPeninsula
{
    public static partial class Renderer
    {
        // ── 通知字号：可调接口 ──
        // 原先四档字号是 13.5 / 11.5 / 11.5 / 10 这样的字面量，各自写死在绘制与排版算式里，
        // 编译期就被内联了，外部无论如何都改不到。这里把它们提成可写属性：
        // 写入后下一帧即按新字号排版，并作废 Toast 的文本分段与宽度缓存，岛宽跟着重算。
        //
        // 默认值就是改动前的原字号，所以不调用这些接口时，排版与改动前完全一致。

        /// <summary>全局硬边界：兜住注册表被手写坏的离谱值。</summary>
        public const float ToastFontMin = 8f;

        /// <summary>全局硬边界，见 <see cref="ToastFontMin"/>。</summary>
        public const float ToastFontMax = 32f;

        // 每档可用范围。上限按「55px 高的岛体还能容下两行」反推：
        // 块高 = 标题 + 正文 + 行距，再大就会像 24 / 22 那样两行贴在一起。
        public const float ToastSenderSizeMin = 9f;
        public const float ToastSenderSizeMax = 20f;
        public const float ToastBodySizeMin = 9f;
        public const float ToastBodySizeMax = 18f;
        public const float ToastAppSizeMin = 8f;
        public const float ToastAppSizeMax = 15f;
        public const float ToastMetaSizeMin = 8f;
        public const float ToastMetaSizeMax = 14f;

        // 默认值 = 项目原字号（改动前 _titlePaint / _bodyPaint / _compactTimePaint /
        // _compactAppPaint 的 TextSize），「恢复默认」就是回到这一档。
        public const float ToastSenderSizeDefault = 13.5f;   // 发送者 / 标题（粗体）
        public const float ToastBodySizeDefault = 11.5f;     // 消息正文
        public const float ToastAppSizeDefault = 11.5f;      // 完整模式左上角应用名
        public const float ToastMetaSizeDefault = 10f;       // 右侧「现在 + 应用名」小字

        // 持久化键名。宿主自己读写，与调节入口（设置页 / 插件）无关：
        // 入口只是把这些值写进来，数据源始终只有注册表这一份。
        private const string ToastSenderSizeKey = "ToastFontSender";
        private const string ToastBodySizeKey = "ToastFontBody";
        private const string ToastAppSizeKey = "ToastFontApp";
        private const string ToastMetaSizeKey = "ToastFontMeta";

        private static volatile float _toastSenderSize = ToastSenderSizeDefault;
        private static volatile float _toastBodySize = ToastBodySizeDefault;
        private static volatile float _toastAppSize = ToastAppSizeDefault;
        private static volatile float _toastMetaSize = ToastMetaSizeDefault;

        /// <summary>标题（发送者）字号，粗体那一行。</summary>
        public static float ToastSenderSize
        {
            get => _toastSenderSize;
            set
            {
                _toastSenderSize = ClampToastFontSize(value, ToastSenderSizeDefault, ToastSenderSizeMin, ToastSenderSizeMax);
                InvalidateToastTextCache();
            }
        }

        /// <summary>消息正文字号。</summary>
        public static float ToastBodySize
        {
            get => _toastBodySize;
            set
            {
                _toastBodySize = ClampToastFontSize(value, ToastBodySizeDefault, ToastBodySizeMin, ToastBodySizeMax);
                InvalidateToastTextCache();
            }
        }

        /// <summary>完整模式左上角应用名字号。</summary>
        public static float ToastAppSize
        {
            get => _toastAppSize;
            set
            {
                _toastAppSize = ClampToastFontSize(value, ToastAppSizeDefault, ToastAppSizeMin, ToastAppSizeMax);
                InvalidateToastTextCache();
            }
        }

        /// <summary>右侧「现在 / 来源」小字字号。</summary>
        public static float ToastMetaSize
        {
            get => _toastMetaSize;
            set
            {
                _toastMetaSize = ClampToastFontSize(value, ToastMetaSizeDefault, ToastMetaSizeMin, ToastMetaSizeMax);
                InvalidateToastTextCache();
            }
        }

        /// <summary>一次性写四档字号，只作废一次文本缓存；任一项传 NaN / Infinity 表示该档用默认值。</summary>
        public static void SetToastFontSizes(float sender, float body, float app, float meta)
        {
            _toastSenderSize = ClampToastFontSize(sender, ToastSenderSizeDefault, ToastSenderSizeMin, ToastSenderSizeMax);
            _toastBodySize = ClampToastFontSize(body, ToastBodySizeDefault, ToastBodySizeMin, ToastBodySizeMax);
            _toastAppSize = ClampToastFontSize(app, ToastAppSizeDefault, ToastAppSizeMin, ToastAppSizeMax);
            _toastMetaSize = ClampToastFontSize(meta, ToastMetaSizeDefault, ToastMetaSizeMin, ToastMetaSizeMax);
            InvalidateToastTextCache();
        }

        /// <summary>四档字号全部回到项目原字号。</summary>
        public static void ResetToastFontSizes() => SetToastFontSizes(
            ToastSenderSizeDefault, ToastBodySizeDefault, ToastAppSizeDefault, ToastMetaSizeDefault);

        /// <summary>把当前四档字号写进注册表，供下次 <see cref="RestoreToastFontSizes"/> 恢复。</summary>
        public static void PersistToastFontSizes()
        {
            Program.SaveSetting(ToastSenderSizeKey, _toastSenderSize);
            Program.SaveSetting(ToastBodySizeKey, _toastBodySize);
            Program.SaveSetting(ToastAppSizeKey, _toastAppSize);
            Program.SaveSetting(ToastMetaSizeKey, _toastMetaSize);
        }

        /// <summary>
        /// 启动早期从注册表恢复四档字号，由 Program.LoadSettings 调用。
        /// 放在宿主侧而不是调节入口里，是为了让「入口被关掉」不影响已保存的字号：
        /// 键不存在或值写坏时静默退回默认值，绝不能让配置读坏拖垮启动。
        /// </summary>
        public static void RestoreToastFontSizes(RegistryKey key)
        {
            try
            {
                SetToastFontSizes(
                    ReadToastFontSetting(key, ToastSenderSizeKey, ToastSenderSizeDefault),
                    ReadToastFontSetting(key, ToastBodySizeKey, ToastBodySizeDefault),
                    ReadToastFontSetting(key, ToastAppSizeKey, ToastAppSizeDefault),
                    ReadToastFontSetting(key, ToastMetaSizeKey, ToastMetaSizeDefault));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[通知字号] 恢复设置失败: {ex}");
            }
        }

        private static float ReadToastFontSetting(RegistryKey key, string name, float fallback)
        {
            object? raw = key.GetValue(name);
            if (raw is null) return fallback;

            // 兼容手写成字符串的情况：RegistryValueKind 由首次写入决定。
            string text = raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                ? ClampToastFontSize(v, fallback, ToastFontMin, ToastFontMax)
                : fallback;
        }

        private static float ClampToastFontSize(float value, float fallback, float min, float max)
            => float.IsNaN(value) || float.IsInfinity(value)
                ? fallback
                : Math.Clamp(value, Math.Max(min, ToastFontMin), Math.Min(max, ToastFontMax));

        /// <summary>字号变更后调用：下一帧强制重建文本分段与宽度缓存。</summary>
        public static void InvalidateToastTextCache() => _lastToastId = uint.MaxValue;

        // ── 通知行距：随字号自适应 ──
        // 原先标题与正文之间是写死的 5px；字号调大后字形变高而行距不变，两行就会贴住。
        // 这里把「标题 → 正文」段改成随字号增长的间距，默认字号下恰好仍是 5px，
        // 所以不调字号时排版与改动前完全一致。

        /// <summary>应用名 → 标题这一段的固定行距（项目原值）。</summary>
        public const float ToastBaseLineSpacing = 5f;

        /// <summary>正文字号每比默认大 1px，标题 → 正文行距增加的像素数。</summary>
        public const float ToastLineGapPerBodyPx = 1.0f;

        /// <summary>标题字号变化对行距的次要影响（标题变大本身会拉开空白，不必等额补偿）。</summary>
        public const float ToastLineGapTitleWeight = 0.3f;

        /// <summary>新增行距里分给「正文往下让」的比例，其余仍走整块居中。</summary>
        public const float ToastLineGapBodyBias = 0.25f;

        /// <summary>行距最多比基础值增加多少，防止大字号把文字顶出岛体。</summary>
        public const float ToastLineGapMaxExtra = 4.5f;

        /// <summary>
        /// 计算「标题 → 正文」的行距。字号越大正文越往下让一些，避免两行挤在一起。
        /// 以原字号（13.5 / 11.5）为基准：正文字号每大 1px 行距多 1px（字形也会高约 1px，
        /// 等额补偿才能保住两行间的视觉空白）；标题只按 0.3 折算。调小则维持基础行距，不再收紧。
        /// islandHeight 传目标岛高 TOAST_HEIGHT，不要传动画中的 currentHeight，否则展开动画里行距会抖。
        /// </summary>
        public static float ToastBodyLineGap(float senderSize, float bodySize, float appSize,
                                             bool fullMode, float islandHeight)
        {
            float growth = (bodySize - ToastBodySizeDefault) * ToastLineGapPerBodyPx +
                           (senderSize - ToastSenderSizeDefault) * ToastLineGapTitleWeight;
            float gap = ToastBaseLineSpacing + Math.Clamp(growth, 0f, ToastLineGapMaxExtra);

            // 岛高有限：整块居中后还会再下移 gapBias，底部要给字形下伸部留够空间，
            // 否则两档都拉到最大时正文会贴住岛体底边。按「底部至少 4px」反推
            // 本次行距增量的上限（extra 自身占 1 份、居中补偿与 bias 各占半份）。
            float fixedHeight = fullMode
                ? appSize + 0.5f + senderSize + bodySize + ToastBaseLineSpacing
                : senderSize + bodySize;
            const float minBottomMargin = 4f;
            float extraCap = (islandHeight - fixedHeight - ToastBaseLineSpacing - minBottomMargin * 2f)
                             / (1f + ToastLineGapBodyBias * 2f);
            float extra = Math.Clamp(gap - ToastBaseLineSpacing, 0f, Math.Max(0f, extraCap));
            return Math.Max(4f, ToastBaseLineSpacing + extra);
        }
    }
}

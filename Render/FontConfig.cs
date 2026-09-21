using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;

namespace NotchPeninsula
{
    /// <summary>
    /// 灵动岛字体统一配置中心（全岛唯一的字体来源）。
    ///
    /// 设计目标：让「灵动岛内所有文本用哪套字体」只在这一个文件里定义，
    /// 各处画笔统一从这里取 <see cref="Normal"/> / <see cref="Bold"/> / <see cref="SemiBold"/> 三个公共变量，
    /// 用户切换自定义字体时只需调用 <see cref="ApplyCustomFont"/>，再由 <see cref="Changed"/> 事件
    /// 通知 Renderer 把新字体重绑到全部文本画笔上 —— 因此不需要改动任何一处绘制代码。
    ///
    /// 默认状态（用户从未选择过自定义字体）与改动前完全一致：系统字体 Microsoft YaHei UI。
    /// </summary>
    public static class FontConfig
    {
        /// <summary>默认系统字体族名（与改动前各处硬编码的值保持一致）。</summary>
        public const string DefaultFamily = "Microsoft YaHei UI";

        // 系统字体三档字重：既是默认值，也是自定义字体缺字时的兜底字体
        private static readonly SKTypeface _systemNormal =
            SKTypeface.FromFamilyName(DefaultFamily);
        private static readonly SKTypeface _systemBold =
            SKTypeface.FromFamilyName(DefaultFamily, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        private static readonly SKTypeface _systemSemiBold =
            SKTypeface.FromFamilyName(DefaultFamily, SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

        /// <summary>常规字重：正文、副标题、Toast 主体。</summary>
        public static SKTypeface Normal { get; private set; } = _systemNormal;

        /// <summary>粗体字重：标题、Toast 发送者、时间。</summary>
        public static SKTypeface Bold { get; private set; } = _systemBold;

        /// <summary>半粗字重：歌词、硬件标签。</summary>
        public static SKTypeface SemiBold { get; private set; } = _systemSemiBold;

        /// <summary>
        /// 缺字兜底字体（恒为系统字体）。
        /// 用户若选了只含拉丁字形的字体，中文会走这里，避免整块文字变成方块。
        /// </summary>
        public static SKTypeface Fallback => _systemNormal;

        /// <summary>当前是否已启用自定义字体。</summary>
        public static bool HasCustomFont { get; private set; }

        /// <summary>当前自定义字体文件的完整路径；未启用时为空串。</summary>
        public static string CustomFontPath { get; private set; } = "";

        /// <summary>控制台卡片上展示的字体名（默认「系统字体」）。</summary>
        public static string DisplayName { get; private set; } = "系统字体";

        /// <summary>字体变更通知：Renderer 订阅后把新字体重绑到全部文本画笔。</summary>
        public static event Action? Changed;

        /// <summary>
        /// 当前正在使用的自定义字体面（来自 <see cref="SKTypeface.FromFile"/>）。
        /// 所有权归本类：切换字体 / 重置为系统字体时必须释放，否则每次换字体都会永久多出一份
        /// 字体文件数据（CJK 字体单份 15~30MB，ttc 集合更大），而 Renderer 的静态画笔会把它钉到进程结束。
        /// 系统字体三档 _systemNormal/_systemBold/_systemSemiBold 是进程级共享资源，永不进入这里，因此永不被释放。
        /// </summary>
        private static List<SKTypeface>? _customFaces;

        /// <summary>
        /// 加载指定字体文件并热应用到全岛。失败时保持原字体不变，并通过 error 返回原因。
        /// 只在用户点选字体时调用一次，无持续开销。
        ///
        /// 资源纪律：本次调用创建的每一个 SKTypeface 都有明确归宿 ——
        /// 被选中的三档字重由 <see cref="_customFaces"/> 接管，其余（ttc 里多余的字重）当场释放，
        /// 加载中途失败则全部释放。任何一条路径都不会留下无主的字体面。
        /// </summary>
        public static bool ApplyCustomFont(string path, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "字体文件不存在";
                return false;
            }

            List<SKTypeface>? faces = null;
            SKTypeface? newNormal = null, newBold = null, newSemiBold = null;
            bool picked = false;

            try
            {
                faces = LoadFaces(path);
                if (faces.Count == 0)
                {
                    error = "无法解析该字体文件（仅支持 ttf / otf / ttc）";
                    return false; // faces 为空，没有需要释放的对象
                }

                // 一个字体文件（尤其 ttc）可能内含多个字重，按目标字重挑最接近的那个
                newNormal = PickClosest(faces, SKFontStyleWeight.Normal);
                newBold = PickClosest(faces, SKFontStyleWeight.Bold);
                newSemiBold = PickClosest(faces, SKFontStyleWeight.SemiBold);
                picked = true;
            }
            catch (Exception ex)
            {
                error = "加载字体失败：" + ex.Message;
                Logger.Error($"[FontConfig] 加载自定义字体失败：{path}", ex);
            }

            if (!picked)
            {
                DisposeFaces(faces); // 失败：本次加载出来的字体面全部释放
                ResetToSystemFont();
                return false;
            }

            // 只保留被选中的字面，其余（ttc 中多余的字重）立刻释放。
            // 这些字面既不会被引用也不会再被用到，晚释放一秒就多占一秒的字体文件内存。
            for (int i = 0; i < faces!.Count; i++)
            {
                var face = faces[i];
                if (ReferenceEquals(face, newNormal) || ReferenceEquals(face, newBold) || ReferenceEquals(face, newSemiBold))
                    continue;
                SafeDispose(face);
            }

            SwitchFont(newNormal!, newBold!, newSemiBold!, path);
            return true;
        }

        /// <summary>
        /// 接管新字体并把上一套自定义字体释放掉。
        ///
        /// 释放时机是这里的关键：旧字体在被换下来的那一刻仍然被 Renderer 的 8 支静态画笔引用着，
        /// 必须等 <see cref="Changed"/> 事件把画笔全部重绑到新字体之后才能 Dispose，
        /// 否则画笔会短暂指向已释放的字体，下一帧绘制就是 use-after-dispose。
        /// </summary>
        private static void SwitchFont(SKTypeface normal, SKTypeface bold, SKTypeface semiBold, string path)
        {
            var oldFaces = _customFaces;

            Normal = normal;
            Bold = bold;
            SemiBold = semiBold;
            _customFaces = [normal, bold, semiBold];

            HasCustomFont = true;
            CustomFontPath = path;
            DisplayName = string.IsNullOrWhiteSpace(normal.FamilyName) ? Path.GetFileNameWithoutExtension(path) : normal.FamilyName;

            // 先重绑画笔，再释放旧字体；通知抛异常也不能让旧字体变成无主内存
            try { Changed?.Invoke(); }
            catch (Exception ex) { Logger.Error("[FontConfig] 字体变更通知异常", ex); }

            DisposeFaces(oldFaces);
            Logger.Info($"[FontConfig] 已切换灵动岛字体：{DisplayName}（{path}）");
        }

        /// <summary>恢复系统字体（只改内存状态，是否持久化由调用方决定）。</summary>
        public static void ResetToSystemFont()
        {
            var oldFaces = _customFaces;
            _customFaces = null;

            Normal = _systemNormal;
            Bold = _systemBold;
            SemiBold = _systemSemiBold;
            HasCustomFont = false;
            CustomFontPath = "";
            DisplayName = "系统字体";

            // 同样先让画笔重绑回系统字体，再释放自定义字体
            try { Changed?.Invoke(); }
            catch (Exception ex) { Logger.Error("[FontConfig] 字体变更通知异常", ex); }

            DisposeFaces(oldFaces);
        }

        /// <summary>释放一批字体面；null / 空集合安全，单个失败不影响其余。</summary>
        private static void DisposeFaces(List<SKTypeface>? faces)
        {
            if (faces == null) return;
            for (int i = 0; i < faces.Count; i++)
                SafeDispose(faces[i]);
        }

        /// <summary>释放单个字体面（同一实例可能被三档字重共用，重复 Dispose 在 SkiaSharp 里是安全空操作）。</summary>
        private static void SafeDispose(SKTypeface? face)
        {
            try { face?.Dispose(); }
            catch (Exception ex) { Logger.Warn($"[FontConfig] 释放字体面失败：{ex.Message}"); }
        }

        /// <summary>
        /// 启动时按注册表里记忆的路径恢复自定义字体。
        /// 未记忆过（默认状态）时直接返回，不产生任何改动；文件已被删除或损坏则静默回退系统字体。
        /// </summary>
        public static void Restore(string? savedPath)
        {
            if (string.IsNullOrWhiteSpace(savedPath)) return;

            if (!ApplyCustomFont(savedPath, out string error))
            {
                Logger.Info($"[FontConfig] 记忆的字体已不可用（{error}），本次回退系统字体：{savedPath}");
            }
        }

        /// <summary>
        /// 读取字体文件内的全部字体面（ttc / otf 集合可能包含多个字重）。
        /// 注意：返回的每一个 SKTypeface 都是新分配的非托管对象，<b>所有权全部交给调用方</b>，
        /// 调用方必须保证每个实例最终被 Dispose（选中的交给 _customFaces，未选中的立即释放）。
        /// </summary>
        private static List<SKTypeface> LoadFaces(string path)
        {
            var faces = new List<SKTypeface>(4);
            for (int index = 0; index < 8; index++)
            {
                SKTypeface? face;
                try { face = SKTypeface.FromFile(path, index); }
                catch { face = null; }

                if (face == null) break; // 索引越界即视为已取完
                faces.Add(face);
            }
            return faces;
        }

        /// <summary>在字体文件包含的多个字面里挑最接近目标字重的一个；只有一个字面时直接复用。</summary>
        private static SKTypeface PickClosest(List<SKTypeface> faces, SKFontStyleWeight target)
        {
            SKTypeface best = faces[0];
            int bestDelta = int.MaxValue;
            foreach (var face in faces)
            {
                int delta = Math.Abs((int)face.FontStyle.Weight - (int)target);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = face;
                }
            }
            return best;
        }
    }
}

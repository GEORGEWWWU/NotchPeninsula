using System.IO;
using SkiaSharp;

namespace NotchPeninsula
{
    public static class FontConfig
    {
        public const string DefaultFamily = "Microsoft YaHei UI";

        private static readonly SKTypeface _systemNormal =
            SKTypeface.FromFamilyName(DefaultFamily);
        private static readonly SKTypeface _systemBold =
            SKTypeface.FromFamilyName(DefaultFamily, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        private static readonly SKTypeface _systemSemiBold =
            SKTypeface.FromFamilyName(DefaultFamily, SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

        public static SKTypeface Normal { get; private set; } = _systemNormal;

        public static SKTypeface Bold { get; private set; } = _systemBold;

        public static SKTypeface SemiBold { get; private set; } = _systemSemiBold;

        public static SKTypeface Fallback => _systemNormal;

        public static bool HasCustomFont { get; private set; }

        public static string CustomFontPath { get; private set; } = "";

        public static string DisplayName { get; private set; } = "系统字体";

        public static event Action? Changed;

        private static List<SKTypeface>? _customFaces;

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

            try { Changed?.Invoke(); }
            catch (Exception ex) { Logger.Error("[FontConfig] 字体变更通知异常", ex); }

            DisposeFaces(oldFaces);
            Logger.Info($"[FontConfig] 已切换灵动岛字体：{DisplayName}（{path}）");
        }

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

        private static void DisposeFaces(List<SKTypeface>? faces)
        {
            if (faces == null) return;
            for (int i = 0; i < faces.Count; i++)
                SafeDispose(faces[i]);
        }

        private static void SafeDispose(SKTypeface? face)
        {
            try { face?.Dispose(); }
            catch (Exception ex) { Logger.Warn($"[FontConfig] 释放字体面失败：{ex.Message}"); }
        }

        public static void Restore(string? savedPath)
        {
            if (string.IsNullOrWhiteSpace(savedPath)) return;

            if (!ApplyCustomFont(savedPath, out string error))
            {
                Logger.Info($"[FontConfig] 记忆的字体已不可用（{error}），本次回退系统字体：{savedPath}");
            }
        }

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

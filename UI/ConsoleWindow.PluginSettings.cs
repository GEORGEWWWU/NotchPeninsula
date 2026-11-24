using System.Globalization;
using SkiaSharp;
using NotchPeninsula.Plugins;

namespace NotchPeninsula
{
    public partial class ConsoleWindow
    {
        // 插件设置子页：从「我的插件」行内「设置」按钮进入，不占新的侧栏 tab。
        private string? _settingsPluginId;
        private int _settingsScroll;
        private int _hoveredPluginSettings = -1;
        private bool _settingsBackHovered;
        private int _hoveredSettingControl = -1;
        private int _hoveredSettingSegment = -1;

        private const float SETTINGS_HEADER_H = 96f;
        private const float SETTINGS_ROW_H = 34f;
        private float SettingsValueX => RightColumnText + (WIDTH - CONTENT_TEXT_RM - RightColumnText) * 0.43f;
        private const float SETTINGS_BACK_W = 64f;

        private (string PluginId, ISettingsPage Page)? GetPluginSettingsPage()
        {
            string? id = _settingsPluginId;
            if (string.IsNullOrEmpty(id)) return null;
            var entry = PluginManager.Instance.Host.SettingsPages
                .FirstOrDefault(p => string.Equals(p.PluginId, id, StringComparison.OrdinalIgnoreCase));
            return entry.Page == null ? null : (entry.PluginId, entry.Page);
        }

        private void OpenPluginSettings(string pluginId)
        {
            _settingsPluginId = pluginId;
            _settingsScroll = 0;
            ResetPluginHover();
        }

        private void ClosePluginSettings()
        {
            _settingsPluginId = null;
            _settingsScroll = 0;
            _hoveredSettingControl = -1;
            _hoveredSettingSegment = -1;
            _settingsBackHovered = false;
        }

        private static float GetSettingsListTop() => TITLE_BAR_HEIGHT + CARD_TOP_Y;

        private int GetSettingsMaxScroll()
        {
            var info = GetPluginSettingsPage();
            if (info == null) return 0;
            int total = 28 + info.Value.Page.Controls.Count * (int)SETTINGS_ROW_H + 12;
            int visible = Math.Max(1, (int)((ContentBottom) - GetSettingsListTop() - 28f));
            return Math.Max(0, total - visible);
        }

        private SKRect GetSettingsBackRect()
        {
            float topY = TITLE_BAR_HEIGHT + CARD_TOP_Y;
            return new SKRect(CONTENT_TEXT_X, topY + 60, CONTENT_TEXT_X + SETTINGS_BACK_W, topY + 84);
        }

        private SKRect GetSettingsRowRect(int index)
        {
            float y = GetSettingsListTop() + 28f + index * SETTINGS_ROW_H - _settingsScroll;
            return new SKRect(RightColumnLeft + 8f, y, WIDTH - CONTENT_RM - 8f, y + SETTINGS_ROW_H - 4f);
        }

        private SKRect GetSettingsValueRect(int index)
        {
            var row = GetSettingsRowRect(index);
            return new SKRect(SettingsValueX, row.Top + 5f, row.Right - 8f, row.Bottom - 5f);
        }

        private static int SegmentCount(SettingControl c) => c switch
        {
            ToggleSetting => 2,
            ChoiceSetting choice => Math.Max(1, choice.Options.Length),
            _ => 0
        };

        private int HitSettingsSegment(int index, float x)
        {
            var info = GetPluginSettingsPage();
            if (info == null || index < 0 || index >= info.Value.Page.Controls.Count) return -1;
            int n = SegmentCount(info.Value.Page.Controls[index]);
            if (n <= 0) return -1;
            var r = GetSettingsValueRect(index);
            if (x < r.Left || x > r.Right) return -1;
            return Math.Clamp((int)((x - r.Left) / (r.Width / n)), 0, n - 1);
        }

        private void UpdatePluginSettingsHover(int x, int y,
            out bool backHovered, out int controlIndex, out int segmentIndex)
        {
            backHovered = GetSettingsBackRect().Contains(x, y);
            controlIndex = -1;
            segmentIndex = -1;

            var info = GetPluginSettingsPage();
            if (info == null) return;

            float top = GetSettingsListTop() + 28f;
            if (x < RightColumnLeft || x > WIDTH - CONTENT_RM || y < top || y > ContentBottom) return;
            int idx = (int)((y - top + _settingsScroll) / SETTINGS_ROW_H);
            if (idx < 0 || idx >= info.Value.Page.Controls.Count) return;
            var row = GetSettingsRowRect(idx);
            if (row.Top - 6f > y || row.Bottom + 6f < y) return;
            controlIndex = idx;
            segmentIndex = HitSettingsSegment(idx, x);
        }

        private void HandlePluginSettingsClick(int x, int y)
        {
            if (_settingsBackHovered)
            {
                ClosePluginSettings();
                Render();
                return;
            }

            var info = GetPluginSettingsPage();
            if (info == null || _hoveredSettingControl < 0
                || _hoveredSettingControl >= info.Value.Page.Controls.Count) return;

            var control = info.Value.Page.Controls[_hoveredSettingControl];
            var host = PluginManager.Instance.Host;
            string id = info.Value.PluginId;

            if (control is ToggleSetting toggle)
            {
                int v = _hoveredSettingSegment >= 0 ? _hoveredSettingSegment : 0;
                host.SetSetting(id, toggle.Key, v == 1 ? "1" : "0");
            }
            else if (control is ChoiceSetting choice)
            {
                int v = _hoveredSettingSegment >= 0 ? _hoveredSettingSegment : 0;
                host.SetSetting(id, choice.Key, v.ToString(CultureInfo.InvariantCulture));
            }
            else if (control is NumberSetting number)
            {
                var r = GetSettingsValueRect(_hoveredSettingControl);
                float t = Math.Clamp((x - r.Left) / Math.Max(1f, r.Width), 0f, 1f);
                float raw = number.Min + (number.Max - number.Min) * t;
                int steps = number.Step > 0f ? (int)MathF.Round((raw - number.Min) / number.Step) : 0;
                float value = number.Min + steps * number.Step;
                if (value > number.Max) value = number.Max;
                host.SetSetting(id, number.Key, value.ToString("0.###", CultureInfo.InvariantCulture));
            }

            Render();
        }

        private void RenderPluginSettingsPage(SKCanvas canvas)
        {
            var info = GetPluginSettingsPage();
            float topY = TITLE_BAR_HEIGHT + CARD_TOP_Y;

            var header = new SKRect(CONTENT_L, topY, LeftColumnRight, topY + SETTINGS_HEADER_H);
            DrawSettingsCard(canvas, header);
            canvas.DrawText("插件设置", CONTENT_TEXT_X, topY + 26, _uiTextPaint);

            string name = info == null ? "(插件未提供设置页)" : PluginManager.Instance.Host.GetPluginDisplayName(info.Value.PluginId);
            canvas.DrawText(TruncateText(name, _uiTextPaint, LeftColumnRight - CONTENT_TEXT_X - 16f),
                CONTENT_TEXT_X, topY + 48, _subTextPaint);

            var back = GetSettingsBackRect();
            DrawSettingsButton(canvas, back, "返回", _settingsBackHovered);

            float listY = GetSettingsListTop();
            var listRect = new SKRect(RightColumnLeft, listY, WIDTH - CONTENT_RM, ContentBottom);
            DrawSettingsCard(canvas, listRect);

            canvas.DrawText("插件参数", RightColumnText, listY + 23, _uiTextPaint);
            if (info == null)
            {
                canvas.DrawText("这个插件没有提供设置页。", RightColumnText, listY + 48, _subTextPaint);
                return;
            }

            int maxScroll = GetSettingsMaxScroll();
            _settingsScroll = Math.Clamp(_settingsScroll, 0, maxScroll);

            canvas.Save();
            canvas.ClipRect(listRect);

            var host = PluginManager.Instance.Host;
            var controls = info.Value.Page.Controls;
            for (int i = 0; i < controls.Count; i++)
            {
                var row = GetSettingsRowRect(i);
                if (row.Bottom < listRect.Top || row.Top > listRect.Bottom) continue;

                var c = controls[i];
                canvas.DrawText(TruncateText(c.Label, _subTextPaint, SettingsValueX - RightColumnText - 16f),
                    RightColumnText + 4f, row.Top + 19f, _subTextPaint);

                var valueRect = GetSettingsValueRect(i);
                string raw = host.GetSetting(info.Value.PluginId, c.Key, DefaultOf(c));

                if (c is ToggleSetting || c is ChoiceSetting)
                {
                    string[] labels = c is ToggleSetting ? ["关", "开"] : ((ChoiceSetting)c).Options;
                    if (labels.Length == 0) continue;
                    int selected = int.TryParse(raw, out int si) ? Math.Clamp(si, 0, labels.Length - 1) : 0;
                    DrawSettingSegments(canvas, labels, selected,
                        _hoveredSettingControl == i ? _hoveredSettingSegment : -1, valueRect);
                }
                else if (c is NumberSetting number)
                {
                    float v = float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv)
                        ? Math.Clamp(fv, number.Min, number.Max) : number.Default;
                    DrawSettingSlider(canvas, number, v, valueRect, _hoveredSettingControl == i);
                }
            }

            canvas.Restore();

            if (maxScroll > 0)
                DrawScrollBar(canvas, WIDTH - 26, listY + 12f, listRect.Height - 24f,
                    Math.Min(1f, (float)(listRect.Height - 24f) / (28f + controls.Count * SETTINGS_ROW_H + 12f)),
                    _settingsScroll / (float)maxScroll);
        }

        private static string DefaultOf(SettingControl c) => c switch
        {
            ToggleSetting t => t.DefaultValue ? "1" : "0",
            ChoiceSetting ch => ch.DefaultIndex.ToString(CultureInfo.InvariantCulture),
            NumberSetting n => n.Default.ToString("0.###", CultureInfo.InvariantCulture),
            _ => ""
        };

        private void DrawSettingSegments(SKCanvas canvas, string[] labels, int selected, int hovered, SKRect r)
        {
            DrawSegmented(canvas, r.Left, r.Top, r.Width, r.Height, labels, selected, hovered);
        }

        private void DrawSettingSlider(SKCanvas canvas, NumberSetting number, float value, SKRect r, bool hovered)
        {
            float t = number.Max > number.Min ? (value - number.Min) / (number.Max - number.Min) : 0f;
            DrawSliderTrack(canvas, r.Left, r.Right, r.MidY, r.Left + r.Width * t, hovered);

            string text = value.ToString("0.###", CultureInfo.InvariantCulture);
            float tw = _subTextPaint.MeasureText(text);
            canvas.DrawText(text, r.Right - tw, r.Top + 10f, _subTextPaint);
        }
    }
}

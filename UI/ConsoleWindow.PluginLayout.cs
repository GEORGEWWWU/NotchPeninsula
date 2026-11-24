using SkiaSharp;

namespace NotchPeninsula
{
    public partial class ConsoleWindow
    {
        private const int PluginColumns = 2;
        private const float PluginTileGap = 6f;
        private const float PluginTileHeight = 58f;
        private const float PluginTilePitch = PluginTileHeight + PluginTileGap;
        private const float PluginButtonsTop = (PluginTileHeight - 20f) / 2f;
        private const float MarketButtonsTop = PluginTileHeight / 2f;
        private float PluginTileLeft => CONTENT_L + 6f;
        private float PluginTileSpace => WIDTH - CONTENT_RM - 12f - PluginTileLeft;
        private float PluginTileWidth => (PluginTileSpace - PluginTileGap) / PluginColumns;

        // 市场用两条对齐的横排：标题与统计在上，副文本与操作在下。
        private const float MarketTitleBaseline = MarketButtonsTop - 9f;
        private const float MarketMetadataBaseline = MarketButtonsTop + 14f;

        private (float Title, float Metadata) PluginTextBaselines(SKRect tile, string title)
        {
            const float lineSpacing = 18f;
            SKRect titleInk = default, metadataInk = default;
            _pluginTitlePaint.MeasureText(title.Length == 0 ? "插件" : title, ref titleInk);
            _subTextPaint.MeasureText("版本 · 作者", ref metadataInk);
            float baseline = tile.MidY - (titleInk.Top + lineSpacing + metadataInk.Bottom) / 2f;
            return (baseline, baseline + lineSpacing);
        }

        private const float PluginTextInset = 10f;

        private static readonly SKPaint _pluginTitlePaint = new()
        {
            TextSize = 14f,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName(UI_TEXT_FONT, SKFontStyleWeight.SemiBold,
                SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
        };

        private SKRect PluginTile(float top, int slot)
        {
            float width = PluginTileWidth;
            float x = PluginTileLeft + (slot % PluginColumns) * (width + PluginTileGap);
            float y = top + (slot / PluginColumns) * PluginTilePitch;
            return SKRect.Create(x, y, width, PluginTileHeight);
        }

        private void DrawPluginTile(SKCanvas canvas, SKRect tile)
        {
            _dynamicFillPaint.Color = Overlay(8);
            canvas.DrawRoundRect(tile, SettingsCardRadius, SettingsCardRadius, _dynamicFillPaint);
        }

        private int HitPluginTile(float top, int x, int y)
        {
            if (x < PluginTileLeft || y < top)
                return -1;
            int col = (int)((x - PluginTileLeft) / (PluginTileWidth + PluginTileGap));
            if (col >= PluginColumns)
                return -1;
            int slot = (int)((y - top) / PluginTilePitch) * PluginColumns + col;
            return PluginTile(top, slot).Contains(x, y) ? slot : -1;
        }

        private void MeasurePluginTiles(int total, int first, float top, float bottom, out int visible, out int maxFirst)
        {
            int rows = Math.Max(1, (int)((bottom - top + PluginTileGap) / PluginTilePitch));
            int totalRows = (total + PluginColumns - 1) / PluginColumns;
            maxFirst = Math.Max(0, totalRows - rows) * PluginColumns;
            visible = Math.Min(total - Math.Clamp(first, 0, maxFirst), rows * PluginColumns);
        }

        private float GetPluginRowsHeight(int visible)
            => ((visible + PluginColumns - 1) / PluginColumns) * PluginTilePitch - PluginTileGap;
    }
}

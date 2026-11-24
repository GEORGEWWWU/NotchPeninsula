using SkiaSharp;

namespace NotchPeninsula
{
    public partial class ConsoleWindow
    {
        private const float PluginLayoutButtonWidth = 78f;
        private bool _showPluginCards;
        private int _hoveredPluginLayout = -1;
        private float PluginTileLeft => CONTENT_L + 6f;
        private float PluginTileSpace => WIDTH - CONTENT_RM - 12f - PluginTileLeft;
        private int PluginColumns => _showPluginCards
            ? Math.Max(2, (int)((PluginTileSpace + CARD_GAP) / (272f + CARD_GAP)))
            : 2;
        private float PluginTileWidth => (PluginTileSpace - (PluginColumns - 1) * CARD_GAP) / PluginColumns;
        private float PluginTileHeight => _showPluginCards ? 86f : 54f;
        private float PluginTilePitch => PluginTileHeight + CARD_GAP;
        private float PluginButtonsTop => _showPluginCards ? 55f : 23f;

        private float PluginTitleBaseline => _showPluginCards ? 23f : 19f;
        private float PluginMetadataBaseline => _showPluginCards ? 43f : 39f;
        private float PluginTextInset => _showPluginCards ? 14f : 10f;

        private static readonly SKPaint _pluginTitlePaint = new()
        {
            TextSize = 14f,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName(UI_TEXT_FONT, SKFontStyleWeight.SemiBold,
                SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
        };

        private SKRect PluginTile(float top, int slot)
        {
            int columns = PluginColumns;
            float width = PluginTileWidth;
            float x = PluginTileLeft + (slot % columns) * (width + CARD_GAP);
            float y = top + (slot / columns) * PluginTilePitch;
            return SKRect.Create(x, y, width, PluginTileHeight);
        }

        private void DrawPluginTile(SKCanvas canvas, SKRect tile)
        {
            _dynamicFillPaint.Color = Overlay(_showPluginCards ? (byte)17 : (byte)8);
            canvas.DrawRoundRect(tile, SettingsCardRadius, SettingsCardRadius, _dynamicFillPaint);
            if (_showPluginCards)
            {
                var footer = new SKRect(tile.Left + 6f, tile.Top + PluginButtonsTop - 4f,
                    tile.Right - 6f, tile.Bottom - 6f);
                _dynamicFillPaint.Color = Overlay(9);
                canvas.DrawRoundRect(footer, SettingsControlRadius, SettingsControlRadius, _dynamicFillPaint);
            }
        }

        private int HitPluginTile(float top, int x, int y)
        {
            if (x < PluginTileLeft || y < top)
                return -1;
            int col = (int)((x - PluginTileLeft) / (PluginTileWidth + CARD_GAP));
            if (col >= PluginColumns)
                return -1;
            int slot = (int)((y - top) / PluginTilePitch) * PluginColumns + col;
            return PluginTile(top, slot).Contains(x, y) ? slot : -1;
        }

        private void MeasurePluginTiles(int total, int first, float top, float bottom, out int visible, out int maxFirst)
        {
            int columns = PluginColumns;
            int rows = Math.Max(1, (int)((bottom - top + CARD_GAP) / PluginTilePitch));
            int totalRows = (total + columns - 1) / columns;
            maxFirst = Math.Max(0, totalRows - rows) * columns;
            visible = Math.Min(total - Math.Clamp(first, 0, maxFirst), rows * columns);
        }

        private float GetPluginRowsHeight(int visible)
            => ((visible + PluginColumns - 1) / PluginColumns) * PluginTilePitch - CARD_GAP;

        private SKRect PluginLayoutButton(bool cards, float y)
        {
            float right = WIDTH - CONTENT_TEXT_RM - (cards ? 0f : PluginLayoutButtonWidth + 4f);
            return SKRect.Create(right - PluginLayoutButtonWidth, y, PluginLayoutButtonWidth, 26);
        }

        private void DrawPluginLayoutButtons(SKCanvas canvas, float y)
        {
            Draw(false, "双栏列表");
            Draw(true, "卡片");

            void Draw(bool cards, string label)
            {
                var rect = PluginLayoutButton(cards, y);
                bool selected = _showPluginCards == cards;
                DrawSettingsButton(canvas, rect, label, _hoveredPluginLayout == (cards ? 1 : 0), selected: selected);
            }
        }

        private int HitPluginLayoutButton(int x, int y)
        {
            if (IsConfirmDialogOpen || (_selectedTab != 7 && (_selectedTab != 6 || _settingsPluginId != null)))
                return -1;
            if (_selectedTab == 7 && (_marketSortOpen || _marketCategoryOpen))
                return -1;

            float top = _selectedTab == 7 ? MarketControlsY : TITLE_BAR_HEIGHT + CARD_TOP_Y + 60;
            if (PluginLayoutButton(true, top).Contains(x, y))
                return 1;
            return PluginLayoutButton(false, top).Contains(x, y) ? 0 : -1;
        }

        private bool HandlePluginLayoutClick(int x, int y)
        {
            int layout = HitPluginLayoutButton(x, y);
            if (layout < 0)
                return false;

            _showPluginCards = layout == 1;
            _pluginScroll = _pluginScroll / PluginColumns * PluginColumns;
            _marketScroll = _marketScroll / PluginColumns * PluginColumns;
            _marketSearchFocused = false;
            _marketSearchDragging = false;
            _marketImeComposing = "";
            OnMouseMove(x, y, false);
            Render();
            return true;
        }

    }
}

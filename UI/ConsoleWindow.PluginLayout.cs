using SkiaSharp;

namespace NotchPeninsula
{
    public partial class ConsoleWindow
    {
        private const float PluginLayoutButtonWidth = 78f;
        private bool _showPluginCards;
        private float PluginTileLeft => CONTENT_L + 6f;
        private float PluginTileSpace => WIDTH - CONTENT_RM - 12f - PluginTileLeft;
        private int PluginColumns => _showPluginCards
            ? Math.Max(2, (int)((PluginTileSpace + CARD_GAP) / (272f + CARD_GAP)))
            : 2;
        private float PluginTileWidth => (PluginTileSpace - (PluginColumns - 1) * CARD_GAP) / PluginColumns;
        private float PluginTileHeight => _showPluginCards ? 76f : 50f;
        private float PluginTilePitch => PluginTileHeight + CARD_GAP;
        private float PluginButtonsTop => _showPluginCards ? 48f : 21f;

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
            if (_showPluginCards)
            {
                canvas.DrawRoundRect(tile, 6, 6, _cardBg);
                canvas.DrawRoundRect(tile, 6, 6, _cardBorder);
            }
            else
            {
                canvas.DrawLine(tile.Left + 10, tile.Bottom, tile.Right - 10, tile.Bottom, _separatorPaint);
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
                _dynamicFillPaint.Color = selected ? new SKColor(0, 98, 178) : Overlay(16);
                canvas.DrawRoundRect(rect, 4, 4, _dynamicFillPaint);
                _uiTextPaint.Color = selected ? SKColors.White : _fgColor;
                DrawCenteredButtonLabel(canvas, label, rect, _uiTextPaint);
                _uiTextPaint.Color = _fgColor;
            }
        }

        private bool HandlePluginLayoutClick(int x, int y)
        {
            if (_selectedTab != 7 && (_selectedTab != 6 || _settingsPluginId != null))
                return false;
            if (_selectedTab == 7 && (_marketSortOpen || _marketCategoryOpen))
                return false;
            float top = _selectedTab == 7 ? MarketControlsY : TITLE_BAR_HEIGHT + CARD_TOP_Y + 60;
            bool cards = PluginLayoutButton(true, top).Contains(x, y);
            if (!cards && !PluginLayoutButton(false, top).Contains(x, y))
                return false;

            _showPluginCards = cards;
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

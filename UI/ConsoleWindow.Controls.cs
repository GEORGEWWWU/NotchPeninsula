using SkiaSharp;

namespace NotchPeninsula
{
    public partial class ConsoleWindow
    {
        private const float SettingsCardRadius = 8f;
        private const float SettingsControlRadius = 4f;

        private enum SettingsButtonKind { Standard, Primary, Danger }

        private SKColor SecondaryText => Neutral(200);
        private SKColor DisabledText => Neutral(135);
        private SKColor AccentText => _isLightAppearance ? new SKColor(0, 84, 156) : new SKColor(137, 194, 243);
        private SKColor OnAccent => _isLightAppearance ? SKColors.White : new SKColor(12, 32, 52);
        private SKColor DangerText => _isLightAppearance ? new SKColor(157, 36, 40) : new SKColor(255, 162, 157);

        private SKColor AccentFill(bool hovered, bool pressed = false) => _isLightAppearance
            ? pressed ? new SKColor(0, 65, 122) : hovered ? new SKColor(0, 82, 154) : new SKColor(0, 95, 184)
            : pressed ? new SKColor(95, 161, 215) : hovered ? new SKColor(155, 207, 248) : new SKColor(137, 194, 243);

        private bool SettingsControlPressed(bool hovered)
            => hovered && (Win32.GetKeyState(Win32.VK_LBUTTON) & 0x8000) != 0;

        private void DrawSettingsCard(SKCanvas canvas, SKRect rect)
            => canvas.DrawRoundRect(rect, SettingsCardRadius, SettingsCardRadius, _cardBg);

        private void DrawControlSurface(SKCanvas canvas, SKRect rect, bool hovered, bool enabled = true,
            bool focused = false, bool selected = false)
        {
            bool hot = enabled && hovered;
            bool pressed = SettingsControlPressed(hot);
            _dynamicFillPaint.Color = !enabled ? Overlay(5)
                : focused || selected ? AccentText.WithAlpha(pressed ? (byte)44 : hot ? (byte)32 : (byte)21)
                : Overlay(pressed ? (byte)27 : hot ? (byte)20 : (byte)10);
            canvas.DrawRoundRect(rect, SettingsControlRadius, SettingsControlRadius, _dynamicFillPaint);

            float previousWidth = _dynamicStrokePaint.StrokeWidth;
            _dynamicStrokePaint.StrokeWidth = 1f;
            _dynamicStrokePaint.Color = focused && enabled ? AccentText
                : selected && enabled ? AccentText.WithAlpha(135)
                : Overlay(!enabled ? (byte)12 : hot ? (byte)55 : (byte)38);
            canvas.DrawRoundRect(rect, SettingsControlRadius, SettingsControlRadius, _dynamicStrokePaint);
            _dynamicStrokePaint.StrokeWidth = previousWidth;
        }

        private void DrawSettingsButton(SKCanvas canvas, SKRect rect, string label, bool hovered, bool enabled = true,
            SettingsButtonKind kind = SettingsButtonKind.Standard, bool selected = false)
        {
            if (kind == SettingsButtonKind.Primary && enabled)
            {
                _dynamicFillPaint.Color = AccentFill(hovered, SettingsControlPressed(hovered));
                canvas.DrawRoundRect(rect, SettingsControlRadius, SettingsControlRadius, _dynamicFillPaint);
            }
            else
            {
                DrawControlSurface(canvas, rect, hovered, enabled, selected: selected);
            }

            _uiTextPaint.Color = !enabled ? DisabledText : kind == SettingsButtonKind.Primary ? OnAccent
                : kind == SettingsButtonKind.Danger ? DangerText : selected ? AccentText : _fgColor;
            DrawCenteredButtonLabel(canvas, TruncateText(label, _uiTextPaint, rect.Width - 6f), rect, _uiTextPaint);
        }

        private void DrawSettingsSwitch(SKCanvas canvas, SKRect rect, bool on, bool hovered, bool enabled = true)
        {
            float radius = rect.Height / 2f;
            bool hot = enabled && hovered;
            bool pressed = SettingsControlPressed(hot);
            _dynamicFillPaint.Color = on && enabled ? AccentFill(hot, pressed)
                : Overlay(!enabled ? (byte)5 : pressed ? (byte)22 : hot ? (byte)15 : (byte)6);
            canvas.DrawRoundRect(rect, radius, radius, _dynamicFillPaint);

            if (!on || !enabled)
            {
                float previousWidth = _dynamicStrokePaint.StrokeWidth;
                _dynamicStrokePaint.StrokeWidth = 1f;
                _dynamicStrokePaint.Color = !enabled ? Overlay(40) : hot ? SecondaryText : Neutral(155);
                canvas.DrawRoundRect(rect, radius, radius, _dynamicStrokePaint);
                _dynamicStrokePaint.StrokeWidth = previousWidth;
            }

            float knobX = on ? rect.Right - radius : rect.Left + radius;
            _dynamicFillPaint.Color = !enabled ? DisabledText : on ? OnAccent : SecondaryText;
            canvas.DrawCircle(knobX, rect.MidY, radius - (pressed ? 3f : 4f), _dynamicFillPaint);
        }

        private void DrawSettingsCheckBox(SKCanvas canvas, SKRect rect, bool selected, bool hovered, bool enabled = true)
        {
            if (selected)
            {
                _dynamicFillPaint.Color = enabled ? AccentFill(hovered, SettingsControlPressed(hovered)) : Overlay(42);
                canvas.DrawRoundRect(rect, SettingsControlRadius, SettingsControlRadius, _dynamicFillPaint);
                var previous = _iconPaint.Color;
                _iconPaint.Color = enabled ? OnAccent : DisabledText;
                canvas.DrawLine(rect.Left + 3f, rect.MidY, rect.MidX - 1f, rect.Bottom - 4f, _iconPaint);
                canvas.DrawLine(rect.MidX - 1f, rect.Bottom - 4f, rect.Right - 3f, rect.Top + 4f, _iconPaint);
                _iconPaint.Color = previous;
                return;
            }

            DrawControlSurface(canvas, rect, hovered, enabled);
        }

        private void DrawSliderTrack(SKCanvas canvas, float left, float right, float y, float active, bool hovered)
        {
            _dynamicFillPaint.Color = Overlay(60);
            canvas.DrawRoundRect(new SKRect(left, y - 1, right, y + 1), 1, 1, _dynamicFillPaint);
            _dynamicFillPaint.Color = AccentFill(hovered, SettingsControlPressed(hovered));
            canvas.DrawRoundRect(new SKRect(left, y - 1, active, y + 1), 1, 1, _dynamicFillPaint);
            canvas.DrawCircle(active, y, hovered ? 6f : 5.5f, _dynamicFillPaint);
        }

        private bool ApplySettingsCursor(int x, int y, bool defaultToArrow = false)
        {
            if (_isHoveringDisabledArea)
            {
                Win32.SetCursor(Win32.LoadCursor(IntPtr.Zero, Win32.IDC_NO));
                return true;
            }
            if (_selectedTab == 7 && _marketSearchHovered && _marketDialog == MarketDialog.None
                && !_marketCategoryOpen && !_marketSortOpen
                && x >= MarketSearchX && x <= MarketSearchX + MarketSearchW
                && y >= MarketControlsY && y <= MarketControlsY + MarketControlH)
            {
                Win32.SetCursor(Win32.LoadCursor(IntPtr.Zero, Win32.IDC_IBEAM));
                return true;
            }
            if (IsSettingsControlHovered(x, y))
            {
                Win32.SetCursor(_hCursorHand);
                return true;
            }
            if (defaultToArrow)
                Win32.SetCursor(Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW));
            return false;
        }

        private bool IsSettingsControlHovered(int x, int y)
        {
            if (x < CONTENT_L || x >= WIDTH || y < TITLE_BAR_HEIGHT || y >= ContentBottom || _isHoveringDisabledArea)
                return false;
            if (_marketDialog != MarketDialog.None)
                return _hoveredDialogButton >= 0 || _hoveredDialogClose || _loadFailedLinkHovered || _rateStars > 0;

            return _selectedTab switch
            {
                0 => _toastModeDropdownOpen || _toastSoundDropdownOpen || _soundVolumeDropdownOpen
                    ? _hoveredToastModeIndex >= 0 || _hoveredToastSoundIndex >= 0 || _hoveredSoundVolumeIndex >= 0
                    : _toggleHovered || _topmostToggleHovered || _toastToggleHovered || _toastModeDropdownHovered
                        || _soundToggleHovered || _toastSoundDropdownHovered || _soundVolumeDropdownHovered
                        || _soundPreviewHovered || _soundResetHovered || _mspToggleHovered || _mspWikiHovered
                        || _fontPickHovered || _fontResetHovered,
                1 => _monitorDropdownOpen || _sceneDropdownOpen
                    ? _hoveredMonitorDropdownIndex >= 0 || _hoveredSceneIndex >= 0
                    : _hoveredStyleIndex >= 0 || _hoveredDisplayModeIndex >= 0 || _sceneDropdownHovered
                        || _standbyToggleHovered || _monitorDropdownHovered || _hoveredDisplayRow >= 0
                        || _hoveredDisplayMoveUp >= 0 || _hoveredDisplayMoveDown >= 0 || _hoveredDisplayDrag >= 0,
                2 => _dropdownOpen || _matchModeDropdownOpen || _appDropdownOpen
                    ? _hoveredDropdownIndex >= 0 || _hoveredMatchModeIndex >= 0 || _hoveredAppIndex >= 0
                    : _mediaToggleHovered || _mediaLiveToggleHovered || _dropdownHovered || _matchModeDropdownHovered
                        || _appDropdownHovered || _lyricToggleHovered || _transToggleHovered || _scanToggleHovered
                        || _lyricMinusHovered || _lyricPlusHovered || _lyricResetHovered || _hotkeyToggleHovered
                        || _hoveredHotkeyRow >= 0,
                3 => _autoHideToggleHovered || _focusHideToggleHovered || _pauseHideToggleHovered
                    || _fsHideToggleHovered || _mediaExpToggleHovered || _appLaunchToggleHovered || _passToggleHovered
                    || _islandHotkeyHovered || _islandHotkeyToggleHovered || _silentHotkeyHovered || _silentHotkeyToggleHovered,
                4 => _hoveredLinkIndex >= 0,
                5 => _hoveredThemeIndex >= 0 || _hoveredMaterialIndex >= 0 || _hoveredOpacityIndex >= 0
                    || _hoveredMinusIndex >= 0 || _hoveredPlusIndex >= 0 || _hoveredResetIndex >= 0
                    || _hoveredGlassParamIndex >= 0 || _hoveredGlassReset,
                6 => _settingsPluginId != null
                    ? _settingsBackHovered || _hoveredSettingControl >= 0
                    : _hoveredPluginLayout >= 0 || _hoveredPluginAction >= 0 || _hoveredPluginToggle >= 0
                        || _hoveredPluginReload >= 0 || _hoveredPluginRemove >= 0 || _hoveredPluginSettings >= 0,
                7 => _marketCategoryOpen || _marketSortOpen
                    ? _hoveredMarketCategoryIndex >= 0 || _hoveredMarketSortIndex >= 0
                    : _hoveredPluginLayout >= 0 || _hoveredMarketSortBox || _hoveredMarketCategoryIndex == -2
                        || _hoveredMarketChk || (_hoveredMarketRefresh && !_marketFetching)
                        || (_hoveredMarketUpdateAll && !_updateAllRunning && _marketBusyId.Length == 0)
                        || _hoveredMarketInstall >= 0 || _hoveredMarketUninstall >= 0 || _hoveredMarketDetail >= 0,
                8 => _liveWindowMediaHovered || _liveWindowHoverRow >= 0,
                9 => _silentSwitchHovered || _silentToastHovered || _silentMediaHovered
                    || _hoveredSilentRow >= 0 || _hoveredSilentMoveUp >= 0 || _hoveredSilentMoveDown >= 0,
                _ => false
            };
        }
    }
}

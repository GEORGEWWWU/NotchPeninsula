using System.Runtime.InteropServices;

namespace NotchPeninsula
{
    public partial class ConsoleWindow
    {
        private const int WsThickFrame = 0x00040000;
        private const uint WmGetMinMaxInfo = 0x0024;
        private const uint WmNcCalcSize = 0x0083;
        private const uint WmWindowPosChanging = 0x0046;
        private const uint WmSizing = 0x0214;
        private const uint WmDpiChanged = 0x02E0;
        private const int HtLeft = 10;
        private const int HtRight = 11;
        private const int HtTop = 12;
        private const int HtTopLeft = 13;
        private const int HtTopRight = 14;
        private const int HtBottom = 15;
        private const int HtBottomLeft = 16;
        private const int HtBottomRight = 17;

        private float _monitorScale = 1f;
        private bool _backdropMinimized;

        private const int AspectWidth = 16;
        private const int AspectHeight = 8;
        private const int MinimumWindowWidth = 896;
        private const int MaximumWindowWidth = 1200;
        private static float _sessionWindowWidth = DefaultWidth;
        private int _sizingStartWidth;
        private bool _userSizing;

        private static int HeightForWidth(int width) => width / AspectWidth * AspectHeight;

        private static int SnapWindowWidth(int width, int min, int max)
            => Math.Clamp((int)Math.Round(width / (double)AspectWidth) * AspectWidth, min, max);

        private (int Min, int Max) GetTrackWidths(Rectangle workArea)
        {
            int maxUnits = Math.Min((int)(MaximumWindowWidth * _monitorScale) / AspectWidth,
                Math.Min(workArea.Width / AspectWidth, workArea.Height / AspectHeight));
            int minUnits = (int)Math.Ceiling(MinimumWindowWidth * _monitorScale / AspectWidth);
            int max = Math.Max(1, maxUnits) * AspectWidth;
            return (Math.Min(max, minUnits * AspectWidth), max);
        }

        private Rectangle SetInitialGeometry(Rectangle workArea, uint dpi)
        {
            _monitorScale = Math.Max(1f, dpi / 96f);
            var (min, max) = GetTrackWidths(workArea);
            _scaledWidth = SnapWindowWidth((int)MathF.Round(_sessionWindowWidth * _monitorScale), min, max);
            _scaledHeight = HeightForWidth(_scaledWidth);
            _dpiScale = _monitorScale;
            return new Rectangle(workArea.Left + (workArea.Width - _scaledWidth) / 2,
                workArea.Top + (workArea.Height - _scaledHeight) / 2, _scaledWidth, _scaledHeight);
        }

        private void RememberWindowSize()
        {
            if (_userSizing)
            {
                if (_scaledWidth != _sizingStartWidth)
                    _sessionWindowWidth = Math.Clamp(_scaledWidth / _monitorScale, MinimumWindowWidth, MaximumWindowWidth);
                _userSizing = false;
            }
        }

        private void CenterRestoredWindow()
        {
            var area = Screen.FromHandle(_hwnd).WorkingArea;
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, area.Left + (area.Width - _scaledWidth) / 2,
                area.Top + (area.Height - _scaledHeight) / 2, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        }

        private void RefreshWindowGeometry(bool dpiChanged = false)
        {
            if (!Win32.GetWindowRect(_hwnd, out var rect))
                return;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
                return;

            if (width == _scaledWidth && height == _scaledHeight && !dpiChanged)
                return;

            lock (_renderLock)
            {
                _scaledWidth = width;
                _scaledHeight = height;
                _dpiScale = _monitorScale;
                DisposeRenderBuffer();
            }

            ApplyRoundedRegion(_backdropHwnd);
            SyncBackdropToContent(keepZOrder: _dragMoving);
            Render(resizing: true);
        }

        private void ApplyMonitorDpi(IntPtr dpi, IntPtr suggestedRect)
        {
            _monitorScale = (Win32.Low32(dpi) & 0xffff) / 96f;
            var rect = Marshal.PtrToStructure<Win32.RECT>(suggestedRect);
            var workArea = Screen.FromRectangle(Rectangle.FromLTRB(
                rect.Left, rect.Top, rect.Right, rect.Bottom)).WorkingArea;
            var (min, max) = GetTrackWidths(workArea);
            int width = SnapWindowWidth(rect.Right - rect.Left, min, max);
            int height = HeightForWidth(width);
            int left = Math.Clamp(rect.Left, workArea.Left, workArea.Right - width);
            int top = Math.Clamp(rect.Top, workArea.Top, workArea.Bottom - height);

            Win32.SetWindowPos(_hwnd, IntPtr.Zero, left, top, width, height,
                Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
            RefreshWindowGeometry(dpiChanged: true);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public Win32.POINT Reserved;
            public Win32.POINT MaxSize;
            public Win32.POINT MaxPosition;
            public Win32.POINT MinTrackSize;
            public Win32.POINT MaxTrackSize;
        }

        private void ApplyTrackLimits(IntPtr hwnd, IntPtr data)
        {
            var (min, max) = GetTrackWidths(Screen.FromHandle(hwnd).WorkingArea);
            var info = Marshal.PtrToStructure<MinMaxInfo>(data);
            info.MinTrackSize = new Win32.POINT(min, HeightForWidth(min));
            info.MaxTrackSize = new Win32.POINT(max, HeightForWidth(max));
            Marshal.StructureToPtr(info, data, false);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowPosition
        {
            public IntPtr Window;
            public IntPtr InsertAfter;
            public int X;
            public int Y;
            public int Width;
            public int Height;
            public uint Flags;
        }

        private void ConstrainWindowPosition(IntPtr hwnd, IntPtr data)
        {
            var position = Marshal.PtrToStructure<WindowPosition>(data);
            if ((position.Flags & Win32.SWP_NOSIZE) != 0 || Win32.IsIconic(hwnd)
                || position.Width <= 0 || position.Height <= 0)
                return;

            var screen = (position.Flags & Win32.SWP_NOMOVE) != 0
                ? Screen.FromHandle(hwnd)
                : Screen.FromRectangle(new Rectangle(position.X, position.Y, position.Width, position.Height));
            var (min, max) = GetTrackWidths(screen.WorkingArea);
            int width = SnapWindowWidth(position.Width, min, max);
            int height = HeightForWidth(width);
            if (width == position.Width && height == position.Height)
                return;

            position.Width = width;
            position.Height = height;
            Marshal.StructureToPtr(position, data, false);
        }

        private void ConstrainSizingRect(IntPtr hwnd, int edge, IntPtr data)
        {
            if (!_userSizing)
            {
                _sizingStartWidth = _scaledWidth;
                _userSizing = true;
            }
            var rect = Marshal.PtrToStructure<Win32.RECT>(data);
            var (min, max) = GetTrackWidths(Screen.FromHandle(hwnd).WorkingArea);
            int width = rect.Right - rect.Left;
            int widthFromHeight = (int)Math.Round((rect.Bottom - rect.Top) * (double)AspectWidth / AspectHeight);
            bool fromLeft = edge is 1 or 4 or 7;
            bool fromTop = edge is 3 or 4 or 5;
            if (edge is 3 or 6)
                width = widthFromHeight;
            else if (edge is >= 4 and <= 8 and not 6)
                width = (width + widthFromHeight) / 2;

            width = SnapWindowWidth(width, min, max);
            int height = HeightForWidth(width);
            if (fromLeft)
                rect.Left = rect.Right - width;
            else
                rect.Right = rect.Left + width;
            if (fromTop)
                rect.Top = rect.Bottom - height;
            else
                rect.Bottom = rect.Top + height;

            Marshal.StructureToPtr(rect, data, false);
        }

        private bool TryHitResizeBorder(IntPtr hwnd, IntPtr coords, out int hit)
        {
            hit = Win32.HTCLIENT;
            if (!Win32.GetWindowRect(hwnd, out var bounds))
                return false;

            int packed = Win32.Low32(coords);
            int x = (short)(packed & 0xffff);
            int y = (short)(packed >> 16);
            int grip = (int)MathF.Ceiling(5f * _monitorScale);
            bool left = x < bounds.Left + grip;
            bool right = x >= bounds.Right - grip;

            // Win32 HTLEFT, HTRIGHT, HTTOP/HTBOTTOM and their corner variants.
            if (y < bounds.Top + grip)
                hit = left ? HtTopLeft : right ? HtTopRight : HtTop;
            else if (y >= bounds.Bottom - grip)
                hit = left ? HtBottomLeft : right ? HtBottomRight : HtBottom;
            else if (left || right)
                hit = left ? HtLeft : HtRight;

            return hit != Win32.HTCLIENT;
        }
    }
}

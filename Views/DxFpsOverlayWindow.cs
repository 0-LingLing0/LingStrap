using System;
using System.Runtime.InteropServices;
using Lingstrap.Models;
using Lingstrap.Services;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Color4 = Vortice.Mathematics.Color4;

namespace Lingstrap.Views;

/// <summary>
/// The FPS chip. Two earlier rendering approaches were tried and abandoned:
///
/// 1. A classic WPF layered window (AllowsTransparency, UpdateLayeredWindow driven by WPF's own
///    internal render loop) - click-through worked fine, but WPF's own compositing only pushed a
///    new frame roughly once a second over Roblox's flip-model rendering.
/// 2. A DirectComposition-hosted D3D11 swap chain (WS_EX_NOREDIRECTIONBITMAP) - fixed the update
///    rate (real bursts close to actual frame rate), but click-through never worked correctly for
///    this combination: WS_EX_TRANSPARENT, WM_NCHITTEST/HTTRANSPARENT, and SetWindowRgn (both fully
///    empty and 1x1) were all tried - the region-based attempts hid the window's rendering
///    entirely rather than just its hit-testing, and the rest never stopped blocking clicks. This
///    is a documented, known-hard combination, not something specific to this app.
///
/// This version goes back to WS_EX_LAYERED (proven, reliable click-through - the same mechanism
/// tooltips and cursors use) but pushes frames itself, directly and synchronously, via a raw
/// UpdateLayeredWindow call every time the FPS value changes, instead of relying on WPF's own
/// internal render loop to decide when to call it (that scheduling, not UpdateLayeredWindow itself,
/// was the likely bottleneck the first time around). Direct2D still renders the actual pill/text,
/// through an ID2D1DCRenderTarget bound to a GDI memory DC instead of a DXGI swap chain - same
/// drawing calls, different destination.
/// </summary>
public sealed class DxFpsOverlayWindow : IDisposable
{
    private const int WidthPx = 120;
    // Single-stat mode (just FPS, or just ping) keeps the original one-row pill height. With both
    // stats on, FPS keeps the larger/primary row on top and ping gets a shorter, secondary row
    // below it, inside the same pill - "the ping would be below the fps" per the request.
    private const int SingleRowHeight = 44;
    // Tighter than the single-row height so the two lines sit close together rather than each
    // floating in the middle of an oversized band - DrawText below centers each string vertically
    // within its own row height, so a taller band just means more empty space around the text.
    // Kept close to the actual glyph height at each font size (18pt / 13pt) rather than the
    // original even split, since that's what was leaving visible dead space between the two lines.
    private const int FpsRowHeight = 28;
    private const int PingRowHeight = 20;
    private const int Margin = 16;
    private const float CornerRadius = 10f;

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint GW_HWNDPREV = 3;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;
    private const uint ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const uint DIB_RGB_COLORS = 0;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    private static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, uint dwFlags);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);
    }

    private readonly WndProcDelegate _wndProc;
    private readonly IntPtr _robloxHwnd;
    private readonly bool _showFps;
    private readonly bool _showPing;
    private readonly int _heightPx;
    private IntPtr _hwnd;
    private IntPtr _memDc;
    private IntPtr _dibBitmap;
    private IntPtr _oldBitmap;

    private ID2D1DCRenderTarget _renderTarget = null!;
    private IDWriteTextFormat _primaryTextFormat = null!;
    private IDWriteTextFormat _secondaryTextFormat = null!;
    private ID2D1SolidColorBrush _textBrush = null!;
    private ID2D1SolidColorBrush _secondaryTextBrush = null!;
    private ID2D1SolidColorBrush _backgroundBrush = null!;
    private ID2D1SolidColorBrush _borderBrush = null!;

    private string _fpsText = "-- FPS";
    private string _pingText = "-- ms";
    private System.Windows.Threading.DispatcherTimer? _followTimer;
    private bool _isHidden;
    private System.Windows.Rect? _lastRobloxRect;
    private bool _wasRobloxForeground;

    /// <summary>showFps and showPing are fixed for the lifetime of this window - both come straight
    /// from settings read once when the detached watcher process starts, and that process's whole
    /// job for this Roblox session is done once it's showing something, so there's no need to handle
    /// either one flipping on/off while the window is already up.</summary>
    public DxFpsOverlayWindow(IntPtr robloxHwnd, bool showFps, bool showPing)
    {
        _robloxHwnd = robloxHwnd;
        _showFps = showFps;
        _showPing = showPing;
        _heightPx = showFps && showPing ? FpsRowHeight + PingRowHeight : SingleRowHeight;
        _wndProc = WndProc;
        CreateNativeWindow();
        InitializeGraphics();
        ApplyAppearance();
        Render();
        PlaceAboveRoblox();
        Native.ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
    }

    private void CreateNativeWindow()
    {
        var hInstance = Native.GetModuleHandle(null);
        var className = "LingstrapDxFpsOverlay_" + Guid.NewGuid().ToString("N");

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = className,
        };
        Native.RegisterClassEx(ref wc);

        // WS_EX_LAYERED, not WS_EX_NOREDIRECTIONBITMAP - proven, reliable click-through (the same
        // mechanism tooltips and custom cursors use), unlike the DirectComposition-hosted version
        // this replaces. No WS_VISIBLE here - UpdateLayeredWindow itself is what actually shows the
        // window's content; there's nothing to show until the first Render() call below.
        const uint exStyle = WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_LAYERED;
        _hwnd = Native.CreateWindowEx(exStyle, className, "Lingstrap FPS", WS_POPUP,
            Margin, Margin, WidthPx, _heightPx, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
    }

    /// <summary>Belt-and-suspenders alongside WS_EX_TRANSPARENT - answering WM_NCHITTEST directly
    /// is the standard extra step for click-through, and unlike the DirectComposition version, a
    /// plain WS_EX_LAYERED window actually honors this.</summary>
    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_NCHITTEST) return new IntPtr(HTTRANSPARENT);
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void InitializeGraphics()
    {
        // A 32bpp top-down (negative height) DIB section - CPU/GDI-accessible memory that both
        // Direct2D (via BindDC below) and UpdateLayeredWindow can read the same pixels from.
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = WidthPx,
            biHeight = -_heightPx,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };

        var screenDc = Native.GetDC(IntPtr.Zero);
        _memDc = Native.CreateCompatibleDC(screenDc);
        _dibBitmap = Native.CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS, out _, IntPtr.Zero, 0);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
        _oldBitmap = Native.SelectObject(_memDc, _dibBitmap);

        using var d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        var rtProps = new RenderTargetProperties(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied));
        _renderTarget = d2dFactory.CreateDCRenderTarget(rtProps);
        _renderTarget.BindDC(_memDc, new System.Drawing.Rectangle(0, 0, WidthPx, _heightPx));

        var dwriteFactory = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        _primaryTextFormat = dwriteFactory.CreateTextFormat("Segoe UI", FontWeight.Bold, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 18);
        _primaryTextFormat.TextAlignment = Vortice.DirectWrite.TextAlignment.Center;
        _primaryTextFormat.ParagraphAlignment = Vortice.DirectWrite.ParagraphAlignment.Center;

        // Smaller/dimmer - only used for the ping row when it's sharing the pill with FPS, so FPS
        // stays the visually primary stat and ping reads as a secondary detail below it.
        _secondaryTextFormat = dwriteFactory.CreateTextFormat("Segoe UI", FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 13);
        _secondaryTextFormat.TextAlignment = Vortice.DirectWrite.TextAlignment.Center;
        _secondaryTextFormat.ParagraphAlignment = Vortice.DirectWrite.ParagraphAlignment.Center;

        _textBrush = _renderTarget.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
        _secondaryTextBrush = _renderTarget.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 0.75f));
        _backgroundBrush = _renderTarget.CreateSolidColorBrush(new Color4(0f, 0f, 0f, 0.7f));
        _borderBrush = _renderTarget.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 0.15f));
    }

    /// <summary>Colors come straight from the current Appearance settings, same reasoning as the
    /// WPF version this replaces - this runs in the detached, elevated -fpswatcher process, which
    /// never merges WPF-UI's own theme resources.</summary>
    public void ApplyAppearance()
    {
        var accentTheme = SettingsService.Current.AccentTheme == ColorThemeCatalog.CustomThemeName
            ? ColorThemeCatalog.FromCustomColor(ColorThemeCatalog.ParseCustomColor(SettingsService.Current.CustomAccentColor))
            : ColorThemeCatalog.Find(SettingsService.Current.AccentTheme);
        var accent = accentTheme.Base;
        var isLight = SettingsService.Current.LightTheme;

        _textBrush.Color = isLight ? new Color4(0.106f, 0.106f, 0.122f, 1f) : new Color4(1f, 1f, 1f, 1f);
        _secondaryTextBrush.Color = isLight ? new Color4(0.106f, 0.106f, 0.122f, 0.7f) : new Color4(1f, 1f, 1f, 0.75f);
        _backgroundBrush.Color = isLight
            ? new Color4(1f, 1f, 1f, 0.85f)
            : new Color4(0.086f, 0.09f, 0.122f, 0.85f); // #16171F, same dark base the app itself uses
        _borderBrush.Color = new Color4(accent.R / 255f, accent.G / 255f, accent.B / 255f, 0.35f);
    }

    public void UpdateFps(double fps)
    {
        _fpsText = $"{Math.Round(fps)} FPS";
        Render();
    }

    /// <summary>null means the last ping attempt timed out/failed (e.g. the server host firewalls
    /// ICMP) rather than "no data yet" - both show the same placeholder since there's nothing more
    /// useful to say about either from here.</summary>
    public void UpdatePing(long? ms)
    {
        _pingText = ms.HasValue ? $"{ms} ms" : "-- ms";
        Render();
    }

    /// <summary>
    /// Renders with Direct2D exactly as before, then pushes the result to the screen directly via
    /// UpdateLayeredWindow - a synchronous, immediate push rather than waiting for WPF's own render
    /// loop or DWM's own compositing schedule to get around to it, which is what made both earlier
    /// approaches update too slowly over Roblox's flip-model rendering.
    /// </summary>
    private void Render()
    {
        _renderTarget.BeginDraw();
        _renderTarget.Clear(null); // fully transparent outside the pill - premultiplied alpha

        var pillRect = new RoundedRectangle
        {
            Rect = new Vortice.Mathematics.Rect(1, 1, WidthPx - 2, _heightPx - 2),
            RadiusX = CornerRadius,
            RadiusY = CornerRadius,
        };
        _renderTarget.FillRoundedRectangle(pillRect, _backgroundBrush);
        _renderTarget.DrawRoundedRectangle(pillRect, _borderBrush, 1f);

        if (_showFps && _showPing)
        {
            _renderTarget.DrawText(_fpsText, _primaryTextFormat, new Vortice.Mathematics.Rect(0, 0, WidthPx, FpsRowHeight), _textBrush);
            _renderTarget.DrawText(_pingText, _secondaryTextFormat, new Vortice.Mathematics.Rect(0, FpsRowHeight, WidthPx, PingRowHeight), _secondaryTextBrush);
        }
        else
        {
            var soloText = _showFps ? _fpsText : _pingText;
            _renderTarget.DrawText(soloText, _primaryTextFormat, new Vortice.Mathematics.Rect(0, 0, WidthPx, _heightPx), _textBrush);
        }

        _renderTarget.EndDraw();

        var size = new SIZE { cx = WidthPx, cy = _heightPx };
        var sourcePoint = new POINT { X = 0, Y = 0 };
        var destPoint = new POINT { X = (int)Left, Y = (int)Top };
        var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };

        Native.UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref destPoint, ref size, _memDc, ref sourcePoint, 0, ref blend, ULW_ALPHA);
    }

    private double Left, Top;

    /// <summary>Places this window directly above Roblox in the z-order (not Topmost, which would
    /// float it above the whole desktop) - the same technique OverlayBannerWindow uses. Re-asserted
    /// whenever Roblox's rect or foreground state changes (see StartFollowing) - restoring from
    /// minimized, moving, (borderless) fullscreen, and alt-tabbing a fullscreen Roblox can all bump
    /// it back above this window in the stacking order.</summary>
    private void PlaceAboveRoblox()
    {
        if (_robloxHwnd == IntPtr.Zero) return;
        var insertAfter = Native.GetWindow(_robloxHwnd, GW_HWNDPREV);
        Native.SetWindowPos(_hwnd, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Pins the chip to whichever corner of the Roblox window the user picked. Plain screen
    /// pixel coordinates - no WPF DIU/DPI conversion needed for a raw Win32 window. Doesn't move the
    /// actual window via SetWindowPos - UpdateLayeredWindow (see Render) both positions and paints
    /// it in one call, so the new position only visibly takes effect on the next render.</summary>
    public void Reposition(System.Windows.Rect robloxRect)
    {
        (Left, Top) = SettingsService.Current.FpsOverlayPosition switch
        {
            FpsOverlayPosition.TopLeft => (robloxRect.Left + Margin, robloxRect.Top + Margin),
            FpsOverlayPosition.TopRight => (robloxRect.Right - WidthPx - Margin, robloxRect.Top + Margin),
            FpsOverlayPosition.BottomLeft => (robloxRect.Left + Margin, robloxRect.Bottom - _heightPx - Margin),
            _ => (robloxRect.Right - WidthPx - Margin, robloxRect.Bottom - _heightPx - Margin),
        };
        Render();
    }

    /// <summary>
    /// Keeps the chip glued to the Roblox window's corner while both are visible, same as
    /// OverlayBannerWindow's own follow timer - reuses the WPF Dispatcher already running on this
    /// thread purely for its timer/thread-marshaling machinery, not for any rendering. Also hides
    /// this window whenever Roblox itself isn't currently visible (minimized) rather than leaving it
    /// floating alone at its last position.
    /// </summary>
    public void StartFollowing()
    {
        _followTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _followTimer.Tick += (_, _) =>
        {
            if (RobloxWindowLocator.GetClientRectPhysicalQuiet(_robloxHwnd) is { } rect)
            {
                var isForeground = Native.GetForegroundWindow() == _robloxHwnd;

                if (_isHidden)
                {
                    Native.ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
                    PlaceAboveRoblox();
                    _isHidden = false;
                }
                else if (_lastRobloxRect is not { } last || last != rect || isForeground != _wasRobloxForeground)
                {
                    PlaceAboveRoblox();
                }

                _lastRobloxRect = rect;
                _wasRobloxForeground = isForeground;
                Reposition(rect);
            }
            else if (!_isHidden)
            {
                Native.ShowWindow(_hwnd, SW_HIDE);
                _isHidden = true;
            }
        };
        _followTimer.Start();
    }

    public void Dispose()
    {
        _followTimer?.Stop();
        _borderBrush?.Dispose();
        _backgroundBrush?.Dispose();
        _secondaryTextBrush?.Dispose();
        _textBrush?.Dispose();
        _secondaryTextFormat?.Dispose();
        _primaryTextFormat?.Dispose();
        _renderTarget?.Dispose();
        if (_memDc != IntPtr.Zero)
        {
            Native.SelectObject(_memDc, _oldBitmap);
            Native.DeleteDC(_memDc);
        }
        if (_dibBitmap != IntPtr.Zero) Native.DeleteObject(_dibBitmap);
        if (_hwnd != IntPtr.Zero) Native.DestroyWindow(_hwnd);
    }
}

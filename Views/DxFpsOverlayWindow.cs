using System;
using System.Runtime.InteropServices;
using Lingstrap.Models;
using Lingstrap.Services;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Color4 = Vortice.Mathematics.Color4;

namespace Lingstrap.Views;

/// <summary>
/// The FPS chip, rendered through a real DirectX 11 swap chain hosted via DirectComposition rather
/// than a classic Win32/WPF layered window (AllowsTransparency, UpdateLayeredWindow). That distinction
/// matters: Roblox uses DXGI's hardware-accelerated "flip model" presentation (confirmed via the
/// CreateDirectFlipResource/DWMRedirection events RobloxFpsTracker logs while capturing its present
/// events), and a GDI-based layered window only got recomposited by DWM roughly once a second on top
/// of that - it isn't part of DWM's fast hardware compositor pipeline the way a proper
/// DirectComposition-hosted swap chain is. This is a plain Win32 window (not a WPF Window) since WPF
/// doesn't expose the WS_EX_NOREDIRECTIONBITMAP style DirectComposition needs - created on the
/// fpswatcher process's existing WPF Dispatcher thread, so its messages still get pumped by that same
/// thread's already-running message loop with no separate loop needed.
///
/// Update rate is real but not perfectly consistent: this measurably updates far faster than the
/// original layered-window version, in bursts close to real frame rate, but Windows' own scheduling
/// of when it recomposites this window over Roblox's flip-model rendering isn't fully controllable
/// from here - that ceiling is undocumented OS/DWM behavior, not something more app-side tuning has
/// reliably improved further (a wider, whole-client-area-covering variant was tried and didn't show a
/// repeatable improvement over this simpler chip-sized window).
/// </summary>
public sealed class DxFpsOverlayWindow : IDisposable
{
    private const int WidthPx = 120;
    private const int HeightPx = 44;
    private const int Margin = 16;
    private const float CornerRadius = 10f;

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint GW_HWNDPREV = 3;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

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

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);
    }

    private readonly WndProcDelegate _wndProc;
    private readonly IntPtr _robloxHwnd;
    private IntPtr _hwnd;

    private ID3D11Device _device = null!;
    private IDXGISwapChain1 _swapChain = null!;
    private IDCompositionDevice _dcompDevice = null!;
    private IDCompositionTarget _dcompTarget = null!;
    private IDCompositionVisual _dcompVisual = null!;
    private ID2D1RenderTarget _renderTarget = null!;
    private IDWriteTextFormat _textFormat = null!;
    private ID2D1SolidColorBrush _textBrush = null!;
    private ID2D1SolidColorBrush _backgroundBrush = null!;
    private ID2D1SolidColorBrush _borderBrush = null!;

    private string _currentText = "-- FPS";
    private System.Windows.Threading.DispatcherTimer? _followTimer;
    private bool _isHidden;
    private System.Windows.Rect? _lastRobloxRect;
    private bool _wasRobloxForeground;

    public DxFpsOverlayWindow(IntPtr robloxHwnd)
    {
        _robloxHwnd = robloxHwnd;
        _wndProc = WndProc;
        CreateNativeWindow();
        InitializeGraphics();
        ApplyAppearance();
        Render();
        PlaceAboveRoblox();
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

        const uint exStyle = WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP;
        _hwnd = Native.CreateWindowEx(exStyle, className, "Lingstrap FPS", WS_POPUP | WS_VISIBLE,
            Margin, Margin, WidthPx, HeightPx, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        Native.DefWindowProc(hWnd, msg, wParam, lParam);

    private void InitializeGraphics()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_1, Vortice.Direct3D.FeatureLevel.Level_10_0 },
            out _device).CheckError();

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var swapChainDesc = new SwapChainDescription1
        {
            Width = WidthPx,
            Height = HeightPx,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
        };
        _swapChain = factory.CreateSwapChainForComposition(_device, swapChainDesc, null);

        DComp.DCompositionCreateDevice(dxgiDevice, out _dcompDevice).CheckError();
        _dcompDevice.CreateTargetForHwnd(_hwnd, true, out _dcompTarget).CheckError();
        _dcompDevice.CreateVisual(out _dcompVisual).CheckError();
        _dcompVisual.SetContent(_swapChain);
        _dcompTarget.SetRoot(_dcompVisual);
        _dcompDevice.Commit();

        using var d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        using var surface = _swapChain.GetBuffer<IDXGISurface>(0);
        var rtProps = new RenderTargetProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied));
        _renderTarget = d2dFactory.CreateDxgiSurfaceRenderTarget(surface, rtProps);

        var dwriteFactory = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        _textFormat = dwriteFactory.CreateTextFormat("Segoe UI", FontWeight.Bold, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 18);
        _textFormat.TextAlignment = Vortice.DirectWrite.TextAlignment.Center;
        _textFormat.ParagraphAlignment = Vortice.DirectWrite.ParagraphAlignment.Center;

        _textBrush = _renderTarget.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
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

        // The number itself is plain white/near-black (not the accent) - against a background as
        // busy and varied as a live game, an accent-colored number was hard to read at a glance;
        // white (or near-black on the light theme) keeps solid contrast against the pill's own
        // fixed dark/light background regardless of what's behind the overlay. The accent still
        // comes through in the pill's border, so it isn't lost, just no longer carried by the text.
        _textBrush.Color = isLight ? new Color4(0.106f, 0.106f, 0.122f, 1f) : new Color4(1f, 1f, 1f, 1f);
        _backgroundBrush.Color = isLight
            ? new Color4(1f, 1f, 1f, 0.85f)
            : new Color4(0.086f, 0.09f, 0.122f, 0.85f); // #16171F, same dark base the app itself uses
        _borderBrush.Color = new Color4(accent.R / 255f, accent.G / 255f, accent.B / 255f, 0.35f);
    }

    public void UpdateFps(double fps)
    {
        _currentText = $"{Math.Round(fps)} FPS";
        Render();
    }

    private void Render()
    {
        _renderTarget.BeginDraw();
        _renderTarget.Clear(null); // fully transparent outside the pill - premultiplied alpha

        var pillRect = new Vortice.Direct2D1.RoundedRectangle
        {
            Rect = new Vortice.Mathematics.Rect(1, 1, WidthPx - 2, HeightPx - 2),
            RadiusX = CornerRadius,
            RadiusY = CornerRadius,
        };
        _renderTarget.FillRoundedRectangle(pillRect, _backgroundBrush);
        _renderTarget.DrawRoundedRectangle(pillRect, _borderBrush, 1f);
        _renderTarget.DrawText(_currentText, _textFormat, new Vortice.Mathematics.Rect(0, 0, WidthPx, HeightPx), _textBrush);

        _renderTarget.EndDraw();
        // Sync interval 0 - don't block this thread waiting for DWM's next composition opportunity.
        _swapChain.Present(0, PresentFlags.None);
    }

    /// <summary>
    /// Places this window directly above Roblox in the z-order (not Topmost, which would float it
    /// above the whole desktop) - the same technique OverlayBannerWindow uses. This needs to be
    /// re-asserted continuously (see StartFollowing below), not just once at creation - restoring
    /// Roblox from being minimized, or it entering (borderless) fullscreen, both change the overall
    /// window stacking order and would otherwise silently leave this window buried behind Roblox
    /// again with no error or event to react to.
    /// </summary>
    private void PlaceAboveRoblox()
    {
        if (_robloxHwnd == IntPtr.Zero) return;
        var insertAfter = Native.GetWindow(_robloxHwnd, GW_HWNDPREV);
        Native.SetWindowPos(_hwnd, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Pins the chip to whichever corner of the Roblox window the user picked. Plain screen
    /// pixel coordinates - no WPF DIU/DPI conversion needed for a raw Win32 window.</summary>
    public void Reposition(System.Windows.Rect robloxRect)
    {
        var (x, y) = SettingsService.Current.FpsOverlayPosition switch
        {
            FpsOverlayPosition.TopLeft => (robloxRect.Left + Margin, robloxRect.Top + Margin),
            FpsOverlayPosition.TopRight => (robloxRect.Right - WidthPx - Margin, robloxRect.Top + Margin),
            FpsOverlayPosition.BottomLeft => (robloxRect.Left + Margin, robloxRect.Bottom - HeightPx - Margin),
            _ => (robloxRect.Right - WidthPx - Margin, robloxRect.Bottom - HeightPx - Margin),
        };
        Native.SetWindowPos(_hwnd, IntPtr.Zero, (int)x, (int)y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Keeps the chip glued to the Roblox window's corner while both are visible, same as
    /// OverlayBannerWindow's own follow timer - reuses the WPF Dispatcher already running on this
    /// thread purely for its timer/thread-marshaling machinery, not for any rendering. Also hides
    /// this window whenever Roblox itself isn't currently visible (minimized) rather than leaving it
    /// floating alone at its last position, and re-asserts z-order (see PlaceAboveRoblox) whenever
    /// Roblox's rect changes OR its foreground/focus state changes - restoring from minimized,
    /// moving the window, entering/leaving (borderless) fullscreen, and alt-tabbing away from and
    /// back to a fullscreen Roblox can all bump it back above this window in the stacking order, and
    /// the focus-only case in particular changes neither position nor size, so the rect check alone
    /// doesn't catch it. Deliberately NOT done unconditionally on every tick either way - that was
    /// tried first and made Roblox's own fullscreen-detection logic think a window kept appearing
    /// above it over and over, 60 times a second, which made ROBLOX ITSELF visibly flicker in and
    /// out of fullscreen on its own. Only re-asserting on an actual change keeps this to real,
    /// occasional events instead of a continuous fight over z-order.
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
        _textBrush?.Dispose();
        _textFormat?.Dispose();
        _renderTarget?.Dispose();
        _dcompVisual?.Dispose();
        _dcompTarget?.Dispose();
        _dcompDevice?.Dispose();
        _swapChain?.Dispose();
        _device?.Dispose();
        if (_hwnd != IntPtr.Zero) Native.DestroyWindow(_hwnd);
    }
}

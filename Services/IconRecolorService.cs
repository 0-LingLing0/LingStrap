using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lingstrap.Services;

/// <summary>
/// Recolors the app's badge icon (a purple gradient rounded-square with a white "L" glyph) to match
/// whichever accent the user picked, so the logo doesn't stay stuck on the original violet everywhere
/// it appears (title bar, loading screen) while the rest of the app follows the chosen theme.
/// Computed once per process rather than live: a theme change already triggers a full app restart
/// (see AppearanceView), so there's never a need to re-recolor an already-running instance.
/// </summary>
public static class IconRecolorService
{
    private static BitmapSource? _cached;
    private static Color _cachedColor;

    public static BitmapSource GetIcon(Color accent)
    {
        if (_cached != null && _cachedColor == accent) return _cached;

        var source = new BitmapImage(new Uri("pack://application:,,,/lingstrap.png"));
        _cached = Recolor(source, accent);
        _cachedColor = accent;
        return _cached;
    }

    /// <summary>
    /// Only pixels with real saturation get re-hued - that's the purple badge, at every shade its
    /// gradient uses. Near-grey pixels (the white "L" glyph, and anti-aliased edges close to it) are
    /// left alone so the glyph keeps its own contrast against whatever color the badge becomes.
    /// Original lightness is preserved per pixel so the gradient's shading survives the recolor.
    /// </summary>
    private static BitmapSource Recolor(BitmapSource source, Color target)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[height * stride];
        converted.CopyPixels(pixels, stride, 0);

        var (targetHue, targetSat, _) = RgbToHsl(target.R, target.G, target.B);

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (a == 0) continue;

            var b = pixels[i];
            var g = pixels[i + 1];
            var r = pixels[i + 2];

            var (_, sat, lightness) = RgbToHsl(r, g, b);
            if (sat < 0.15) continue;

            var (nr, ng, nb) = HslToRgb(targetHue, targetSat, lightness);
            pixels[i] = nb;
            pixels[i + 1] = ng;
            pixels[i + 2] = nr;
        }

        var result = new WriteableBitmap(width, height, source.DpiX, source.DpiY, PixelFormats.Bgra32, null);
        result.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, stride, 0);
        result.Freeze();
        return result;
    }

    /// <summary>Shared with ColorThemeCatalog's custom-color Light/Dark derivation - internal rather
    /// than private so that code doesn't need to duplicate this math.</summary>
    internal static (double Hue, double Sat, double Lightness) RgbToHsl(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;

        if (max == min) return (0, 0, l);

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);

        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h /= 6;

        return (h, s, l);
    }

    internal static (byte R, byte G, byte B) HslToRgb(double h, double s, double l)
    {
        if (s == 0)
        {
            var v = (byte)Math.Round(l * 255);
            return (v, v, v);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;

        return (
            (byte)Math.Round(HueToRgb(p, q, h + 1.0 / 3) * 255),
            (byte)Math.Round(HueToRgb(p, q, h) * 255),
            (byte)Math.Round(HueToRgb(p, q, h - 1.0 / 3) * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}

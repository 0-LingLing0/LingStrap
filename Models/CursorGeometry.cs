using SixLabors.ImageSharp;

namespace Lingstrap.Models;

/// <summary>
/// The real shape of one of Roblox's cursor PNGs: the full canvas size, and the rectangle within
/// it that actually holds visible (non-transparent) pixels. Roblox's own cursor files pad their
/// canvas well beyond the visible glyph (e.g. a 64x64 canvas holding only a 17x24 arrow) - a
/// custom cursor has to match that same canvas-vs-content proportion, not just fill the canvas.
/// </summary>
public record CursorGeometry(Size CanvasSize, Rectangle ContentBounds);

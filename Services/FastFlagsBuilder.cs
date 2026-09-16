using System.Collections.Generic;
using System.Linq;
using Lingstrap.Models;

namespace Lingstrap.Services;

/// <summary>Turns the editor's flag list plus the renderer choice into what actually gets written to disk.</summary>
public static class FastFlagsBuilder
{
    public static Dictionary<string, string> BuildEffectiveFlags()
    {
        var s = SettingsService.Current;
        var flags = new Dictionary<string, string>();

        foreach (var entry in s.CustomFlags)
        {
            if (!entry.Enabled || string.IsNullOrWhiteSpace(entry.Name)) continue;
            if (FastFlagCatalog.RendererFlagNames.Contains(entry.Name)) continue; // the renderer combo owns these
            flags[entry.Name] = entry.Value;
        }

        var activeRenderer = s.Renderer switch
        {
            "Vulkan" => FastFlagCatalog.RendererVulkan,
            "OpenGL" => FastFlagCatalog.RendererOpenGL,
            _        => FastFlagCatalog.RendererD3D11,
        };
        flags[activeRenderer] = "true";

        return flags;
    }
}

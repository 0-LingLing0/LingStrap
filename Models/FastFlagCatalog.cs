using System.Collections.Generic;
using System.Linq;

namespace Lingstrap.Models;

public enum FlagKind { Bool, Int }

public record FlagDefinition(string Name, FlagKind Kind, int? Min = null, int? Max = null, bool IsRenderer = false, string Description = "");

/// <summary>
/// Roblox silently ignores every FastFlag name except the ones it still reads. These 18 are
/// the only ones worth exposing - anything else typed into the editor gets a warning, not a block.
/// </summary>
public static class FastFlagCatalog
{
    public const string RendererD3D11  = "FFlagDebugGraphicsPreferD3D11";
    public const string RendererVulkan = "FFlagDebugGraphicsPreferVulkan";
    public const string RendererOpenGL = "FFlagDebugGraphicsPreferOpenGL";

    public const string RenderQuality         = "DFIntDebugFRMQualityLevelOverride";
    public const string TextureQualityEnabled = "DFFlagTextureQualityOverrideEnabled";
    public const string TextureQuality        = "DFIntTextureQualityOverride";
    public const string AntiAliasing          = "FIntDebugForceMSAASamples";
    public const string GrassMinDistance      = "FIntFRMMinGrassDistance";
    public const string GrassMaxDistance      = "FIntFRMMaxGrassDistance";
    public const string GrassMovement         = "FIntGrassMovementReducedMotionFactor";
    public const string FreezeVoxelLighting   = "DFFlagDebugPauseVoxelizer";
    public const string GreySky               = "FFlagDebugSkyGray";
    public const string DisableDpiScaling     = "DFFlagDisableDPIScale";
    public const string LodBase               = "DFIntCSGLevelOfDetailSwitchingDistance";
    public const string Lod12                 = "DFIntCSGLevelOfDetailSwitchingDistanceL12";
    public const string Lod23                 = "DFIntCSGLevelOfDetailSwitchingDistanceL23";
    public const string Lod34                 = "DFIntCSGLevelOfDetailSwitchingDistanceL34";
    public const string AltEnterFullscreen    = "FFlagHandleAltEnterFullscreenManually";

    public static readonly IReadOnlyList<FlagDefinition> Known = new[]
    {
        new FlagDefinition(RenderQuality, FlagKind.Int, 1, 21,
            Description: "Overrides Roblox's automatic quality slider. 1 is lowest, 21 is highest."),
        new FlagDefinition(TextureQualityEnabled, FlagKind.Bool,
            Description: "Turns on the manual texture quality override below."),
        new FlagDefinition(TextureQuality, FlagKind.Int, 0, 3,
            Description: "Texture detail level. 0 is lowest, 3 is highest."),
        new FlagDefinition(AntiAliasing, FlagKind.Int, 0, 4,
            Description: "Forces this many MSAA samples for anti-aliasing. 0 turns it off."),
        new FlagDefinition(GrassMinDistance, FlagKind.Int,
            Description: "Closest distance (studs) at which grass starts rendering."),
        new FlagDefinition(GrassMaxDistance, FlagKind.Int,
            Description: "Furthest distance (studs) grass still renders at."),
        new FlagDefinition(GrassMovement, FlagKind.Int,
            Description: "How much grass sways in the wind - higher reduces the movement."),
        new FlagDefinition(FreezeVoxelLighting, FlagKind.Bool,
            Description: "Stops Roblox from recalculating voxel lighting, trading accuracy for performance."),
        new FlagDefinition(GreySky, FlagKind.Bool,
            Description: "Replaces the sky with flat grey, removing the skybox's rendering cost."),
        new FlagDefinition(DisableDpiScaling, FlagKind.Bool,
            Description: "Renders at your monitor's native pixel scale instead of Windows' DPI scaling."),
        new FlagDefinition(RendererD3D11, FlagKind.Bool, IsRenderer: true,
            Description: "Forces the DirectX 11 renderer."),
        new FlagDefinition(RendererVulkan, FlagKind.Bool, IsRenderer: true,
            Description: "Forces the Vulkan renderer."),
        new FlagDefinition(RendererOpenGL, FlagKind.Bool, IsRenderer: true,
            Description: "Forces the OpenGL renderer."),
        new FlagDefinition(LodBase, FlagKind.Int,
            Description: "Distance at which CSG parts first drop to a lower level of detail."),
        new FlagDefinition(Lod12, FlagKind.Int,
            Description: "Distance for the next level-of-detail step down."),
        new FlagDefinition(Lod23, FlagKind.Int,
            Description: "Distance for the level-of-detail step after that."),
        new FlagDefinition(Lod34, FlagKind.Int,
            Description: "Distance for the furthest level-of-detail step."),
        new FlagDefinition(AltEnterFullscreen, FlagKind.Bool,
            Description: "Lets Alt+Enter toggle fullscreen instead of Roblox's own handling."),
    };

    public static readonly IReadOnlyList<string> RendererFlagNames = new[]
    {
        RendererD3D11, RendererVulkan, RendererOpenGL
    };

    public static bool IsKnown(string name) => Known.Any(f => f.Name == name);
    public static FlagDefinition? Find(string name) => Known.FirstOrDefault(f => f.Name == name);
}

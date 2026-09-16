using System;

namespace Lingstrap.Services;

/// <summary>A specific, user-facing reason a Roblox install/update failed - never just "could not install".</summary>
public class RobloxInstallException : Exception
{
    public RobloxInstallException(string message) : base(message) { }
}

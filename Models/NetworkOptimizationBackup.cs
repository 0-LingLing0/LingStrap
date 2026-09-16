using System.Collections.Generic;

namespace Lingstrap.Models;

/// <summary>
/// Exactly what NetworkOptimizationService changed on this machine, captured before each change so
/// "Restore" can put every value back rather than guessing at a generic Windows default - some of
/// these (adapter driver properties especially) vary enough by hardware/vendor that a hardcoded
/// "restore to X" would be wrong on plenty of machines.
/// </summary>
public class NetworkOptimizationBackup
{
    public List<AdapterPowerBackup> AdapterPower { get; set; } = new();
    public RegistryValueBackup? NetworkThrottling { get; set; }
    public RegistryValueBackup? QosReservedBandwidth { get; set; }
    public RegistryValueBackup? DeliveryOptimization { get; set; }
    public List<AdapterParamBackup> AdapterParams { get; set; } = new();
}

public class AdapterPowerBackup
{
    public string InstanceName { get; set; } = "";
    public bool OriginalEnable { get; set; }
}

/// <summary>A single DWORD registry value - <see cref="Existed"/> tells Restore whether to put
/// <see cref="OriginalValue"/> back or delete the value entirely (it didn't exist before Lingstrap
/// created it, so the true "restore" is for it to not exist).</summary>
public class RegistryValueBackup
{
    public bool Existed { get; set; }
    public int OriginalValue { get; set; }
}

public class AdapterParamBackup
{
    public string SubKeyName { get; set; } = "";
    public string ParamName { get; set; } = "";
    public string OriginalValue { get; set; } = "";
    public string Description { get; set; } = "";
}

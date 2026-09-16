using System.Linq;
using Lingstrap.Models;

namespace Lingstrap.Services;

/// <summary>
/// Shared read/write access to SettingsService.Current.CustomFlags, used by both the friendly
/// FastFlags page and the raw FastFlag Editor page so a change on one shows on the other.
/// </summary>
public static class FastFlagStore
{
    public static FastFlagEntry? Find(string name) =>
        SettingsService.Current.CustomFlags.FirstOrDefault(f => f.Name == name);

    public static void Set(string name, string value)
    {
        var entry = Find(name);
        if (entry != null)
        {
            entry.Value = value;
            entry.Enabled = true;
        }
        else
        {
            SettingsService.Current.CustomFlags.Add(new FastFlagEntry { Name = name, Value = value, Enabled = true });
        }

        PresetService.MarkCustom();
        SettingsService.Save();
    }

    public static void Remove(string name)
    {
        if (SettingsService.Current.CustomFlags.RemoveAll(f => f.Name == name) > 0)
        {
            PresetService.MarkCustom();
            SettingsService.Save();
        }
    }
}

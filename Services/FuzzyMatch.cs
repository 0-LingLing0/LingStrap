using System;

namespace Lingstrap.Services;

/// <summary>
/// Subsequence-based fuzzy matching for search boxes - the query's characters just need to appear
/// in order somewhere in the target (case-insensitive), not as one contiguous substring. Lets
/// "drqo" find "DFIntDebugFRMQualityLevelOverride" the way VS Code's Ctrl+P search does, instead of
/// requiring an exact fragment.
/// </summary>
public static class FuzzyMatch
{
    public static bool IsMatch(string target, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        if (string.IsNullOrEmpty(target)) return false;

        var queryIndex = 0;
        foreach (var c in target)
        {
            if (queryIndex >= query.Length) break;
            if (char.ToLowerInvariant(c) == char.ToLowerInvariant(query[queryIndex]))
                queryIndex++;
        }
        return queryIndex == query.Length;
    }
}

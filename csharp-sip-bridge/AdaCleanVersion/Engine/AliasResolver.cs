using AdaCleanVersion.Models;

namespace AdaCleanVersion.Engine;

/// <summary>
/// Resolves address aliases ("home", "work", "mum's", etc.) from a returning caller's
/// stored alias dictionary. Only applies to address slots (pickup, destination).
/// </summary>
public static class AliasResolver
{
    private static readonly HashSet<string> AddressSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        "pickup", "destination"
    };

    /// <summary>
    /// If the transcript matches a known alias for an address slot, returns the resolved address.
    /// Otherwise returns null (caller said a real address, not an alias).
    /// </summary>
    public static AliasMatch? TryResolve(string slotName, string transcript, CallerContext? context)
    {
        // Only resolve aliases for address slots
        if (!AddressSlots.Contains(slotName))
            return null;

        var aliases = context?.AddressAliases;
        if (aliases == null || aliases.Count == 0)
            return null;

        var cleaned = transcript.Trim();

        // Direct match: caller said exactly "home", "work", etc.
        foreach (var (alias, address) in aliases)
        {
            if (string.Equals(cleaned, alias, StringComparison.OrdinalIgnoreCase))
                return new AliasMatch(alias, address);
        }

        // Phrase match: "from home", "to work", "my home", "the usual", "go home"
        var lower = cleaned.ToLowerInvariant();
        foreach (var (alias, address) in aliases)
        {
            var aliasLower = alias.ToLowerInvariant();

            // Common speech patterns around alias keywords
            if (lower == aliasLower ||
                lower == $"from {aliasLower}" ||
                lower == $"to {aliasLower}" ||
                lower == $"my {aliasLower}" ||
                lower == $"go to {aliasLower}" ||
                lower == $"take me {aliasLower}" ||
                lower == $"take me to {aliasLower}" ||
                lower == $"{aliasLower} please" ||
                lower == $"the {aliasLower}" ||
                lower.StartsWith($"{aliasLower} ") ||
                lower.EndsWith($" {aliasLower}"))
            {
                return new AliasMatch(alias, address);
            }
        }

        // "the usual" / "same as last time" → resolve to last pickup or last destination
        if (IsUsualPhrase(lower))
        {
            var fallback = slotName.Equals("pickup", StringComparison.OrdinalIgnoreCase)
                ? context?.LastPickup
                : context?.LastDestination;

            if (!string.IsNullOrWhiteSpace(fallback))
                return new AliasMatch("usual", fallback!);
        }

        return null;
    }

    private static bool IsUsualPhrase(string lower) =>
        lower is "the usual" or "usual" or "same as last time" or "same place" or
                 "same as before" or "the same" or "usual place";
}

/// <summary>Result of a successful alias resolution.</summary>
public sealed record AliasMatch(string AliasName, string ResolvedAddress);

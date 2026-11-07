using System.Text.Json;

namespace FMSC_SMS.Core;

internal sealed record PermissionDefinition(string Code, string Name, string Description);

/// <summary>The permissions implemented by the application and migration 3.</summary>
// BD-32: preserve Administrator authority and granted permissions across screens, shortcuts and direct calls.
// Menu visibility is not authorization; writers still validate the current actor.
internal static class PermissionCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<string, PermissionDefinition>> Definitions = new(() => {
        using var stream = typeof(PermissionCatalog).Assembly.GetManifestResourceStream("FMSC_SMS.Core.PermissionCatalog.json")
            ?? throw new InvalidDataException("The application's permission catalog is missing.");
        var entries = JsonSerializer.Deserialize<PermissionDefinition[]>(stream)
            ?? throw new InvalidDataException("The application's permission catalog is invalid.");
        return entries.ToDictionary(entry => entry.Code, StringComparer.OrdinalIgnoreCase);
    });
    internal static IReadOnlyDictionary<string, PermissionDefinition> All => Definitions.Value;
    internal static bool Contains(string code) => All.ContainsKey(code);
    internal static bool IsSupportedCatalog(IEnumerable<string> codes)
    {
        var actual = codes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return actual.SetEquals(All.Keys);
    }
}

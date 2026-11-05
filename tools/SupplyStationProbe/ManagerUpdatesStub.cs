namespace SpirePatch;

// The probe compiles the exact production HTTP client source in an isolated console host.
// Its assembly version is set from this project's Version property.
internal static class ManagerUpdates
{
    internal static string CurrentVersion =>
        typeof(ManagerUpdates).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}

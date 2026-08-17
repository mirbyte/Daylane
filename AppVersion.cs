using System.Reflection;

namespace Daylane;

internal static class AppVersion
{
    public static string Value =>
        typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? "0.0.0";

    public static string Display => $"v{Value}";

    public static string WindowTitle => $"Daylane {Display}";
}

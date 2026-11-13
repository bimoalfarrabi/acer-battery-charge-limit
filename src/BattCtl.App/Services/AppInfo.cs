using System.Reflection;

namespace BattCtl.App.Services;

public static class AppInfo
{
    private static readonly Lazy<string?> _cachedInformational = new(() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>
    /// The informational version with any <c>+metadata</c> suffix stripped,
    /// e.g. "1.2.0" or "1.2.0-rc.1". Falls back to the assembly's numeric
    /// version when no informational version is present.
    /// </summary>
    public static string Version
    {
        get
        {
            var informational = _cachedInformational.Value;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                return plus >= 0 ? informational[..plus] : informational;
            }

            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        }
    }

    /// <summary>
    /// The numeric core of <see cref="Version"/>, or null when it is not
    /// parseable (handles "1.2.0" and "1.2.0-rc.1").
    /// </summary>
    public static Version? SemanticVersion
    {
        get
        {
            var text = Version;
            if (text is "unknown")
            {
                return null;
            }

            // Drop a prerelease suffix before parsing; System.Version cannot.
            var core = text.Split('-', 2)[0];
            return System.Version.TryParse(core, out var parsed) ? parsed : null;
        }
    }
}

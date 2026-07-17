using System;
using System.Reflection;

namespace TeronWoWLauncher.Services.Core;

/// <summary>
/// Product/version identity read live from assembly metadata so the visible name and version
/// can never drift from the .csproj. The title bar shows "&lt;DisplayName&gt; v&lt;Version&gt;".
/// </summary>
public static class AppInfo
{
    /// <summary>
    /// Product display name without the "(ABBR)" suffix, e.g. "Teron WoW Launcher".
    /// Read from <see cref="AssemblyProductAttribute"/> (&lt;Product&gt; in the csproj).
    /// </summary>
    public static string DisplayName
    {
        get
        {
            var product = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyProductAttribute>()?.Product;
            if (string.IsNullOrWhiteSpace(product))
            {
                return "Teron WoW Launcher";
            }

            // Drop the trailing "(ABBR)" for the visible title; it stays in <Product> for docs.
            var idx = product.IndexOf(" (", StringComparison.Ordinal);
            return idx >= 0 ? product[..idx] : product;
        }
    }

    /// <summary>
    /// Version exactly as written in &lt;Version&gt;, read from
    /// <see cref="AssemblyInformationalVersionAttribute"/> (not AssemblyVersion, which the CLR
    /// pads to four numeric parts).
    /// </summary>
    public static string Version
    {
        get
        {
            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(info))
            {
                return "0.0.0";
            }

            // Defensive: strip any "+metadata" if the source-revision suffix was ever re-enabled.
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
    }

    /// <summary>e.g. "Teron WoW Launcher v0.1.0"</summary>
    public static string DisplayNameWithVersion => $"{DisplayName} v{Version}";
}

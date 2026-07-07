using System.Diagnostics;
using System.IO;
using TeronWoWLauncher.Models;

namespace TeronWoWLauncher.Services;

/// <summary>
/// Reads a DLL's own Win32 version resource (the same data Explorer's file-properties "Details" tab
/// shows) so the DLL lists can show more than just a filename. Best-effort only: plenty of small,
/// hand-built mod DLLs carry no version resource at all, in which case the fields are simply left
/// null rather than treated as an error.
/// </summary>
public static class DllMetadataReader
{
    public static DllInfo Read(string name, string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return new DllInfo { Name = name };
        }

        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(fullPath);
            return new DllInfo
            {
                Name = name,
                Version = FirstNonBlank(info.FileVersion, info.ProductVersion),
                Author = Trimmed(info.CompanyName),
                Description = Trimmed(info.FileDescription),
            };
        }
        catch
        {
            return new DllInfo { Name = name };
        }
    }

    private static string? FirstNonBlank(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) ? a.Trim() : Trimmed(b);

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

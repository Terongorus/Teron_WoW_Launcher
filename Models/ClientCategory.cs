namespace TeronWoWLauncher.Models;

/// <summary>
/// Which family of executable patching a <see cref="ClientProfile"/> needs. Two values only - the
/// specific server (Kronos, OctoWoW, some other private server) is a separate concern carried by the
/// profile itself; this only decides which patch catalog applies (see
/// <see cref="Services.Patching.PatchCatalog.For"/>) and how version-up-to-date checks behave (see
/// <see cref="Services.Launch.GameInstallService.IsUpToDate"/>).
/// </summary>
public enum ClientCategory
{
    /// <summary>Stock, unmodified 1.12.1 client (build 5875) - every executable tweak starts from a
    /// genuinely pristine baseline.</summary>
    Vanilla,

    /// <summary>
    /// A client already built on top of vanilla by a third-party community patcher (the same class of
    /// tool this launcher's own signature-removal/tweak offsets were reverse-engineered from) - ships
    /// several QoL tweaks already applied natively. Confirmed byte-identical to vanilla 5875 in file
    /// layout for both TurtleWoW and OctoWoW; see PatchCatalog's own notes for exactly what's assumed
    /// pre-applied and why.
    /// </summary>
    VanillaPlus,
}

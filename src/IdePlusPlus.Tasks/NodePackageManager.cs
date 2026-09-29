namespace IdePlusPlus.Tasks;

public enum NodePackageManager
{
    Npm,
    Pnpm,
    Yarn,
}

public static class NodePackageManagerExtensions
{
    /// <summary>
    /// The command name to look for on PATH. Note this is the name only, without an extension: on
    /// Windows every one of these ships as a <c>.cmd</c> shim rather than an <c>.exe</c>, which is
    /// why <see cref="NodeToolLocator"/> exists rather than handing the bare name to
    /// <see cref="System.Diagnostics.ProcessStartInfo"/>.
    /// </summary>
    public static string ExecutableName(this NodePackageManager packageManager) => packageManager switch
    {
        NodePackageManager.Pnpm => "pnpm",
        NodePackageManager.Yarn => "yarn",
        _ => "npm",
    };
}

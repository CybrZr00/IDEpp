namespace IdePlusPlus.Tasks;

/// <summary>
/// Works out which package manager a manifest's scripts should be run with. The corepack
/// <c>packageManager</c> field wins when present - it is the repo explicitly declaring an answer -
/// otherwise the nearest lockfile at or above the manifest decides.
///
/// The walk upwards matters for workspaces: in a pnpm/yarn monorepo only the repo root has a
/// lockfile, so a package in <c>packages/web</c> would otherwise be misread as npm and run with the
/// wrong tool.
/// </summary>
public static class PackageManagerDetector
{
    private const string PnpmLockFileName = "pnpm-lock.yaml";
    private const string YarnLockFileName = "yarn.lock";
    private const string NpmLockFileName = "package-lock.json";

    public static NodePackageManager Detect(string manifestPath, string? packageManagerField) =>
        FromPackageManagerField(packageManagerField)
        ?? FromNearestLockfile(manifestPath)
        ?? NodePackageManager.Npm;

    /// <summary>Parses corepack's <c>"pnpm@8.6.0"</c> form. The version suffix is ignored - IDE++
    /// runs whatever is installed, it does not provision a specific version the way corepack does.</summary>
    private static NodePackageManager? FromPackageManagerField(string? packageManagerField)
    {
        if (string.IsNullOrWhiteSpace(packageManagerField))
        {
            return null;
        }

        var name = packageManagerField.Split('@', 2)[0].Trim();
        return name switch
        {
            _ when name.Equals("pnpm", StringComparison.OrdinalIgnoreCase) => NodePackageManager.Pnpm,
            _ when name.Equals("yarn", StringComparison.OrdinalIgnoreCase) => NodePackageManager.Yarn,
            _ when name.Equals("npm", StringComparison.OrdinalIgnoreCase) => NodePackageManager.Npm,
            _ => null,
        };
    }

    private static NodePackageManager? FromNearestLockfile(string manifestPath)
    {
        var directory = Path.GetDirectoryName(manifestPath);
        while (!string.IsNullOrEmpty(directory))
        {
            // Checked in a fixed order so a directory holding more than one lockfile (a repo
            // mid-migration between managers) always resolves the same way.
            if (File.Exists(Path.Combine(directory, PnpmLockFileName)))
            {
                return NodePackageManager.Pnpm;
            }

            if (File.Exists(Path.Combine(directory, YarnLockFileName)))
            {
                return NodePackageManager.Yarn;
            }

            if (File.Exists(Path.Combine(directory, NpmLockFileName)))
            {
                return NodePackageManager.Npm;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }
}

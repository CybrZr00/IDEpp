using System.Diagnostics;

namespace IdePlusPlus.Infrastructure;

/// <summary>
/// Keeps MSBuildLocator's process-wide settings out of the `dotnet` processes this app starts.
///
/// Loading a solution in-process registers MSBuild with MSBuildLocator, which points this process at
/// one SDK by setting MSBUILD_EXE_PATH, MSBuildSDKsPath and MSBuildExtensionsPath. Those belong to the
/// in-process loader and are inherited by every child. A `dotnet build` that resolves to a different
/// SDK than the one registered (a global.json pin, or a preview SDK installed beside the stable one -
/// plain `dotnet` picks the newest) then runs one SDK's MSBuild against another's targets and tasks,
/// and fails with errors such as a NuGet.Frameworks FileLoadException from ProcessFrameworkReferences.
/// Removed here, the child resolves its own SDK the way the same command in a terminal would.
///
/// The variables cannot simply be cleared once registration is done: the in-process loader reads
/// them again every time a solution is evaluated.
/// </summary>
public static class MSBuildLocatorEnvironment
{
    /// <summary>What MSBuildLocator sets for this process. DOTNET_ROOT and DOTNET_HOST_PATH are left
    /// alone: the dotnet host sets them for its children itself, and they say where dotnet is, not
    /// which SDK to use.</summary>
    public static IReadOnlyList<string> Variables { get; } =
    [
        "MSBUILD_EXE_PATH",
        "MSBuildExtensionsPath",
        "MSBuildSDKsPath",
        "MSBuildLoadMicrosoftTargetsReadOnly",
    ];

    /// <summary>Removes <see cref="Variables"/> from the environment <paramref name="startInfo"/> will
    /// start its process with. Call before Start, and not when the process is meant to inherit
    /// exactly this app's own environment on purpose.</summary>
    public static void RemoveFrom(ProcessStartInfo startInfo)
    {
        foreach (var name in Variables)
        {
            startInfo.Environment.Remove(name);
        }
    }
}

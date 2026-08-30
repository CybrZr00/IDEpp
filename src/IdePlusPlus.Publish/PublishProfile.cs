namespace IdePlusPlus.Publish;

/// <summary>
/// What a profile actually does when you run it. <see cref="Unsupported"/> is not an error - it is
/// how a profile IDE++ can't run (MSDeploy, ClickOnce, Azure, FTP) still gets listed rather than
/// hidden, so a solution shared with Visual Studio doesn't look like it has fewer profiles than it
/// does. Those are shown read-only and never written back; silently rewriting someone's Azure
/// profile with the four properties IDE++ happens to model would be data loss.
/// </summary>
public enum PublishKind
{
    Folder,
    Container,
    Unsupported,
}

/// <summary>
/// One .pubxml, parsed. Deployment mode is <see cref="SelfContained"/> being null vs set, matching
/// MSBuild: an absent SelfContained is not "framework-dependent", it's "whatever the project says",
/// and writing it out explicitly when the user never asked for it changes behavior.
/// </summary>
public sealed record PublishProfile
{
    /// <summary>The file name without ".pubxml" - this is also the value `dotnet publish
    /// -p:PublishProfile=x` takes, which is why it has to stay a legal file name (see
    /// <see cref="PublishProfileStore.ValidateName"/>).</summary>
    public required string Name { get; init; }

    /// <summary>Where it was read from, or null for a profile that has never been saved.</summary>
    public string? FilePath { get; init; }

    public PublishKind Kind { get; init; } = PublishKind.Folder;

    /// <summary>Wins over the IDE's Debug/Release status-bar picker when publishing - publishing
    /// Release while debugging Debug is the normal case, so the status bar must not override it.</summary>
    public string Configuration { get; init; } = "Release";

    public string? TargetFramework { get; init; }

    public string? RuntimeIdentifier { get; init; }

    public bool? SelfContained { get; init; }

    public string? PublishDir { get; init; }

    public bool PublishSingleFile { get; init; }

    public bool PublishReadyToRun { get; init; }

    public bool PublishTrimmed { get; init; }

    public string? ContainerRepository { get; init; }

    public string? ContainerImageTag { get; init; }

    public string? ContainerRegistry { get; init; }

    public string? ContainerBaseImage { get; init; }

    /// <summary>
    /// Every property in the file that IDE++ doesn't model, kept verbatim.
    ///
    /// This is what makes editing a Visual Studio profile safe. VS writes a pile of properties IDE++
    /// has no opinion on - _TargetId, DeleteExistingFiles, LastUsedBuildConfiguration, ProjectGuid,
    /// ExcludeApp_Data, SiteUrlToLaunchAfterPublish - and a writer that only emitted its own known
    /// set would delete all of them on the first save. They round-trip through here instead.
    /// </summary>
    public IReadOnlyDictionary<string, string> AdditionalProperties { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>True for the kinds IDE++ knows how to run. The dialog disables editing for the rest.</summary>
    public bool IsRunnable => Kind is PublishKind.Folder or PublishKind.Container;

    public static PublishProfile CreateFolderDefault(string name, string? targetFramework = null) => new()
    {
        Name = name,
        Kind = PublishKind.Folder,
        Configuration = "Release",
        TargetFramework = targetFramework,
        SelfContained = false,
    };

    public static PublishProfile CreateContainerDefault(
        string name, string projectName, string? targetFramework = null) => new()
    {
        Name = name,
        Kind = PublishKind.Container,
        Configuration = "Release",
        TargetFramework = targetFramework,
        // The SDK's own default is the project name lowercased; spelling it out makes the dialog show
        // what will actually be produced rather than an empty box that silently works anyway.
        ContainerRepository = projectName.ToLowerInvariant(),
        ContainerImageTag = "latest",
    };
}

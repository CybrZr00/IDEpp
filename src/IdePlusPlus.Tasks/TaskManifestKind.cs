namespace IdePlusPlus.Tasks;

/// <summary>
/// Which kind of file a task was discovered from. Only <see cref="NpmScripts"/> is produced today:
/// reading a package.json's "scripts" object is a pure JSON parse, so it can run automatically on
/// solution load, while enumerating gulp/grunt tasks means executing the manifest and is gated
/// behind explicit consent - see docs/task-runner-design.md.
/// </summary>
public enum TaskManifestKind
{
    NpmScripts,
    Gulp,
    Grunt,
}

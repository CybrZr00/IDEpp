namespace IdePlusPlus.PluginContracts;

/// <summary>
/// Snapshot handed to <see cref="IPluginHoverProvider.GetHoverTextAsync"/> describing where in the
/// document the mouse came to rest.
/// </summary>
public sealed record PluginHoverContext(string FilePath, int Line, int Column, string WordUnderCursor);

/// <summary>
/// A plugin's editor hover provider, registered per file extension via
/// <see cref="IPluginHost.RegisterHoverProvider"/>. Returning <see langword="null"/> means "nothing
/// to show for this position" rather than an error.
/// </summary>
public interface IPluginHoverProvider
{
    Task<string?> GetHoverTextAsync(PluginHoverContext context, CancellationToken cancellationToken);
}

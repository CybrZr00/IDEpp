using Xunit;

namespace IdePlusPlus.Roslyn.Tests;

/// <summary>
/// Ties <see cref="RoslynWorkspaceServiceIntegrationTests"/> and
/// <see cref="RoslynCompletionServiceIntegrationTests"/> to one shared, already-loaded
/// <see cref="RoslynScratchSolutionFixture"/> instance - generating and restoring the scratch
/// solution via the real `dotnet` CLI is the expensive part (real wall-clock seconds), so it's done
/// once for both real-integration test classes rather than once per class. xUnit runs test classes
/// in the same collection sequentially, which is also required here: MSBuildLocator's process-wide
/// registration and the fixture's single <see cref="RoslynWorkspaceService"/> are shared state, not
/// safe to hit concurrently from two collections.
/// </summary>
[CollectionDefinition(nameof(RoslynScratchSolutionCollection))]
public sealed class RoslynScratchSolutionCollection : ICollectionFixture<RoslynScratchSolutionFixture>
{
}

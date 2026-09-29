using System.Text.RegularExpressions;

namespace IdePlusPlus.Tasks;

/// <summary>
/// Guesses whether a task is one that never exits - a file watcher or a dev server.
///
/// Only ever used to warn: binding one of these to Before Build means every build waits for the
/// binding timeout and then kills it, which is a confusing way to discover the mistake. A false
/// positive costs a line in the Output pane, so this leans towards catching the obvious cases
/// rather than towards being clever.
/// </summary>
public static partial class WatchTaskDetector
{
    [GeneratedRegex(@"watch|serve", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex WatchNameRegex();

    [GeneratedRegex(@"--watch|\s-w(\s|$)|nodemon|watchexec|webpack(-dev)?-server|\bvite\b(?!\s+build)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex WatchCommandRegex();

    public static bool LooksLikeAWatchTask(TaskDefinition task) =>
        WatchNameRegex().IsMatch(task.Name)
        || (task.Command is { Length: > 0 } command && WatchCommandRegex().IsMatch(command));
}

using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>
/// The Output pane, split into named channels (Build, Run, Debug, MCP, ...) chosen from a dropdown,
/// the way Visual Studio's Output window is. Each channel keeps its own bounded buffer, so a flood on
/// one channel can never grow without limit or drown out another - this is the guard against the
/// runaway-output hang that a single ever-growing StringBuilder allowed. Only the selected channel is
/// rendered to <see cref="Text"/>; writes to other channels just buffer, costing nothing on the UI.
///
/// The view model is thread-safe: buffers are updated under a lock on the caller's thread, and the
/// UI-affecting parts (adding a channel, refreshing <see cref="Text"/>) are marshalled through
/// <see cref="Post"/>. Tests leave <see cref="Post"/> as the default synchronous invoke; the app sets
/// it to the dispatcher so a background logger (e.g. a plugin) never touches WPF off-thread.
/// </summary>
public sealed partial class OutputPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "Output";

    public const string DefaultChannelName = "General";
    public const string BuildChannel = "Build";
    public const string RunChannel = "Run";
    public const string DebugChannel = "Debug";
    public const string SolutionChannel = "Solution";
    public const string NuGetChannel = "NuGet";
    public const string TasksChannel = "Tasks";
    public const string McpChannel = "MCP";
    public const string CopilotChannel = "Copilot";
    public const string PluginsChannel = "Plugins";

    private const int MaxLinesPerChannel = 5_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, Channel> _channels = new(StringComparer.OrdinalIgnoreCase);

    public OutputPaneViewModel()
        : base(PaneContentId, "Output")
    {
        _channels[DefaultChannelName] = new Channel();
        Channels.Add(DefaultChannelName);
    }

    /// <summary>The channel names shown in the dropdown, in creation order (General first).</summary>
    public ObservableCollection<string> Channels { get; } = [];

    [ObservableProperty]
    private string selectedChannel = DefaultChannelName;

    [ObservableProperty]
    private string text = "Build and run output will stream here.";

    /// <summary>Marshals the UI-affecting part of an append (channel list and <see cref="Text"/>).
    /// Default is synchronous; <c>App</c> replaces it with a dispatcher post so off-thread callers are
    /// safe.</summary>
    public Action<Action> Post { get; set; } = static action => action();

    /// <summary>Appends to the default "General" channel. Kept so existing callers and plugins that
    /// don't name a channel keep working unchanged.</summary>
    public void AppendLine(string line) => AppendLine(DefaultChannelName, line);

    public void AppendLine(string channelName, string line)
    {
        channelName = string.IsNullOrWhiteSpace(channelName) ? DefaultChannelName : channelName;

        bool isNewChannel;
        lock (_gate)
        {
            if (!_channels.TryGetValue(channelName, out var channel))
            {
                channel = new Channel();
                _channels[channelName] = channel;
                isNewChannel = true;
            }
            else
            {
                isNewChannel = false;
            }

            channel.Append(line ?? string.Empty);
        }

        Post(() =>
        {
            if (isNewChannel && !Channels.Contains(channelName))
            {
                Channels.Add(channelName);
            }

            if (string.Equals(channelName, SelectedChannel, StringComparison.OrdinalIgnoreCase))
            {
                RefreshText();
            }
        });
    }

    /// <summary>Clears the default channel.</summary>
    public void Clear() => Clear(DefaultChannelName);

    /// <summary>Clears one channel and brings it to the front - callers call this when an operation
    /// (a build, a run) starts, so the pane shows that operation's output as it happens.</summary>
    public void Clear(string channelName)
    {
        channelName = string.IsNullOrWhiteSpace(channelName) ? DefaultChannelName : channelName;

        bool isNewChannel;
        lock (_gate)
        {
            if (!_channels.TryGetValue(channelName, out var channel))
            {
                _channels[channelName] = new Channel();
                isNewChannel = true;
            }
            else
            {
                channel.Clear();
                isNewChannel = false;
            }
        }

        Post(() =>
        {
            if (isNewChannel && !Channels.Contains(channelName))
            {
                Channels.Add(channelName);
            }

            // Selecting the channel also refreshes Text via OnSelectedChannelChanged; when it is already
            // selected the setter is a no-op, so refresh explicitly.
            if (string.Equals(channelName, SelectedChannel, StringComparison.OrdinalIgnoreCase))
            {
                RefreshText();
            }
            else
            {
                SelectedChannel = channelName;
            }
        });
    }

    partial void OnSelectedChannelChanged(string value) => RefreshText();

    private void RefreshText()
    {
        string rendered;
        lock (_gate)
        {
            rendered = _channels.TryGetValue(SelectedChannel, out var channel) ? channel.Render() : string.Empty;
        }

        Text = rendered;
    }

    /// <summary>One channel's bounded line buffer. Oldest lines are dropped in batches once the cap is
    /// passed, so trimming is amortised rather than per-line.</summary>
    private sealed class Channel
    {
        private readonly StringBuilder _buffer = new();
        private int _lines;

        public void Append(string line)
        {
            _buffer.AppendLine(line);
            _lines++;
            if (_lines > MaxLinesPerChannel)
            {
                Trim();
            }
        }

        public void Clear()
        {
            _buffer.Clear();
            _lines = 0;
        }

        public string Render() => _buffer.ToString();

        private void Trim()
        {
            // Drop the oldest ~20% so this runs about once every MaxLinesPerChannel/5 appends.
            var drop = _lines - (MaxLinesPerChannel * 4 / 5);
            var text = _buffer.ToString();
            var index = 0;
            var removed = 0;
            while (removed < drop)
            {
                var newline = text.IndexOf('\n', index);
                if (newline < 0)
                {
                    break;
                }

                index = newline + 1;
                removed++;
            }

            _buffer.Clear();
            _buffer.Append(text, index, text.Length - index);
            _lines -= removed;
        }
    }
}

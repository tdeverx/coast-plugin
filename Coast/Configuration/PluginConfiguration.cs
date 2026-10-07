using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Coast.Configuration;

/// <summary>Server-wide Coast companion settings.</summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Initializes default settings.</summary>
    public PluginConfiguration()
    {
        EnableTrailerCrop = true;
        EnableLiveUpdates = true;
        FfmpegPath = string.Empty;
    }

    /// <summary>Gets or sets whether authenticated trailer crop analysis is available.</summary>
    public bool EnableTrailerCrop { get; set; }

    /// <summary>Gets or sets whether the administrator-only change feed is available.</summary>
    public bool EnableLiveUpdates { get; set; }

    /// <summary>Gets or sets an optional FFmpeg executable override.</summary>
    public string FfmpegPath { get; set; }
}

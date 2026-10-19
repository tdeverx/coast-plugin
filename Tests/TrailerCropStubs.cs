// The dependency-free harness links the production controller without a Jellyfin host.
namespace MediaBrowser.Controller.MediaEncoding
{
    public interface IMediaEncoder
    {
        string EncoderPath { get; }
    }
}

namespace Jellyfin.Plugin.Coast
{
    public sealed class Plugin
    {
        public static Plugin? Instance { get; set; } = new();
        public Configuration Configuration { get; } = new();
    }

    public sealed class Configuration
    {
        public bool EnableTrailerCrop { get; set; } = true;
        public string? FfmpegPath { get; set; }
    }
}

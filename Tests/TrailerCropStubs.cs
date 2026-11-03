// The dependency-free harness links the production controller without a Jellyfin host.
namespace Jellyfin.Database.Implementations.Enums
{
    public enum PermissionKind { IsDisabled, EnableMediaPlayback }
}

namespace Jellyfin.Database.Implementations.Entities
{
    public sealed class User
    {
        public bool Disabled { get; set; }
        public bool CanPlay { get; set; } = true;
    }
}

namespace Jellyfin.Data
{
    public static class UserEntityExtensions
    {
        public static bool HasPermission(this Database.Implementations.Entities.User user,
            Database.Implementations.Enums.PermissionKind kind) =>
            kind == Database.Implementations.Enums.PermissionKind.IsDisabled ? user.Disabled : user.CanPlay;
    }
}

namespace MediaBrowser.Controller.Entities
{
    public class Video { }
}

namespace MediaBrowser.Controller.Library
{
    public interface ILibraryManager
    {
        T? GetItemById<T>(Guid id, Jellyfin.Database.Implementations.Entities.User user) where T : class;
    }
    public interface IUserManager
    {
        Jellyfin.Database.Implementations.Entities.User? GetUserById(Guid id);
    }
}

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

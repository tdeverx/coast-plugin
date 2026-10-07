using Jellyfin.Data.Events;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Coast.Updates;

internal sealed class ChangeMonitor(ChangeJournal journal, ILibraryManager library, IUserDataManager data, IUserManager users, ISessionManager sessions) : IHostedService
{
    private bool Enabled => Plugin.Instance?.Configuration.EnableLiveUpdates == true;
    private void Added(object? sender, ItemChangeEventArgs e) { if (Enabled) journal.Add("item-updated", e.Item.Id, itemType: e.Item.GetType().Name); }
    private void Removed(object? sender, ItemChangeEventArgs e) { if (Enabled) journal.Add("item-removed", e.Item.Id, itemType: e.Item.GetType().Name); }
    private void UserData(object? sender, UserDataSaveEventArgs e) { if (Enabled && e.Item is not null) journal.Add("user-data", e.Item.Id, e.UserId, e.Item.GetType().Name); }
    private void UserUpdated(object? sender, GenericEventArgs<User> e) { if (Enabled) journal.Add("user-updated", userId: e.Argument.Id); }
    private void Playback(object? sender, PlaybackProgressEventArgs e)
    {
        if (Enabled && e.Item is not null)
            foreach (var user in e.Users) journal.Add("user-data", e.Item.Id, user.Id, e.Item.GetType().Name);
    }
    private void Stopped(object? sender, PlaybackStopEventArgs e) => Playback(sender, e);

    private void ConfigurationChanged(object? sender, BasePluginConfiguration e) => journal.Reset();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin) plugin.ConfigurationChanged += ConfigurationChanged;
        library.ItemAdded += Added;
        library.ItemUpdated += Added;
        library.ItemRemoved += Removed;
        data.UserDataSaved += UserData;
        users.OnUserUpdated += UserUpdated;
        sessions.PlaybackStart += Playback;
        sessions.PlaybackStopped += Stopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin) plugin.ConfigurationChanged -= ConfigurationChanged;
        library.ItemAdded -= Added;
        library.ItemUpdated -= Added;
        library.ItemRemoved -= Removed;
        data.UserDataSaved -= UserData;
        users.OnUserUpdated -= UserUpdated;
        sessions.PlaybackStart -= Playback;
        sessions.PlaybackStopped -= Stopped;
        return Task.CompletedTask;
    }
}

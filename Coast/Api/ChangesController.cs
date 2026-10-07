using Microsoft.Extensions.DependencyInjection;
using Jellyfin.Plugin.Coast.Updates;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Coast.Api;

/// <summary>Administrator-only incremental hints and a sanitized current stream snapshot.</summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Coast/Changes")]
public sealed class ChangesController : ControllerBase
{
    private readonly ChangeJournal _journal;
    private readonly ISessionManager _sessions;
    private readonly IServerApplicationHost _host;

    /// <summary>Initializes an authenticated server change feed.</summary>
    public ChangesController(IServiceProvider services, ISessionManager sessions, IServerApplicationHost host)
    {
        _journal = services.GetRequiredService<ChangeJournal>();
        _sessions = sessions;
        _host = host;
    }

    /// <summary>Reads up to 200 coalesced changes. A reset must be reconciled before acknowledging the new cursor.</summary>
    [HttpGet]
    public ActionResult Get([FromQuery] string? epoch = null, [FromQuery] long cursor = 0)
    {
        if (Plugin.Instance?.Configuration.EnableLiveUpdates != true) return NotFound();
        var page = _journal.Read(epoch, cursor);
        var sessions = _sessions.Sessions.Where(s => s.NowPlayingItem is not null && s.LastActivityDate > DateTime.UtcNow.AddMinutes(-2)).Select(s => new
        {
            s.Id,
            UserId = s.UserId.ToString("N"),
            s.UserName,
            s.Client,
            s.DeviceName,
            NowPlayingItem = new
            {
                Id = s.NowPlayingItem.Id.ToString("N"),
                s.NowPlayingItem.Name,
                s.NowPlayingItem.SeriesName,
                Type = s.NowPlayingItem.Type.ToString(),
                s.NowPlayingItem.ParentIndexNumber,
                s.NowPlayingItem.IndexNumber,
                s.NowPlayingItem.RunTimeTicks
            },
            PlayState = new { s.PlayState.IsPaused, s.PlayState.PositionTicks, PlayMethod = s.PlayState.PlayMethod?.ToString() }
        }).ToArray();
        // Pin JSON spelling: Jellyfin serializer defaults may vary between server versions.
        return new JsonResult(new { protocol = 1, serverId = _host.SystemId, epoch = page.Epoch, cursor = page.Cursor, reset = page.Reset, more = page.More, changes = page.Changes, sessions },
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = null });
    }
}

# Coast Plugin

Coast Plugin is the optional Jellyfin 12.1 companion for the
[Coast](https://github.com/tdeverx/coast) web app. It provides authenticated trailer crop analysis and an administrator-only change feed for incremental library, user-data and permission updates.
Coast remains usable when the plugin is absent, disabled, or temporarily unavailable.

The plugin does **not** inject CSS or JavaScript into Jellyfin Web and does not
replace Jellyfin playback, permissions, or transcoding.

## Install

In Jellyfin, open **Dashboard → Plugins → Repositories** and add:

```text
https://raw.githubusercontent.com/tdeverx/coast-plugin/main/manifest.json
```

Install **Coast Plugin** from the catalogue and restart Jellyfin. Its settings
page allows live updates and crop analysis to be disabled, or an FFmpeg executable to be supplied;
the default uses Jellyfin's configured FFmpeg.

## Coast live updates

Coast discovers `GET /Coast/Changes` through an existing Jellyfin administrator connection. In Coast’s **Settings → Jobs → Plugin updates**, choose that connection as the server administrator account. No public callback URL, new API key or browser-visible token is needed. The feed defaults to one read per minute and respects Coast’s schedule and developer mode.

The feed returns protocol version 1, the Jellyfin server identity, an epoch/cursor, up to 200 coalesced change hints, and a sanitized current-stream snapshot. Playback start/stop and user-data changes identify the affected user and item; item changes refresh shared metadata once and schedule each linked account’s native access checks. User permission updates trigger an authenticated account reconciliation. Credentials, filesystem paths and remote IP addresses are omitted. Only Jellyfin’s elevated authorization policy can read this endpoint.

The in-memory journal holds at most 4,096 coalesced hints. It is **not durable**: a plugin/server restart, configuration change or evicted range explicitly returns `reset:true`. Coast commits acknowledgement together with durable reconciliation jobs and retains native polling while reconciliation is incomplete or the plugin is unavailable. Once caught up, native live polls are replaced by the shared snapshot and periodic full reconciliation remains. Initial onboarding imports are never skipped.

Install the updated plugin and restart Jellyfin once to register its event subscriptions. Coast auto-discovers the feed on its next run. Older plugins remain usable for trailer crop; Coast uses native polling until the change endpoint is available.

## Build

The current preview targets Jellyfin 12.1.0 and .NET 10:

```sh
dotnet build Coast.slnx --configuration Release
dotnet run --project Tests/Coast.Journal.Tests.csproj --configuration Release
./scripts/package-plugin.sh
```

## Licence

Coast Plugin is licensed under AGPL-3.0-only.

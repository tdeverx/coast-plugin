# Coast Plugin

Coast Plugin is the optional Jellyfin 12.1 companion for the
[Coast](https://github.com/tdeverx/coast) web app. It provides authenticated video crop analysis and an administrator-only change feed for incremental library, user-data and permission updates.
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

## Crop resource contract and regression checks

`POST /Coast/TrailerCrop` retains its existing route and crop rectangle contract for authenticated video streams. One process-wide owner admits at most two FFmpeg processes and 64 total running, queued or completed analyses. Source URL and authorization are hashed together for cache identity. Completed entries expire opportunistically after 15 unused minutes or are evicted oldest-first under capacity pressure; active saturation returns `503 analysis_busy` with `Retry-After: 1`.

Each analysis has a 20-second budget including admission. Disconnecting one caller preserves shared work for other callers; the last caller cancels and drains its child. Standard error is continuously drained with a 65,536-character tail retained. Do not replace these bounds with an unbounded dictionary or one process per request.

The test executable below includes 24 crop checks and 11 journal checks. Crop controls cover credential isolation, sharing, cancellation, two-child admission, 64-entry capacity and expiry with controlled child processes. These are regression fixtures, not real-media performance measurements.

The [Coast performance follow-up](https://github.com/tdeverx/coast/tree/main/docs/audits/2026-10-22-performance-followup) records separate real retained-catalogue/media evidence, runtime and source fingerprints, and remaining acceptance work. The October follow-up compared unchanged crop rectangles across five actual codec/resolution inputs, including 4K HEVC, using real FFmpeg. The continuation also verified the production timeout and independent/final-consumer cancellation against genuine video bytes through a deliberately stalled range proxy, with zero FFmpeg children after final drain. These direct-controller checks did not exercise hosted Jellyfin authorization middleware. Remaining acceptance is tracked on the [application roadmap](https://github.com/tdeverx/coast/blob/main/docs/roadmap.md#performance-follow-up--remaining-acceptance-23-october-2026), especially `BLOCK-HOSTED`, `BLOCK-DEVICE` and `BOUND-CAPACITY`: repeat hosted permission/revocation checks, HLS/transcode and multi-device/long-session schedules, recording actual process/RSS/latency measurements without exposing tokens or media URLs. Keep the companion source fingerprint alongside the app fingerprint; a green application suite alone does not verify this owner.

## Build

The current preview targets Jellyfin 12.1.0 and .NET 10:

```sh
dotnet build Coast.slnx --configuration Release
dotnet run --project Tests/Coast.Journal.Tests.csproj --configuration Release
./scripts/package-plugin.sh
```

## Licence

Coast Plugin is licensed under AGPL-3.0-only.

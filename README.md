# Coast Plugin

Coast Plugin is the optional Jellyfin 12 companion for the
[Coast](https://github.com/tdeverx/coast) web app. It currently provides
authenticated, server-assisted detection of encoded black bars in local trailers.
Coast remains usable when the plugin is absent, disabled, or temporarily unavailable.

The plugin does **not** inject CSS or JavaScript into Jellyfin Web and does not
replace Jellyfin playback, permissions, or transcoding.

## Install

In Jellyfin, open **Dashboard → Plugins → Repositories** and add:

```text
https://raw.githubusercontent.com/tdeverx/coast-plugin/main/manifest.json
```

Install **Coast Plugin** from the catalogue and restart Jellyfin. Its settings
page allows crop analysis to be disabled or an FFmpeg executable to be supplied;
the default uses Jellyfin's configured FFmpeg.

## Build

The current preview targets Jellyfin 12.0.0 RC7 and .NET 10:

```sh
dotnet build Coast.slnx --configuration Release
./scripts/package-plugin.sh
```

## Licence

Coast Plugin is licensed under AGPL-3.0-only.

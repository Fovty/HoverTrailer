# Changelog

User-facing changes per release. The release workflow copies the section of the
tagged version into the GitHub release and into the plugin catalog entry, so a
new version needs its `## vX.Y.Z.W` section here before the tag is pushed.

## v0.4.2.0

- Fixed: clicking a card no longer starts its trailer a moment later. With a hover delay above two seconds the trailer used to play invisibly over the details page or the movie, with the page blurred (#27).
- Fixed: a running or pending preview closes when you navigate to another page or start playing a video.
- Fixed: a local trailer that fails to load no longer blocks every later preview. Direct play falls back to Jellyfin's remux once, then the preview closes with a message.
- Fixed: a YouTube trailer that is private, removed or not embeddable closes with "Trailer unavailable" instead of leaving YouTube's error page on screen.
- Fixed: the background blur stays on when one trailer replaces another.
- Fixed: without Persistent preview, the preview closes when Jellyfin redraws or removes its card.
- Fixed: the settings page keeps 0 for border radius, volume and opacity instead of resetting it to the default.

## v0.4.1.0

- Fixed: local trailers the browser can play natively are served as-is, so the seek bar works. Other files still go through Jellyfin's remux, and the seek bar is hidden for them (#25).

## v0.4.0.0

- Jellyfin 12 support. The plugin now targets Jellyfin 12 and .NET 10; servers on 10.11 stay on v0.3.1.0 (#24).

## v0.3.1.0

- New: "Don't replace a playing trailer". A trailer that is playing is no longer swapped when the cursor passes over other cards (#23).

## v0.3.0.0

- New: Fix Position mode pins the preview to the center or a corner. Drag it to move it and drag the corner handle to resize it (#20).
- New: volume and mute set on the player are remembered per device.
- New: the control bar also works for local trailers.

## v0.2.9.1

- New: option to stop a trailer when it ends instead of looping.

## v0.2.9.0

- New: control bar with play/pause, seek, volume, mute and fullscreen (#18).
- Fixed: YouTube trailers play in HD.
- Fixed: black edges at the top and left of YouTube previews.

## v0.2.8.1

- Changed: default settings for new installs.

## v0.2.8.0

- New: Pin to Viewport keeps an anchored preview inside the window.
- Fixed: the preview closes when the window is resized.
- Fixed: Halo blur follows the preview when its size changes.

## v0.2.7.0

- New: Anchor to Card positioning and Persistent preview.
- New: Halo background blur.
- New: optional focus trigger for keyboard and D-pad navigation.
- Fixed: the preview is limited to 90% of the viewport.
- Fixed: hover only reacts to movie and series cards.

## v0.2.6.1

- Fixed: YouTube trailers autoplay on the first hover after a page load (#16).

## v0.2.6.0

- Fixed: Jellyfin's Back and Home buttons keep working after a trailer played (#15).
- Changed: the plugin turns itself off on touch devices.

## v0.2.5.0

- New: progress indicator on the card while the hover delay runs.

## v0.2.4.0

- New: optional theme video fallback when an item has no trailer (#14).
- Fixed: the page stayed unresponsive after a hover preview.

## v0.2.3.0

- New: trailers for series cards (#12).

## v0.2.2.0

- New: toast messages while a trailer loads and when none is found (#11).

## v0.2.1.1

- Fixed: hover listeners attach more reliably.

## v0.2.1.0

- New: volume control and YouTube quality setting.

## v0.2.0.1

- Fixed: race conditions that left previews behind.
- Fixed: script injection through the File Transformation plugin.

## v0.2.0.0

- Jellyfin 10.11 support.
- New: script injection through the File Transformation plugin, so the web directory is not modified.
- Fixed: trailer URLs behind a reverse proxy with a base path (#1).

## v0.1.1.0

- New: centered positioning and background blur.

## v0.1.0.0

- First stable release.

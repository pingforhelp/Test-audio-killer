# AudioPruner Plugin

AudioPruner is a safe, non-destructive Jellyfin plugin that lets you remove unwanted audio tracks by remuxing with ffmpeg while keeping backups.

## Features
- ffprobe-backed audio track discovery
- Choose one audio stream to keep
- Keep/remove subtitles and chapters
- Non-destructive remux (original preserved)
- Optional backup + restore
- Live ffmpeg log streaming via SSE with colorized overlay in UI
- File-level locking to avoid conflicts

## Install
1. Copy `AudioPruner.zip` into your Jellyfin `plugins` folder.
2. Restart Jellyfin.
3. The plugin will appear under Plugins > General.

## Usage
- Open `/AudioPruner/audiopruner.html` in your Jellyfin instance.
- Enter the `ItemId` of a media file (can be found in Jellyfin's metadata manager URL).
- Fetch tracks → choose the audio stream to keep.
- Run remux → watch live ffmpeg logs in the overlay.
- A new file is created; original stays untouched unless you restore/replace.

## Notes
- Requires ffmpeg in Jellyfin's path.
- SSE log stream closes cleanly on completion/error.
- Use the restore endpoint to revert to backups if needed.

---
Author: PingForHelp

# VLC Commentary

A writing companion for VLC. While a movie plays, press **Ctrl+Alt+N**, type a
thought, press **Enter**. The note is saved with the exact movie timestamp, and
the movie never leaves the screen.

This is **Watch mode**, the first part of a timestamped commentary editor.
Write mode (sidebar, rich text, click-to-seek) and exports (SRT, Markdown, JSON)
come next.

## How it works

```
 VLC (fullscreen)  <--- localhost web interface --->  VLC Commentary (tray)
                         time, pause, resume                 |
                                                             v
                                   Documents\VLC Commentary\<movie>.json + .md
```

VLC's built-in web interface is switched on, listening on `127.0.0.1` only and
protected by a random password. The tray app reads the playback position
when you press the hotkey.

## Install (Windows)

Nothing extra to install. It's compiled with the C# compiler that ships with Windows.

1. Close VLC.
2. In this folder, run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File install.ps1
   ```
   Add `-Startup` to launch it automatically when you sign in.
3. Open a movie in VLC and press **Ctrl+Alt+N**.

The installer:
- builds `bin\VlcCommentary.exe`
- backs up `%APPDATA%\vlc\vlcrc`, then enables the web interface
  (`extraintf=http`, `http-host=127.0.0.1`, `http-port=8765`, a random `http-password`)
- writes `%APPDATA%\VlcCommentary\config.ini`
- adds a Start menu shortcut and starts the app (orange icon in the system tray)

## Using it

| Key | In the note box |
|---|---|
| `Ctrl+Alt+N` | Open the note box; the timestamp is captured at this moment |
| `Enter` | Save and close |
| `Shift+Enter` | New line |
| Tap `Tab` / `Shift+Tab` | Change type: Observation, Interpretation, Emotion, Question |
| Hold `Tab` | Show the notes already saved for this movie, with ▶ where this one will go |
| `Esc` | Cancel |

Saving is silent. To check what's saved, hold `Tab` in the note box. Only a failed
save shows a notice (red, in the corner), and your text is copied to the clipboard.

- The box appears in the top-right corner, clear of subtitles.
- The movie keeps playing. To pause while you type, right-click the tray icon and
  tick **Pause the movie while I type**.
- If you click back into the movie mid-note, the box stays open with your text.
  Press `Ctrl+Alt+N` to return to it.
- Every note starts as a **draft**. A Draft/Publish status will separate research
  notes from final commentary in Write mode.

Notes go to `Documents\VLC Commentary\`, one folder per movie:

```
Documents\VLC Commentary\
  Dhurandhar...\
    Dhurandhar....json        notes: timestamp, type, status, duration, content
    Dhurandhar....md          readable copy, regenerated on every save
    Dhurandhar....eng.srt     subtitles extracted from inside the video
    subtitles.txt             what was saved and from where
```

**Subtitles** are saved after the first note for a movie, once, in the background:
- separate subtitle files next to the movie (`Movie.srt`, `Movie.en.srt`, and a
  `Subs\` folder when the movie is alone in its folder) are copied
- subtitle tracks inside the video (`.mkv`, `.mp4`) are extracted with
  [FFmpeg](https://ffmpeg.org): `winget install Gyan.FFmpeg`. It reads the whole
  file at idle priority, so playback isn't affected. DVD bitmap subtitles can't be
  extracted on their own and are skipped.
- delete `subtitles.txt` in a movie folder to make the app look again

Notes from earlier versions, stored as loose files, are moved into folders automatically.

Command-line mode, for scripts: `VlcCommentary.exe --note "text" [Type]`

### Each movie keeps its own notes

Notes are linked to a movie by a fingerprint of the video file (its size plus a
hash of its first and last 64 KB), not by its name:

- Switching movies never mixes notes. Each note belongs to whatever was playing
  when you pressed the hotkey.
- Two different films with the same filename get separate files (`Movie.json`,
  `Movie (2).json`).
- Renaming or moving a film keeps its notes attached.

### Crash and power-cut safety

- Saved notes are written to a temp file, flushed to the physical disk, then
  swapped in. A power cut leaves the old file or the new one, never a broken one.
- While the note box is open, your text is copied to disk every 1.5 seconds
  with its movie and timestamp. After a crash, the next `Ctrl+Alt+N` reopens it,
  marked RECOVERED, still attached to the original movie.
- A notes file that can't be read is never overwritten. It's renamed to
  `*.unreadable-<date>` and the `.md` copy still has your text.

## Settings

`%APPDATA%\VlcCommentary\config.ini` (restart the app after editing):

```ini
password=...            # must match VLC's http-password
port=8765               # must match VLC's http-port
hotkey=Ctrl+Alt+N       # e.g. Ctrl+Shift+Space, Alt+F9
autopause=false
notes_dir=C:\Users\you\Documents\VLC Commentary
ffmpeg=                 # optional path to ffmpeg.exe; found automatically otherwise
```

## Troubleshooting

Check `%APPDATA%\VlcCommentary\log.txt` first.

- **"Can't reach VLC"**: VLC must be restarted once after install. If another
  program uses port 8765, run `install.ps1 -Port 8790` (any free port).
- **"VLC rejected the password"**: run `install.ps1` again with VLC closed.
- **Hotkey unavailable**: another app owns it. Change `hotkey=` in config.ini.

## Uninstall

1. Exit from the tray icon.
2. Delete the Start menu / Startup shortcuts named *VLC Commentary*.
3. To restore VLC's previous settings, copy the newest `%APPDATA%\vlc\vlcrc.bak-*`
   over `vlcrc` (or untick *Web* in VLC → Tools → Preferences → All → Interface → Main interfaces).

## Roadmap

- [x] Watch mode: hotkey capture, timestamped notes, types, draft status
- [ ] Write mode: chronological sidebar, click to seek VLC, rich text, Draft/Publish
- [ ] Screenshots attached to notes
- [ ] Export: SRT (published notes only), Markdown, JSON

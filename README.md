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
| `Tab` / `Shift+Tab` | Change type: Observation, Interpretation, Emotion, Question |
| `Esc` | Cancel |

- The box appears in the top-right corner, clear of subtitles.
- The movie keeps playing. To pause while you type, right-click the tray icon and
  tick **Pause the movie while I type**.
- If you click back into the movie mid-note, the box stays open with your text.
  Press `Ctrl+Alt+N` to return to it.
- Every note starts as a **draft**. A Draft/Publish status will separate research
  notes from final commentary in Write mode.

Notes go to `Documents\VLC Commentary\`, one file per movie:
- `<movie>.json`: the source of truth (timestamp, seconds, type, status, duration, content)
- `<movie>.md`: a readable copy, regenerated on every save

Command-line mode, for scripts: `VlcCommentary.exe --note "text" [Type]`

## Settings

`%APPDATA%\VlcCommentary\config.ini` (restart the app after editing):

```ini
password=...            # must match VLC's http-password
port=8765               # must match VLC's http-port
hotkey=Ctrl+Alt+N       # e.g. Ctrl+Shift+Space, Alt+F9
autopause=false
notes_dir=C:\Users\you\Documents\VLC Commentary
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

# falsifile

A tiny Windows desktop tidier.

Double-click the **falsfile** shortcut on your desktop and every loose file on the desktop gets
collected into one folder. Double-click it again and everything goes back to the exact grid cell
it came from.

No overlay window, no animation, no resident process. It is just a normal shortcut occupying a
normal icon slot.

---

## What it does

| Action | Items |
|---|---|
| **Collects** | Plain files on the desktop (jpg / png / pdf / doc / xlsx / py / zip ...) |
| **Keeps** | Every folder, `.lnk` / `.url` / `.appref-ms` shortcuts, hidden and system files, `desktop.ini` |
| **Never touches** | `C:\Users\Public\Desktop` (public desktop), the program itself |

Destination: `<anchor folder>\collected_yyyy-MM-dd_HHmm\` — rename it if you prefer, see *Configuration*.

Every run writes a `journal.json` entry set. Restore replays it exactly; it never scans folders and
never overwrites an existing file.

## Usage

1. Put `falsifile.exe` anywhere (for example `%LOCALAPPDATA%\Programs\falsifile\`).
2. Create the desktop shortcut:
   ```
   falsifile.exe --install
   falsifile.exe --install --anchor "C:\Users\you\Desktop\Inbox"
   ```
   This creates `falsfile.lnk` on the desktop with a generated multi-size icon (16-256 px).
3. **Double-click** it to collect, **double-click again** to restore.

Command line (the shortcut uses `--toggle`):

```
falsifile.exe --toggle          # collect / restore
falsifile.exe --clean-dryrun    # print what would be kept or collected, move nothing
falsifile.exe --clean-now       # collect now
falsifile.exe --restore-now     # restore now
```

## How it works

- **Positions**: `IFolderView2::GetItemPosition` gives the real position of each desktop item,
  `ClientToScreen` converts it to physical screen pixels. No grid guessing, no DPI multipliers,
  no screen capture.
- **Restoring positions**: `IFolderView::SelectAndPositionItems` with `SVSI_POSITIONITEM` puts each
  icon back into the cell it was recorded in.
- **Moving files**: `File.Move` (same-volume rename, sub-millisecond, does not trigger a desktop repaint).
- **Icons**: `IShellItemImageFactory.GetImage` for the real shell icon; the shortcut icon is
  rendered in-process into a multi-size `.ico`.

## Why there is no animation

An earlier version drew a frosted-glass badge over a desktop icon and animated files being sucked
into it. It was dropped on purpose:

1. Desktop icons are drawn by Explorer. Any animated layer has to be a separate window that is
   positioned exactly over an icon rect, and every change of icon size, DPI or layout means
   recomputing that rect.
2. Such a window shows visible single-frame flashes while it is first shown or while z-order is
   adjusted — very noticeable on a dark wallpaper.
3. A plain shortcut, on the other hand, is laid out by Explorer itself: zero windows, zero
   flicker, zero resident memory.

So the final version has no UI code in the active path at all.

## Build

.NET 8 SDK:

```
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output is a single ~66 MB executable with the runtime bundled; the target machine does not need
.NET installed.

Dependencies: `System.Drawing.Common` (icon generation). The Direct3D/Direct2D packages are a
leftover from the rendering prototype and are not used by the active code path.

## Configuration and logs

- Config: `%LOCALAPPDATA%\falsifile\config.json`
  ```json
  { "AnchorPath": "C:\\Users\\you\\Desktop\\Inbox" }
  ```
- Log: `%LOCALAPPDATA%\falsifile\logs\desktidy.log`
- Undo journal: `%LOCALAPPDATA%\falsifile\journal.json`

## Known limitations

- **Multi-monitor is untested** (the development machine has a single display). Coordinates are
  computed per monitor from that monitor's own `DefView`, but this path has never been exercised.
- Position restore depends on `IFolderView2`. If Explorer restarts and the view cannot be acquired,
  files still come back but their grid position is chosen by Windows.
- OneDrive / cloud placeholder files and exclusively locked files are not handled specially
  (locked files are skipped and logged).
- Only the current user's desktop is cleaned; the public desktop is left alone.

## Safety

- The journal is written before files move, so a crash is recoverable by hand.
- Name collisions get a `(2)` suffix; nothing is ever overwritten.
- The program's own directory is hard-coded as excluded, so it cannot move itself.

## License

Not specified.

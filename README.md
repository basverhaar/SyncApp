# Valheim World Sync

A small Windows app that lets a group of friends share one Valheim world through a Google Drive folder,
so whoever is online can host it.

**[⬇ Download ValheimWorldSync.exe](https://github.com/basverhaar/SyncApp/releases/latest/download/ValheimWorldSync.exe)**
(Windows 10/11, no installation needed; see [all releases](https://github.com/basverhaar/SyncApp/releases))

- **Play** checks whether a friend is already hosting. If so, it starts Valheim so you can join them.
- If nobody is hosting, it downloads the newest version of the world, starts Valheim through Steam, and you host.
- When you close Valheim, the world is uploaded back to Google Drive automatically.
- A setup wizard walks every player through what's needed.

## For players

You need:

1. **Steam** with **Valheim** installed.
2. **[Google Drive for Desktop](https://www.google.com/drive/download/)**, signed in with your Google account.
3. Access to the shared Google Drive folder (Editor role). The person who set it up shares it with you.

Then run `ValheimWorldSync.exe` and follow the setup. No installation is needed. Windows SmartScreen may warn
because the app isn't signed: click **More info → Run anyway**.

### Rules of thumb

- Always start Valheim with the app's **Play** button when you want to host the shared world.
- Keep the app open while you play (minimized is fine). It uploads the world when the game closes.
- When hosting, choose the world in Valheim, tick **Start server**, and set a password.
- If Valheim lists the world twice, use the local one, not the Steam Cloud copy.

### Safety nets

- Before the app overwrites a world on your PC, it saves a backup. It keeps the last 10 per world in
  `%LOCALAPPDATA%\ValheimWorldSync\Backups` (**Open backups** in the app).
- Each of those backups is also uploaded as a `.zip` to the `backups\` folder on Google Drive, so the group keeps them
  even if a PC is lost. Identical backups are stored only once, and the newest 10 are kept. They count towards the
  Google Drive storage of the player who uploaded them.
- **Back up…** saves the latest version of the world as a `.zip` wherever you like. These are never cleaned up.
- **Restore…** makes an earlier version the shared world again: pick one of the automatic backups (from Google Drive or
  this PC), or choose a `.zip` backup or a world file (`.fwl` / `.fwl2`). The current version is backed up first, so a
  restore can be undone. It's uploaded as a new version, and everyone gets it the next time they play.
- If two people end up hosting at the same time, the second upload is detected and you choose which version to keep.
  The other version is backed up.
- If a host's PC crashes, the others see "session didn't finish" and are asked to wait. When that host opens the
  app again, it uploads their progress automatically.

## How it works

The shared folder contains:

| Path              | Purpose                                                      |
|-------------------|--------------------------------------------------------------|
| `sharedsave.json` | Which world is shared                                        |
| `state.json`      | Latest version number, who saved it, and a hash of each file |
| `world\`          | The world files (only changed files are copied)              |
| `hosts\`          | One small file per PC that is hosting, with a heartbeat every minute |
| `backups\`        | Everyone's automatic backups as `.zip` files (newest 10)     |

Every PC only writes its own file in `hosts\`, so Google Drive never has to merge edits. A host that hasn't
written a heartbeat for 4 minutes is treated as gone. Downloads wait until the files on Google Drive match the
hashes in `state.json`, so you never get a half-synced world.

## Building

Requires the .NET 10 SDK.

```powershell
dotnet publish src/ValheimWorldSync -c Release -o dist   # -> dist\ValheimWorldSync.exe (self-contained)
dotnet run --project tests/ValheimWorldSync.Tests         # sync logic checks in a temp sandbox
```

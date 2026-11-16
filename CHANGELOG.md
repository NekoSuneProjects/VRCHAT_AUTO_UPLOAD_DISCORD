# Changelog

Each release has a `## <version>` section. CI copies the section for the version being built into the
GitHub release description, and the app shows it under "What's new". Write new entries at the top.

## 2.2.0

### New
- **Photos page**: all your VRChat screenshots grouped by the month they were taken, with Uploaded / Not uploaded filters
- Click a photo to see it large with its date, world, instance and players (when the photo has that info), its upload status per server, and upload it to the servers you tick – including paused ones
- ← / → to flip through photos, Esc to close; "Other file…" opens any PNG the same way (replaces the old "Upload file…" button)
- Dates come from when the photo was **taken** (VRChat file name), not when it was uploaded – Recent uploads shows "Taken …" with the upload time below
- Uploading an older photo adds "📸 Taken <date>" to the Discord post
- **No duplicates**: a photo is never sent to a server that already has it. The photo view asks Nekumi (`/check`) and locks those servers ("✓ already posted"); if Nekumi still answers "duplicate", it counts as already there instead of failing
- **Server rules**: adding or editing a server shows its ⚠️ Rules from Nekumi and needs "I've read these rules" before saving. Each server shows where it posts (server › #channel, Public/Private)
- If a server changes its rules, auto-upload there pauses and the Dashboard asks you to review them
- The photo view shows the rules of every server you tick, above the Upload button
- Update notifier: checks GitHub releases (NekoSuneProjects/VRCHAT_AUTO_UPLOAD_DISCORD) on start and every 6 hours
- Dashboard banner with **Update now** (downloads, closes, updates and restarts – settings/history are kept), **What's new** and **Skip**
- "What's new" shows the changelog of every version between yours and the update, and the new version's notes after updating
- Settings → Updates: auto-check on/off, opt-in beta versions, Check now, link to all releases
- If the app is in a folder it can't write to (e.g. Program Files), "Download" opens the release page instead

### CI
- Builds are stamped with their real version (e.g. 2.2.0-beta-15) so the app can compare versions
- BETA releases are published as pre-releases (only offered to users who turn on beta updates)
- Release descriptions come from this CHANGELOG.md (the section for the version being built); release-drafter removed

## 2.1.0

### New
- Nekumi support: https://nekumi.nekosunevr.co.uk/integration/vrchat/v1/upload
- Multiple upload destinations – every photo is posted to each enabled one, each with its own key:
  - **NekoSune Community** (NEKOSUNECOMMUNITY) – personal key from `/vrchatupload key`
  - **Your own servers** – one key per server/channel from the Nekumi dashboard → Integrations → VRChat
  - Custom URLs (old bot / self-hosted) still work
- Destination editor with key-type check (nkvrm_ community vs nkvrc_ server), enable/pause per server and a connection check
- Upload history shows the result per server; retry only resends to the servers that failed
- Sends worldId + instanceId (Nekumi shows them in the post)

### Privacy
- Instance info is cleaned before posting: the instance nonce and owner IDs are removed, so a posted photo can't be used to join a private instance

### Changed
- Settings format v2 (old single URL/key is moved into a "My server" destination automatically; a backup is kept)
- Server ID / Channel ID only shown for old-style URLs

## 2.0.0

### New
- Full GUI app (no more editing .env): Dashboard, Settings, Data and Logs pages, dark modern design
- Settings + upload history saved in %APPDATA%\NekoSuneVR\VRCX Image Discord Uploader – updates never lose your data
- Old .env next to the exe is imported automatically on first run (or via Data → Import old .env)
- Data page: export / import backups, clear history, reset – a safety copy is always kept in the backups folder
- Versioned settings file (SchemaVersion) with automatic upgrades for future changes
- Auth key encrypted with your Windows account (DPAPI)
- Tray icon, start with Windows, upload notifications, single instance
- Upload any screenshot by hand, retry failed uploads from the dashboard
- Reads VRChat's own XMP metadata and the "_wrld_" file name if VRCX metadata is missing

### Fixed
- "Could not find file" when VRCX renames the screenshot to ..._wrld_xxx.png
- Same screenshot being uploaded twice / racing (Created + Renamed events processed in parallel)
- "Upload failed: Bad Request" when no metadata was found (empty "{}" payload) – the bot now always gets world + players
- Error responses from the bot are now shown in full in the log
- Removed DotNetEnv / MetadataExtractor / Newtonsoft.Json dependencies

# VRCHAT_AUTO_UPLOAD_DISCORD

Automaticly Uploads Photos to your VRChat

# REQUIRES
1. [VRCX](https://github.com/vrcx-team/VRCX/releases/) (optional but recommended – gives world + player info)
2. Windows 10 / 11 (the release zip is self-contained, no .NET install needed)

# SETUP

# IN VRCX GO TO `Advanced` PAGE AND TURN THIS ON

![IMAGE HERE SETTINGS](https://cdn.discordapp.com/attachments/1163195342754435193/1250768960912363592/image.png?ex=666c24de&is=666ad35e&hm=1bd2232e598e6ccea6775733a89d558b10d09942bcf1dcda5f8913126a34247e&)

1. Download [Latest Files](https://github.com/NekoSuneProjects/VRCHAT_AUTO_UPLOAD_DISCORD/releases/latest)
2. Run `VRCX Image Discord Uploader.exe`
3. Open **Settings → Upload destinations** and add where photos should go (add as many as you like – each photo is posted to all enabled ones):
    - **Add NekoSune Community** – join the NekoSune Community Discord, run `/vrchatupload key` and paste your personal key (`nkvrm_…`)
    - **Add my own server** – invite [Nekumi](https://nekumi.nekosunevr.co.uk), open the [dashboard](https://nekumi.nekosunevr.co.uk/dashboard) → your server → **Integrations → VRChat**, create an integration for a channel and paste its key (`nkvrc_…`). Each key always posts to the channel it was made for.
4. Pick your **Screenshot folder** (e.g. `C:\Users\USERNAME\Pictures\VRChat`) and press **Save**
5. Press **Start watching** on the Dashboard and take photos in VRChat

All Nekumi destinations use the same upload URL: `https://nekumi.nekosunevr.co.uk/integration/vrchat/v1/upload` – the key decides the server.

### Upgrading from 1.x
Leave your old `.env` next to the new exe on first launch and it is imported automatically as a destination,
or use **Data → Import old .env…**. Settings from 2.0 are upgraded automatically. After that the `.env` is not needed any more.

### Updating
The app checks [GitHub releases](https://github.com/NekoSuneProjects/VRCHAT_AUTO_UPLOAD_DISCORD/releases) and shows a banner when a new version is out.
Press **Update now** – it downloads the new version, restarts and keeps all your settings.
Want to test new things early? Turn on **Settings → Updates → Include beta versions**.
**What's new** shows the changes since your version before you update, and again right after updating.

### Releasing (maintainers)
Add a `## <version>` section at the top of [CHANGELOG.md](CHANGELOG.md) and bump `<Version>` in the `.csproj`.
CI copies that section into the GitHub release description, which is what the in-app updater shows.

### Where is my data?
`%APPDATA%\NekoSuneVR\VRCX Image Discord Uploader`

| File | What |
|---|---|
| `settings.json` | your settings (auth key encrypted for your Windows user) |
| `history.json` | upload history (stops double uploads) |
| `logs\` | daily log files, kept 14 days |
| `backups\` | automatic copies made before imports / resets / upgrades |

Replacing the exe with a new version never touches this folder. Use **Data → Export backup** to move everything to another PC.

# PREVIEW

![PREVIEW PHOTO](https://upload.nekosunevr.co.uk/upload/i/si4vj.png)

# NOTE

Do not share your upload keys or exported backups or Peopel can Start Uploading all your Channels but all dpends Permission giving to BOT
For your Securuity if your Auth key got Hacked / Leaked and Starting Posting stuff shoudnt posting. please Regenerate your Key

Community key leaked? Run `/vrchatupload key` again – the old key stops working. Server keys can be regenerated in the Nekumi dashboard.

# IF YOU FACE ANY ISSUES OR PROBLEMS, CAN CONTACT ME `NEKOSUNEVR` ON DISCORD OR TWITTER/X

# THIS WONT WORK ON APPLE/LINUX/ANDROID/IOS YET, I NEED WORK ON APP SIDE OF THAT (RIGHT NOW WORKS AS WINDOWS ONLY)

<!-- GitAds-Verify: W1F9IKPOL4OH2YF27KBRPY4I8AQE71PZ -->

## GitAds Sponsored
[![Sponsored by GitAds](https://gitads.dev/v1/ad-serve?source=nekosuneprojects/vrchat_auto_upload_discord@github)](https://gitads.dev/v1/ad-track?source=nekosuneprojects/vrchat_auto_upload_discord@github)



# VRCHAT_AUTO_UPLOAD_DISCORD

Automaticly Uploads Photos to your VRChat

# REQUIRES
1. [VRCX](https://github.com/vrcx-team/VRCX/releases/) (optional but recommended – gives world + player info)
2. Windows 10 / 11 (the release zip is self-contained, no .NET install needed)

# SETUP

# IN VRCX GO TO `Advanced` PAGE AND TURN THIS ON

![IMAGE HERE SETTINGS](https://cdn.discordapp.com/attachments/1163195342754435193/1250768960912363592/image.png?ex=666c24de&is=666ad35e&hm=1bd2232e598e6ccea6775733a89d558b10d09942bcf1dcda5f8913126a34247e&)

1. Download [Latest Files](https://github.com/NekoSuneProjects/VRCHAT_AUTO_UPLOAD_DISCORD/releases/latest)
2. Invite [My Discord BOT](https://discord.com/oauth2/authorize?client_id=1390664793345757204) For this Work
3. Make Text Channel or use Current Channel
4. Run `VRCX Image Discord Uploader.exe`
5. Open **Settings** and fill in:
    - **Upload URL** – the bot upload URL (`DISCORD_WEBHOOK_URL` from the old .env)
    - **Server ID** / **Channel ID** – your Discord server + channel
    - **Auth key** – from `/show-token-key`
    - **Screenshot folder** – e.g. `C:\Users\USERNAME\Pictures\VRChat`
6. Press **Save**, then **Start watching** on the Dashboard
7. Take photos in VRChat – they upload automatically

### Upgrading from 1.x
Leave your old `.env` next to the new exe on first launch and it is imported automatically,
or use **Data → Import old .env…**. After that the `.env` is not needed any more.

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

Do not Share Your Server ID, Auth key or exported backups or Peopel can Start Uploading all your Channels but all dpends Permission giving to BOT
For your Securuity if your Auth key got Hacked / Leaked and Starting Posting stuff shoudnt posting. please Regenerate your Key

Todo that use `/regenerate-key` in Channel and to get AuthKey `/show-token-key`

# IF YOU FACE ANY ISSUES OR PROBLEMS, CAN CONTACT ME `NEKOSUNEVR` ON DISCORD OR TWITTER/X

# THIS WONT WORK ON APPLE/LINUX/ANDROID/IOS YET, I NEED WORK ON APP SIDE OF THAT (RIGHT NOW WORKS AS WINDOWS ONLY)

<!-- GitAds-Verify: W1F9IKPOL4OH2YF27KBRPY4I8AQE71PZ -->

## GitAds Sponsored
[![Sponsored by GitAds](https://gitads.dev/v1/ad-serve?source=nekosuneprojects/vrchat_auto_upload_discord@github)](https://gitads.dev/v1/ad-track?source=nekosuneprojects/vrchat_auto_upload_discord@github)



<p align="center"><img src="docs/images/banner.png" alt="FF9Recomp: FINAL FANTASY IX, native Android recomp. The app icon, an airship crossing two moons at dusk"></p>

# FF9Recomp

FINAL FANTASY IX (the PC release) with [Memoria](https://github.com/Albeoris/Memoria) and Moguri
Mod, running natively on ARM64 Android without Unity. The game's own .NET assemblies run on
.NET 8 Mono, on top of an engine written here (rendering, assets, audio, movies, input). It
includes a mod launcher for Memoria's catalog. Built for the AYN Thor (dual screen), and meant to
run on other Android handhelds and phones.

> **This repository and the APK contain no game code or data.** You need your own copy of FINAL
> FANTASY IX for PC. The app imports **your** game files on your device (or downloads them from
> your Steam account); nothing from the game is ever published here.
> Not affiliated with or endorsed by Square Enix. FINAL FANTASY is a trademark of Square Enix.

![FF9Recomp on the AYN Thor: Alexandria's steeple with Moguri Mod at 60 FPS on the top screen; the party, location, playtime, items and a touch deck on the bottom screen](docs/images/dual-screen-field.jpg)

## Made with AI

**This project was built with AI, start to finish.** I wanted a native way to play a modded FF9
on my AYN Thor, and I directed an AI coding agent (Anthropic's Claude, in Claude Code) to build
it, the same way as FF7Recomp. The AI wrote
essentially all of the code here: the engine underneath the game's assemblies, the Android host,
the launcher and these docs. My part was deciding what to build, testing every step on the Thor,
and saying what worked and what did not. Commits are credited to Claude for that reason.

I know AI-written code is a real point of contention, so here it is up front:

- **It has not been reviewed line by line by an experienced human developer.** It works on my
  Thor: the game boots, plays, and runs Moguri Mod and other Memoria mods, but expect rough
  edges and bugs.
- **The hard parts were built by people.** Memoria, Moguri Mod, the mods in Memoria's catalog,
  .NET and Mono, FFmpeg and SoLoud are human work that this project stands on (see
  [Credits](#credits-and-licenses)). Please support those projects.
- **I am making it public to improve it,** and so people can play it. Bug reports, testing on
  other devices, reviews and fixes are all welcome.

If AI-made software is not something you want to run or contribute to, that is a fair choice,
and I would rather you know before you install it.

## What you get

- The game running natively: no emulation, no Wine/FEX, no Unity. Memoria's own code runs as it
  does on PC, so its features and settings work here too.
- 60 FPS fields and battles (Memoria's frame interpolation), the original 30/15, or an
  experimental 120. 16:9 widescreen.
- A launcher:
  - import your game, or download it from Steam;
  - browse and search Memoria's mod catalog, with install, update and a download queue;
  - set mod order and turn mods on and off;
  - settings, save states, achievements and Report a problem.
  Downloads, imports and moves go on with the app in the background, with Pause and Continue
  (in the launcher and in the notification). A job Android stopped goes on where it was.
  It plays FF9's own menu sounds, taken from your game files, and works with a controller.
- Moguri Mod and the rest of Memoria's catalog. That includes the 2K and 4K video packs, which
  play at full resolution.
- Controller support, analog walk and run, swap confirm and cancel. On-screen touch controls for
  devices with no controller, with a layout you can arrange, buttons you can hide, and a BOOST button
  for the boosters.
- The game's boosters (High Speed with a choice of speed, Battle Assistance, No Encounters,
  9999 damage) and fast dialogue.
- A CRT filter (**Settings** → **Display**): scanlines and a phosphor mask.
- On the AYN Thor's bottom screen, in FF9's grey window style:
  - the party with portraits, HP/MP, ATB and Trance;
  - the location, playtime and gil;
  - **Auto**, **Abilities** (AP towards each ability), **Map**, **Items**, **Key items** and
    **Extras** (boosters and counters such as steals, frogs, dragons, chocographs and cards);
  - in battle, each enemy's level, HP, weaknesses, absorbs, immunities, halved elements, what's
    left to steal and drops;
  - on the world map, the whole map with where you, your chocobo and the airship are;
  - a touch deck: Menu, Save state, Load state, Speed, Battles and Battle+.

  The screen fades to shade while the game has taken control (cutscenes, talks) and lights up
  again when you have it back. Portraits, icons, the window style and the map are taken from your
  own game files when it opens.
- The book at the bottom screen's top right opens [Pixel Guide](https://pixelguide.app) on the
  bottom screen, with a back arrow to the panels. Without Pixel Guide, the book downloads it from
  its GitHub releases, and Android asks you to confirm the install.
- The same panels on one screen: beside the game (**Side by side**), under it (**Portrait**), or over it,
  opened by a small button or a controller button.
- Save states: Select+R1 in game keeps your place, in 10 slots.
- Steam achievements, sent to your Steam account when you sign in with a QR code.

## Screenshots

On the AYN Thor, with Moguri Mod and saves from a mid-game playthrough.

| | |
|---|---|
| ![Alexandria's steeple with Moguri Mod's backgrounds; the party, location, playtime and items with icons on the bottom screen](docs/images/dual-screen-field.jpg) | ![A battle against a Dracozombie at 60 FPS; on the bottom screen, the party's ATB and Trance, and the enemy's weaknesses, what's left to steal and drops](docs/images/dual-screen-battle.jpg) |
| **Dual screen: field** | **Dual screen: battle** |
| ![Gegalrich Shores on the world map; on the bottom screen, the world map with the party's position, the chocobo and the airship](docs/images/dual-screen-world-map.jpg) | ![Alexandria with the CRT filter: scanlines and a phosphor mask](docs/images/crt-filter.jpg) |
| **Dual screen: world map, with where you are** | **CRT filter** |
| ![Get mods: Memoria's catalog with search and categories](docs/images/launcher-get-mods.jpg) | ![Mods: on and off, load order, info and remove](docs/images/launcher-mods.jpg) |
| **Get mods** | **Your mods** |
| ![Game files: import your folder or download from Steam](docs/images/launcher-game-files.jpg) | ![Settings: frame rate, resolution, widescreen](docs/images/launcher-settings.jpg) |
| **Game files** | **Settings** |

## Playing

1. Download the APK from [Releases](../../releases/latest) and install it (allow installing
   from your browser or file manager when Android asks).
2. Get your game onto the device, one of two ways:
   - **Import game folder** (Game files): pick your FINAL FANTASY IX folder, the one with
     `FF9_Launcher.exe`, `StreamingAssets` and `x64`. Install
     [Memoria](https://github.com/Albeoris/Memoria/releases) in it on the PC first (and Moguri Mod
     if you want it). The folder is copied into the app once.
   - **Download from Steam** (Game files): sign in by scanning a QR code with the Steam app (you
     never type your Steam password here). The app downloads the game your account owns, checks
     every file against Steam's, then installs Memoria itself. A stopped download goes on where it
     was, and **Repair from Steam** fetches only missing or damaged files.
3. Press **Play** (or Start).

**Short on space?** With an SD card in the device, **Game files** → **Storage** moves your game,
mods and saves to it, and back. **Use your own folder** keeps them in a folder you pick instead
(for example `/sdcard/FF9Recomp`), easy to back up or sync with Syncthing; it needs Android's
"All files access", which the launcher asks for then.

### Bringing your saves

**Game files** → **Import a PC save**: pick `SavedData_ww.dat` from the PC
(`AppData/LocalLow/SquareEnix/FINAL FANTASY IX/Steam/EncryptedSavedData`). It is the same file
the game uses here, so all your save slots come across. The saves already on the device are kept
as a backup first.

## Mods

**Get mods** lists [Memoria's mod catalog](https://github.com/Albeoris/Memoria), the same one
Memoria's own launcher shows. Search as you type, filter by category, and press **Install**. Mods
download one at a time under **Downloads**, and the launcher stays usable meanwhile. Each mod's
page shows its preview, release notes, size and the mods it doesn't work with.

**Mods** lists what you have, in Memoria's load order: when two mods change the same thing, the
higher one wins. Turn mods on and off, move them with ▲▼, or update and remove them. Mods you
added by hand on the PC are listed too.

## Steam achievements

**Achievements** → **Sign in to Steam** shows a QR code: scan it in the Steam app on your phone.
The game unlocks achievements as it does on PC, and FF9Recomp sends them to your Steam account
(the FINAL FANTASY IX you own on Steam), straight away when online, otherwise the next time.

## Steam Cloud saves

**Game files** → **Saves** → **Sync with Steam Cloud** uses the same Steam sign-in as achievements.
With it on, the **Cloud Data** menu on the game's title screen works as it does on the PC:
- **Upload** puts all your saves in FINAL FANTASY IX's Steam Cloud.
- **Download** replaces the saves on the device with the ones there.

Nothing goes to Steam on its own: your saves only reach Steam Cloud when you choose Upload, as on
the PC. An Upload goes to Steam right away, while the game keeps running, with a message at the
bottom of the screen when it's there (**Upload to Steam right away**, on by default; with it off,
an Upload reaches Steam once the game has closed). The launcher also syncs with Steam before you
play and after you leave the game, so an upload made on the PC is there the next time you press
Play. **Sync now** syncs straight away. If the Cloud save changed on both sides, the newer
one is kept. Any copy about to be replaced is first saved to
`Android/data/com.ff9recomp/files/steam/cloud-backup`. Offline, the game starts as usual and the
next sync catches up.

## Reporting problems

**Report a problem** in the launcher makes one zip with everything needed to look into a
problem:
- the game's logs and how it last closed;
- your device, screen and settings;
- the game's install and your mod list;
- your saves or newest save state, if you tick them.

It doesn't include your game files, mods or Steam sign-in. Save it to Downloads or share it,
then open an issue here with what happened.

## Known limitations

- **Not every area has been played through.** The opening, Alexandria, fields, battles and the world
  map play on the Thor; a whole playthrough to the ending hasn't been confirmed yet. Please report
  what you find.
- Echo-S 9's voice lines don't play in dialogue
  ([#7](https://github.com/bhuiyah/ff9-recomp-android/issues/7)).
- An older set of FF9's own data files can break screens such as Config. **Repair from Steam** in the
  launcher fixed it for a player; otherwise update the PC copy, install Memoria there and import it
  again.
- The developer has the GOG release. **Repair from Steam** has worked for a player with the Steam one,
  but a whole download of the game, and Steam achievements, haven't been confirmed. Signing in to
  Steam shows a QR code to scan with the Steam app on another device.
- Steam Cloud saves were tested both ways with the PC version's Cloud save. A first Upload to an
  account with no Cloud save yet, and the Japanese game's Cloud file, have not been tested.
- Frame generation has only been tried on the AYN Thor. Getting `Lossless.dll` from Steam or choosing
  a file, a stretched picture and swapped screens haven't been tested with it.
- Movies decode on the CPU, on several cores. The 2K video pack plays at full speed on the Thor;
  the 4K pack has not been timed.
- The 120 FPS mode is experimental and runs warmer. The default Smooth 60 also runs warm on some
  devices: **Frame rate: Original** or a lower **Resolution** cools it down.
- Developed and tested on the AYN Thor (Snapdragon 8 Gen 2). Players have also run it on other
  Snapdragon devices (Odin 2 and 3, Retroid Pocket Flip 2 and Classic, Galaxy phones and more), on a
  Dimensity 8300 (Mali), an Exynos 2100 (Mali) and a Pixel 10 Pro Fold (Tensor G5).

## Building the APK

The APK is built on **Windows** (Git Bash). You need the Android SDK and NDK, JDK 17, the .NET 8
SDK, LLVM, and a PC copy of FINAL FANTASY IX with Memoria installed. The engine is compiled
against the game's own `UnityEngine.dll`, but nothing from the game goes into the APK.

```bash
bash tools/build_ffmpeg.sh     # FFmpeg (Ogg/Theora/Vorbis) for arm64
bash tools/build_soloud.sh     # SoLoud, Memoria's audio backend
bash tools/build_engine.sh     # FF9Engine.dll
bash tools/build_apk.sh        # the host library, the runtime zip and the APK
```

The APK ends up in `android/app/build/outputs/apk/release/app-release.apk`.
[docs/NOTES.md](docs/NOTES.md) has the details, and [docs/ROADMAP.md](docs/ROADMAP.md) has the
milestones.

## Credits and licenses

FF9Recomp is licensed under the [GPL-3.0](LICENSE). It builds on:
[Memoria](https://github.com/Albeoris/Memoria) (MIT; installed from its own releases, not shipped),
Moguri Mod and the authors of every mod in Memoria's catalog (downloaded from their own links),
[.NET runtime and Mono](https://github.com/dotnet/runtime) (MIT),
[FFmpeg](https://ffmpeg.org/) (LGPL),
[SoLoud](https://solhsa.com/soloud/) (zlib),
the [LZMA SDK](https://www.7-zip.org/sdk.html) (public domain),
[lsfg-vk-android](https://github.com/FrankBarretta/lsfg-vk-android) (MIT) and
[volk](https://github.com/zeux/volk) (MIT) for frame generation.
For Steam: [JavaSteam](https://github.com/Longi94/JavaSteam) (MIT) with protobuf (BSD-3),
kotlinx.coroutines (Apache-2.0), [Bouncy Castle](https://www.bouncycastle.org/) (MIT),
[XZ for Java](https://tukaani.org/xz/java.html) (public domain) and
[zstd-jni](https://github.com/luben/zstd-jni) (BSD-2); [ZXing](https://github.com/zxing/zxing)
(Apache-2.0) draws the QR code.
The launcher, the bottom screen and the tooling follow FF7Recomp's.

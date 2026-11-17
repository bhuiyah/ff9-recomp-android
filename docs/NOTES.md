# Notes

## Layout
- `src/host/` the native host: embeds Mono, implements UnityEngine internal calls.
- `tools/` PC-side scripts: `cil.py` (ECMA-335 reader), `unity_surface.py` (engine-call survey),
  `gen_host_tables.py` (stub table + JIT token lists), `unity_sf.py` (Unity 5.2 serialized
  files), `dll_seeds.py` (export seeds for FF7Recomp's pcrecomp lift), `build_host.sh`.
- Not in git (game-derived or downloaded): `work/FF9-memoria` (copy of the game with Memoria
  installed), `out/` (generated tables, logs), `dl/` (Memoria Patcher, .NET 8 Mono runtime
  nupkg), `ref/` (Memoria source files for reading).

## Thor
- Wi-Fi adb; `$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe` with `MSYS_NO_PATHCONV=1`.
- Device staging: `/data/local/tmp/ff9` (`bcl/` .NET 8 libraries and native .so files,
  `managed/` game DLLs, `tokens/`, `game/` = working directory with Memoria.ini,
  StreamingAssets and `x64/FF9_Data`).
- Before using it: check FF7Recomp isn't running (the user may be playing). Put it back to
  sleep afterwards.

## Mono embedding (.NET 8.0.30, android-arm64)
- `monovm_initialize` properties: TRUSTED_PLATFORM_ASSEMBLIES, NATIVE_DLL_SEARCH_DIRECTORIES,
  APP_CONTEXT_BASE_DIRECTORY, `System.Globalization.Invariant=true`,
  `System.Resources.UseSystemResourceKeys=true` (avoids a resource cctor crash).
- The host thread must be inside `mono_threads_enter_gc_unsafe_region` to use object APIs.
- Engine functions are registered cooperative: `mono_add_internal_call_with_flags(name, fn, 1)`.
- Exception `ToString` from native code can abort; only ask for it when debugging (`FF9_WHY`).

## Unity 5.2 serialized files (format 15)
- Big-endian header (metaSize, fileSize, version, dataOffset), endianness byte at 16 (0 =
  little), metadata from 20: version string, platform, typetree flag (0 in this build),
  types (classID, 16-byte script hash if classID < 0, 16-byte hash), objects (aligned:
  pathID i64, offset, size, typeIndex, classID u16, scriptIndex i16, stripped u8),
  script types, externals.
- level0 = splash scene: `Camera_SplashTitle` (Camera + `SplashScreen`), `SplashTitle`
  (SpriteRenderer).

## Engine notes (M1-M3)
- Engine calls: every UnityEngine internal call is a jump thunk (out/host/ue_thunks.S) through
  ue_icall_table; FF9Engine fills slots with its [Icall] methods' compiled code. Registering
  managed methods with Mono directly broke when a caller got compiled (and the icall resolved)
  before registration finished.
- Debug switches (FF9_ENV): FF9_FRAMES, FF9_SHOT=f1,f2:dir, FF9_DRAWLOG=frame, FF9_DUMP_FRAME=frames
  (+FF9_INSPECT=Types or FF9_INSPECT_GO=Name_With_Underscores), FF9_TRACE_FIELDS=Type,
  FF9_DUMP_TREE=classId, FF9_DUMP_GLSL, FF9_SKIP_SHADER, FF9_ONLY_Q/FF9_SKIP_Q, FF9_DUMP_TEX,
  FF9_TRACE_MESHCOL, FF9_STOP_ON_EXCEPTION.
- Text fields (issue 6, Core/TextInput.cs): Input.inputString is empty here, so a selected UIInput
  (the naming screen) posts its text to the host and GameActivity shows Android's keyboard in a
  dialog when no controller is connected (or touch controls are forced on); the reply goes in
  through UIInput.value. Controller: Confirm twice keeps the default, Menu resets it.
  FF9_TEST_NAMING=character opens the naming screen in a field.
- Battles after a narrow field camera (issue 5, Core/Widescreen.cs): PSXCameraAspect applies
  NarrowMapList.RestrictedCams by fldMapNo and the old FieldMap's camIdx, which still name the field
  in a battle (Prima Vista cargo room, map 50 camera 1, before the first battle): camIdx is cleared
  in battles and on the world map. To test: New Game, the candle, the naming screen, first battle.
- Memoria's own log (game/Memoria.log on the device) explains most startup failures.
- Gotchas found: serialized 1/2-byte script fields align to 4 except inside arrays; format-15
  objects' type ID is the class ID (match type trees by it); AnimationCurve wrap modes are
  Unity's internal enum (0 ping-pong, 1 repeat, 2 clamp); CharacterInfo uv is bottom-left with
  positive height and vert.y is relative to the ascent; Memoria casts SoLoud handles to int (C++
  objects are allocated below 2 GB, src/host/lowmem.cpp: 1 GB when free, else the first free
  64 MB-aligned place below 2 GB, since a Pixel 5 has 1 GB taken (issue 2); FF9_TEST_LOWMEM_TAKEN=1
  blocks 1 GB to test that); .NET 8 rejects relative
  Assembly.LoadFile (detour in Compat.cs); Android .NET crypto needs a JVM (src/host/crypto.c).
- Memoria.ini on the device: [Audio] Backend = 2 (SoLoud).

## APK (M5)
- `bash tools/build_apk.sh` -> `android/app/build/outputs/apk/release/app-release.apk`, signed with the
  key in ~/.ff9recomp/keystore.properties (or FF7Recomp's). Contents: libff9.so, Mono's .so files,
  assets/runtime.zip (.NET 8 class libraries + FF9Engine.dll). No game files.
- On the device: Launcher -> Import game folder (SAF) copies the player's Memoria-installed FF9
  folder to Android/data/com.ff9recomp/files/game (skips x86, *.exe, Mono, Plugins) and sets
  [Audio] Backend = 2. The game runs in its own process (:game); log in files/ff9.log and logcat FF9.
- UnityEngine's internal calls are enumerated at run time from the player's UnityEngine.dll and
  bound to numbered thunks (tools/gen_thunks.py), so nothing game-derived is compiled in.
- Moguri Mod (~5 GB) is installed with Memoria's Mod Manager on the PC before importing; Memoria
  loads it from the game folder as on PC.

## Battle effects plugin (FF9SpecialEffectPlugin.dll)
- Memoria's SFXRework still calls the plugin every battle frame (`SFX_UpdateCamera`: the PSX
  battle camera, 13 floats) and for camera sequences, so battles need it.
- src/host/sfx_plugin.c loads the player's DLL from `x64/FF9_Data/Plugins` at run time:
  sections into an mmap, DIR64 relocations, each IAT slot pointed at a stub address that the
  interpreter hands to `host_call` (msvcr120/msvcp120/kernel32 calls implemented there), then
  DllMain. The game's `SFX.Callback` is a native function pointer and is called directly.
- src/host/x64emu.c: an x86-64 interpreter (guest addresses = host addresses; Windows x64
  calling convention; flags computed eagerly; SSE scalar/packed). Unknown opcodes stop the
  call with a log line (`x64emu: unknown ... at`), never crash.
- Nothing derived from the DLL is generated, stored or shipped.

## More debug switches (2026-11-14)
- `FF9_ARGS="-memoria-battle+1"`: extra game arguments (`+` separates; Memoria's launch options:
  `-memoria-battle N`, `-memoria-party a,b`, `-memoria-seed N`, `-memoria-autoload`).
- `FF9_MEMSTAT=n` (+`FF9_MEMSTAT_MAPS`, `FF9_MEMSTAT_TYPES`): memory, mappings, objects by type.
- `GLTRACE=1` build + `FF9_GL_TRACE`: GL call counts per report.
- `FF9_EXC_OFFSETS`: IL offsets in exception stacks. `FF9_MONO_OPTS="--optimize=-inline"`.
- `FF9_DUMP_RT=name` with `FF9_DUMP_FRAME`: render texture contents. `FF9_TRACE_DRAWTEX`.
- `FF9_SKIP_CAMERA=Name@depth`, `FF9_SKIP_SHADER`, `FF9_NO_RENDER_FROM`, `FF9_TRACE_SCENE`.
- `FF9_SFX_TRACE`, `FF9_SFX_STUB`, `FF9_SFX_PLUGIN=path`.

## Gotchas found
- Prefab assets must not run scripts or render (the MBG prefab's camera wiped the field).
- Shader values the shader doesn't declare in Properties come from the globals even when the
  material has a saved copy (field fades, `_MatrixRT`).
- Matrix4x4 is serialized by row (e00 e01 ...); UnityEngine.Matrix4x4's fields go by column.
- Screenshots must force alpha 255 (cameras clear with alpha 0.02).
- Renderer.material / MeshFilter.mesh copy once per renderer, not per call.
- Known: during the opening FMV (field 70, which has no walk mesh by design) the player's
  FieldMapActorController throws in MovePC every frame (walkMesh is null); the game carries on
  to the cargo hold after the movie. Not yet known whether Unity avoids it. Logged once (repeats
  of the same exception are collapsed: first 3, then one line per 1000).
- App: Mono on Android renames DllImport "__Internal" to "libmonodroid.so" (map it to the host).
- App input: a press stays visible to the game for at least 80 ms (taps shorter than a frame,
  several pad readers per frame).
- A component's name is its GameObject's (`transform.name`). Memoria finds bones with
  `GetChildByName` (battle targeting, smooth animation); components now copy the GameObject's
  name when created, cloned or renamed. Returning `g.Name` from the `Object::get_name` icall
  instead corrupted strings after a GC (not understood; avoid adding logic to that icall).
- `-memoria-battle 1` is too hard for a level-1 Zidane (enemies hit for 600-800); use
  `-memoria-seed 1` to get one turn. 302 is a scripted battle (no party input).

## App layer (ported from FF7Recomp, 2026-11-14)
- adb test API (TestControl, DUMP-protected): `am broadcast -a com.ff9recomp.test.GET|SET|STOP|REPORT -n com.ff9recomp/.TestControl`,
  `am start -n com.ff9recomp/.LauncherActivity -f 0x24000000 --ez test_play true`,
  `am broadcast -a com.ff9recomp.test.PAD -p com.ff9recomp --ei buttons 0x1000 --ei ms 150` (XInput bits).
- Settings (Settings.java) live in the launcher's prefs and are written into game/Memoria.ini on Play
  (read/written as ISO-8859-1: the file is not valid UTF-8).
- Sleep/background: surfaceDestroyed hands the engine a null window (generation handshake in
  android_jni.c); Gfx/Device.ChangeWindow drops the EGL surface, waits (SoLoud muted), recreates it.
- Select+Start held 1 s: Leave dialog (the game sees neither button while both are held).
- Report a problem: zip with ff9.log (+prev), Memoria.log/.ini, java-crash.txt, tombstone/ANR records.
- Launcher: FF7Recomp's layout (commands left, help line top, page window right) in FF9 colours.
- Mods: Memoria's catalog (files/mods/catalog.xml); installs into game/<InstallationPath>, FolderNames
  ordered by catalog priority; .7z unpacked by src/launcher/un7z.c (libff9launcher.so, LZMA SDK).
- Save states: copies of Memoria's autosave (files/savestates/slotN.dat); resume = restore +
  -memoria-autoload (start of the place, not the exact moment). Select+R1 in game.
- Achievements: steamwrapper stand-in (src/host/win32.c) reports Steam ready, answers the stats
  callbacks and appends unlocks to files/achievements.txt (FF9_ACHIEVEMENTS); SteamLink syncs (app 377840).
- Menu sounds: Core/UiSounds.cs writes sound ids 101-103 (cancel, buzzer, cursor) as Ogg to files/ui-sounds.
- Second screen: Core/Companion.cs JSON (FF9_COMPANION) -> CompanionView; Select+L1 swaps in the guide page.
- adb: `am start ... --ez test_play true --es test_args "-memoria-battle 336"`; TestControl SET takes guide_url.
- Tested 2026-11-14: Masked Man, Alexandria (meeting-room dialogue choice loops on the wrong answer, by
  design), in-game menu and Item screen, Smooth 60 fields and battles, battles 4 6 21 52 73 300 clean.
- Rumble: the game calls XInputGamePadSetState only where a scene has a vibration track (none seen yet;
  the host logs "rumble l r").
- Opening-movie error (field 70, FieldMapActorController.MovePC IL_061f): Memoria's own line
  `if (Configuration.Control.PSXMovementMethod) actualMoveVec *= this.fieldMap.walkMesh.GetTriangleSlopeFactor(...)`
  has no null check and field 70 has no walk mesh by design (HonoluluFieldMain guards it elsewhere).
  A Memoria bug, not the engine's; harmless (the game carries on).
- `FF9_IL_DUMP=Type::Method:fromHex:toHex`: prints a game method's IL with tokens resolved (on the device;
  no .NET runtime on the PC). `FF9_WARP_WORLD=frame:wldMapNo`: rough world-map switch for tests.
- Rumble reaches the game's XInput path (pad reported connected); FF9 vibrates only at scripted moments.
- 2026-11-14: a shared save file (Steam forum, SavedData_ww.dat, slot 2: Alexandria, Gegalrich Shores on
  the world map, Dali Inn at Lv 99) is kept on the Thor only (/data/local/tmp/ff9/save_shared), never
  in the repository. `FF9_LOAD_FILE=frame:slot:file` (0-based; slot 2 FILE03 = 1:2) loads one from the
  title like the Load Game screen; the app takes `--es test_load 1:2` on test_play.
- Shader register bug (fixed): oT1..oT9 parsed as oT0. The world map was all fog colour, the movies
  lost their chroma (the opening FMV's "tint" was this, not the original).
- The Thor sleeping during a test leaves the game without a surface (nothing starts): wake it first.
- 2026-11-14: second-screen deck with tabs (Auto, Abilities, Map, Items, Key items, Extras, « hides the
  party). Core/Companion.cs adds per enemy: Lv, weak/absorb/immune/half elements, StealableItems,
  DroppableItems; per member: portrait (FF9UIDataTool.AvatarSpriteName), gear, abilities the gear
  teaches with AP (ff9abil); items (icons item{shape}_{color} from IconAtlas), key items; counters
  (steal_no, Frogs, categoryKillCount[Dragon], treasure points, chocographs = gEventGlobal 187..189
  bits, card W/L/D); 9999 damage booster (command 6, BoosterType 3); chocobo and airship on the map.
  Portraits and icons are cut from the player's atlases at runtime (GPU read-back once per atlas) into
  files/worldmap/ui_<Atlas>_<sprite>.png; nothing ships with the app. Reflection lookups are cached;
  gear and counters refresh once a second, item lists only when the inventory changes.

## Download from Steam (from FF7Recomp's handoff part 2)
- Game files → Download / Repair from Steam (SteamFiles.java): JavaSteam depot download of app
  377840's Windows depots, every file SHA-1 checked, intact files kept (resume and repair). The
  import's skip rule (GameInstall.skipped) applies, and the files Memoria replaces are not taken.
- Then Memoria (MemoriaInstall.java): Memoria.Patcher.exe v2026.11.09 from Memoria's GitHub
  release (SHA-256 pinned, cached in files/steam), unpacked as its Program.cs does: trailer
  "MEMORIA\0" + uncompressed size + gzip offset at the end (or within 0x2700..0x2920 of it when
  signed); entries u32 size, i64 ticks, u8 part count, parts (i16 id, high bit = new name: u8
  length + UTF-8), data. "{PLATFORM}" = x64. An existing Memoria.ini is merged (player's values
  kept, new keys added).
- Steam over WebSocket only (JavaSteam's TCP path needs BouncyCastle on the classpath; FF7 #49).
- Test: `am broadcast -a com.ff9recomp.test.STEAM_DOWNLOAD -n com.ff9recomp/.TestControl --es op
  download|memoria|stop|status --es dir ABS` (never the player's game folder). Tested 2026-11-15:
  op memoria gives files identical to a PC install (Assembly-CSharp, Memoria.Scripts SHA-256), the
  ini merge keeps settings. The Steam download itself is not tested: it needs an account that owns FF9 on Steam (the developer has the GOG release).

## GLES capability check (from FF7Recomp's handoff part 2, section 4)
- FF7's three PowerVR/Mali failures cannot happen here: render targets are RGBA8 only
  (SupportsRenderTextureFormat says so), depth is DEPTH24_STENCIL8 (core GLES 3), textures are
  decoded to RGBA8 on the CPU, and movies are Theora (8-bit only; ffmpeg is built with theora
  alone). No GL extension is used.
- What can differ is GL_MAX_TEXTURE_SIZE (GLES 3 promises 2048; the Thor's Adreno has 16384).
  Textures over it are uploaded as a halved copy (Textures.Shrink, same UVs) and logged once.
  The log also shows each new largest texture over 2048: Prima Vista's field 'atlas' is
  2788x2924 with Moguri; Moguri's own PNGs are at most 4096.
- Test switch: files/env.txt, `FF9_TEST_MAX_TEXTURE=1024` (any FF9_TEST_* line is set before the
  engine starts). Tested 2026-11-15: at 1024 the title's 2048x2048 'General Atlas' and two
  screen-sized textures were shrunk and the title drew correctly; a field at 2048 was not
  checked (the test save would not load that run, also with the switch off).

## Movie mods (2K / 4K video packs)
- The catalog's video packs replace StreamingAssets/ma/FMV*.bytes with Ogg Theora at 1920x1440
  (2K) or larger; Memoria finds them in the mod folder (MovieMaterial.Open, TryFindAssetInModOnDisc),
  and the same FFmpeg Theora decoder plays them. The opening is FMV001.
- theora.c decodes with frame + slice threads (thread_count 0) and logs per movie at close:
  "movie WxH: N frames, decode X ms average, Y ms worst, Z over a frame's time".
- Tested 2026-11-15 on the Thor: the 2K pack's FMV001 as a test mod, 1799 of 1800 frames, 0.1 ms
  average on the game thread, none late; 16 FPS counter during movies as with the originals.
- files/env.txt also passes FF9_TRACE_* (FF9_TRACE_MOVIE=1 logs each movie's path and size).

## Long jobs in the background, Pause / Continue (FF7Recomp GitHub #53, its 1.1.13)

- `JobService` (a foreground service, type dataSync, with a partial wake lock) runs the launcher's
  long jobs one at a time: the Steam download, importing the game folder, and moving to or from
  the SD card. Its notification shows the progress and has Pause; a paused or stopped job's
  notification has Continue. The state is static (`running`, `status`, `percent`, `paused`), and
  the launcher reads it.
- The job (its kind and argument) is kept in the launcher's prefs (`job_kind`, `job_arg`) until it
  finishes. A job that was paused, or stopped by Android or by an app update, shows **Continue**
  at the top of Game files, before the "imported" line: a paused import over an older copy is not
  finished.
- Resuming: the Steam download skips files whose SHA-1 already matches; the import and the move
  write `.part` files and rename them, so a file that is there is whole, and both skip those. The
  import keeps a persistable read grant on the picked folder, so Continue can read it after a
  restart; the grant is given back when the job ends or is discarded. Only a continued import
  (`job_ran`) skips files of the right size; a new one copies everything, so importing again
  repairs a damaged file (both from FF7Recomp 1.1.14). Discarding a move deletes its partial copy; discarding a download or an import keeps
  the files.
- The mod queue: `QueueService` keeps the process in the foreground while `DownloadQueue` works.
  Pause stops the running download (its partial archive stays) and is persisted in
  `files/mods/queue.paused`.
- Test API: `STEAM_DOWNLOAD --es op pause|continue|discard|test_memoria|queue_add --es folder
  X|queue_pause|queue_continue|queue_cancel`. `test_memoria` is a JobService job that downloads
  only Memoria, the first step of the Steam download, so it works without Steam.
- Tested on the Thor: Pause at 48%, then `am force-stop`, then the launcher shows Continue, which
  resumes at 51 MB of 87, and the SHA-256 check passes. The queue: Pause, then force-stop, then it
  is still paused after the restart, then Continue resumes it. A move to the SD card wasn't tried
  (the card is full), and neither was the import (it needs the folder picker).

## GitHub issues 8-13 (0.1.4)

- Memoria.ini saved by a file manager or a PC can belong to another app and be read-only to this
  one (adb push reproduces it: owner shell, mode 644). `Settings.writeIni` then writes a new file
  in its place (the folder is the app's). Settings > Memoria > Edit Memoria.ini edits it in place.
- A mod folder copied in by hand (ModDescription.xml, or StreamingAssets / FF9_Data inside) is
  listed on the Mods page, so it can be turned on without editing FolderNames.
- `ReflectionProbe.texture` was null: Memoria's battle Toon/Realism shaders wait for it in
  HonoluluBattleMain.UpdateAmbientLight and read its name (NullReferenceException, issue 13).
  A white texture stands in. Not yet seen in a battle on the Thor.
- Second screen background pure black (the CRT filter over the old dark blue showed lines).
- Resolution 25%.
- Seen, not fixed: a dialog's box goes 1-2 frames before its text when it closes (issue 12):
  DialogAnimator.StartHideDialog shrinks the body sprite and the clip panel's baseClipRegion,
  but the phrase label is not clipped; the Hidden/... clip shaders are all found.
- Debug switches: FF9_TRACE_EXCEPTIONS=1 (every exception as thrown, caught or not),
  FF9_TEST_SAVEUI=1 (the Save/Load screen's state when it changes). Coroutines whose object is
  inactive or destroyed are logged once (TODO lines).
- The Thor's SavedData_ww.dat currently has no saves in slots 1-2 (test_load 1:2 gives ok=False;
  the Load screen shows nothing for those slots): load from Continue (the autosave) instead.

## Load Game froze on a slot's file list (0.1.5, issue 10, Reddit)

SaveLoadUI shows a slot's files inside HonoFading.FadePingPong: FadeIn starts WaitForAnimation
(WaitForSeconds d), then PingPongProcess yields WaitForSeconds d+0.01 and calls FadeOut, which
does nothing while the fade is `busy`. Both came due in the same frame, and coroutines resumed in
the order they started, so FadeOut ran before WaitForAnimation cleared `busy`: its callback (the
one that ends Loading) was dropped and the screen ignored every button. Coroutines now resume in
the order they yielded, as Unity's do. FF9_TEST_SAVE=slot:file saves the game once a field has
been up for 300 frames (0-based, as FF9_LOAD_FILE), for tests that need a save in a slot.

Game files > Saves: Import takes several files or zips at once (SavedData_ww.dat with Memoria's
SavedData_ww_Memoria_*.dat and ExtraSave_N/), putting the current ones in backup-<time>/;
Export writes them all into one zip.

## NGUI clipping (0.1.6, issue 12)

Clipped NGUI panels (dialogs, scroll lists) were drawn unclipped. Two causes: Shader.name gave the
asset's name ("Unlit - Transparent Colored") instead of the ShaderLab name ("Unlit/Transparent
Colored"), so UIDrawCall.CreateMaterial looked for "Hidden/Unlit - Transparent Colored 2", found
nothing in Memoria's ShadersLoader (StreamingAssets/Shaders/Hidden/Unlit/*.txt) and kept the plain
shader; and renderers' scripts never got OnWillRenderObject, where UIDrawCall sets _ClipRange/
_ClipArgs (with the right shader and no values, panels vanished). Object.name now returns the
ShaderLab name for shaders, and the pipeline calls OnWillRenderObject on the visible renderers'
scripts before gathering draws. A closing dialog's text now shrinks with its box.
FF9_TEST_DRAWCALLS=1 logs NGUI's draw calls (panel, shader, clip count, clip vectors) as they change.

## Memoria settings page (0.1.7)

Settings > Memoria settings: Memoria's own options as switches and choices (MemoriaSettings.java),
as its PC launcher has them: shaders, interface, battle, card game, world map camera, autosave,
Memoria cheats. Each choice is a set of Section.Key=value writes; a section with an Enabled key
([Battle], [Hacks], [Shaders]) is turned on when one of its options changes, since Memoria ignores
the section otherwise. A file set by hand to other values shows "Custom". Not here: what
Settings.apply writes at each Play (frame rate, widescreen, skip logos, boosters, speed, fast
dialog, swap confirm, analog walk, volumes). Edit Memoria.ini stays for everything else.

## GitHub issues 6, 12, 14-16 (0.1.8)

- Coroutine timing (Scripting/Coroutines.cs): a coroutine started from a script's Update ran to its
  first `yield return null` and was then resumed in the same frame's coroutine pass. Unity resumes
  it on the next frame. Tetra Master's coin toss switches the coin on, yields, then uses what the
  coin's Start sets; Start had not run, the toss threw and the match stalled (issue 16). Yields made
  outside a pass now wait for the next frame's Update pass.
- AddComponent<SpriteRenderer>() gets a "Sprites/Default" material, as in Unity (Gfx/Renderers.cs).
  Tetra Master's card cursor builds its frames that way; with no material they were never drawn.
  The board/hand QuadMistCursor's SpriteSheet has null sprites in the game files: it shows nothing
  on PC either; the cursor players see is QuadMistCardCursor.
- Music stopped after one play-through (issue 16): Memoria calls Soloud_setLoopStartPoint and
  Soloud_setLoopEndPoint on a voice, never setLooping; its soloud.dll loops a voice given a loop
  start. src/host/soloud_ext.cpp now does too. FF9_TRACE_MUSIC=1 logs the voices every 5 s.
- Menu sounds late or skipped when scrolling (issue 12): SoLoud's miniaudio backend turned Memoria's
  automatic buffer size into 2048 frames (43 ms, sounds start on buffer boundaries); 512 now
  (patches/soloud-android-buffer.sh).
- Naming screen (issue 6): OK in the keyboard box sends "\u0001" + text; TextInput sets the name and
  calls NameSettingUI.OnConfirmButtonClick. Tapping the game's own Confirm selected the name field
  again. FF9_TEST_NAMING no longer reads UIManager.Instance before a field is up (that made a stray
  UIManager whose scenes were never set, and the game stopped at the splash).
- Second screen: GameActivity hides the panels' Presentation in onStop and shows it again in
  onStart (Home left them on the bottom screen). Settings > Screens > Dim the panels in cutscenes.
- Mods copied in by hand (issue 15): ModCatalog.handMod reads the folder's own ModDescription.xml
  for the name, options and the Info page.
- GameInstall.extractRuntime deletes a runtime file it may not write before unpacking (an engine
  DLL pushed over adb is owned by shell).
- FF9_TEST_QUADMIST=opponent: a Tetra Master game 300 frames after a field is up; logs the game's
  state as it changes.
- The Lost Chapters (issue 14) patches the game at run time with HarmonyLib; AccessTools' static
  constructor fails here (MethodInvoker.GetHandler), and the game later dies with signal 11.
  Not supported.
- Mod archives are unpacked by their first bytes (ModDownloader.kind: 7z, zip; rar or a web page
  give a clear error), not by the link: KupoMail's Google Drive link has no ".7z" and the catalog no
  DownloadFormat, so its 7z was read as a zip, nothing came out and the move failed (issue 17, 0.1.9).

## The Lost Chapters and Harmony (0.1.10, issue 14)

- The mod loads its own 0Harmony.dll (Harmony 2.2.2, net35 build, with MonoMod inside) with
  Assembly.LoadFrom and patches the game at run time (8 patches: IL edits, prefixes, postfixes).
  Native/HarmonyCompat.cs makes that build work on .NET 8 Mono:
  - Assembly.LoadFrom is detoured; a 0Harmony.dll is loaded from a rewritten copy: `call` to
    System.Reflection.MethodBody members becomes `callvirt` (they weren't virtual in .NET 3.5, and
    the base class's stubs throw or return null), and AppDomain.DefineDynamicAssembly (gone) is
    pointed at the static AssemblyBuilder.DefineDynamicAssembly (MemberRef parent and signature
    patched, the AppDomain.CurrentDomain push before each call turned into nops).
  - MethodInvoker.GetHandler is replaced (AccessTools' static constructor passes the null
    Exception.PrepForRemoting; others call through reflection).
  - PatchInfoSerialization.Serialize/Deserialize keep patch lists as objects (BinaryFormatter is
    not supported on Android).
  MonoMod's DynamicMethodDefinition and its detour then work on ARM64 as they are.
  FF9_TEST_HARMONY=path/to/0Harmony.dll patches a test method (prefix, postfix, transpiler,
  re-patch, UnpatchAll) at the title and logs each step.
- The crash: after the mod loads, SIGSEGV is back to SIG_DFL, so the game's ordinary null
  reference in FieldMapActorController.MovePC on the first field killed the process instead of
  becoming a NullReferenceException (the tester's crash happened with every Harmony hook failing,
  too). The host saves Mono's SIGSEGV/SIGBUS handlers after start-up and the engine puts them back
  each frame if they changed (ff9_keep_fault_handlers, Native/FaultHandlers.cs; logs once).
- Debugging switches: FF9_TRACE_CRASH=1 (host: fault address, pc, frame-pointer walk with library
  or managed method names; installed again at the title, ff9_crash_trace), FF9_TRACE_MODCALLS=1
  (each message call into a Memoria.Scripts* assembly, so the last line before a crash names it).
  env.txt only passes FF9_TEST_* and FF9_TRACE_* variables.

## Naming screen with another release's game data (0.1.11, issue 6)

A Galaxy Fold's game data (Bundle scene 8641 behaviours, 5874 resource paths; ours 8794 / 5879)
gives the naming screen's "Name Input Field" a UIInputPSVitaExtension, a class the current
Assembly-CSharp doesn't have: the component was dropped, NameSettingUI.Awake failed (Memoria's own
recovery finds the object but no UIInput), the field stayed empty and no keyboard came. AssetDB.Script
loads a missing "...PSVitaExtension" script class as its base class (UIInput first in its fields),
and SceneLoader gives a UIInput with no label its child UILabel. Not tested on that data (we don't
have it); our data's naming screen is unchanged.

## Sound backend missing from Memoria.ini (0.1.13, issue 20)

- A Memoria.ini with no `[Audio] Backend` line (a short one, from a PC install that only kept
  changed values) left Memoria on its default backend: SdLib (0) in older Memoria, SaXAudio (1) in
  v2026.11.09, neither of which exists on Android. `SoundLib.Awake` threw (EntryPointNotFound),
  `UIManager.OnLevelWasLoaded` then threw at each scene, and the title screen drew with no hand
  cursor and took no input. `GameInstall.configureMemoria` only rewrote an existing `Backend` line.
- `Settings.apply` (every Play) now writes `Backend = 2` (SoLoud) into [Audio], adding it if absent.
- Reproduced on the Thor by deleting the line; with the fix the line comes back on Play and the
  title menu works.

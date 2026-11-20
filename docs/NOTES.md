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
- Rumble sticking (issues 39, 40): vib.VIB_service sends a level each tick only while the track's sample
  is above zero, and nothing when it drops to zero, the field changes or its caller stops (PS1 pads were
  rewritten every frame). The host treats a level not repeated for 200 ms as off (input.c ff9_rumble_now).
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

## Field changes leaked every field's assets (0.1.14, issue 16)

- Two crashes (SIGSEGV in the GPU driver's memcpy under glDrawElements, at a field's first frames)
  came with the game at 1.6 and 2.0 GB. FF9 frees each field's runtime textures, meshes and
  materials only through Resources.UnloadUnusedAssets (SceneDirector.ChangeScene), which was a
  stub. FF9_TEST_FIELDS=map,map,...[:frames] (Core/TestLoad.cs) changes fields round and round and
  logs memory: +~40 MB, ~1100 meshes and ~4 textures per change.
- Core/Unloader.cs: candidates (runtime textures, meshes, materials, sprites, render textures not
  temporary or bound, and NAsset copies of file assets) leave the registry and their records drop
  the managed object; a GC with weak handles shows which are still reached. Up to three rounds,
  ~120 ms on the Thor. FF9_TEST_NO_UNLOAD=1 turns it off.
- What kept things alive: Renderer.material's copies of file materials are NAsset records (not
  NMaterial), so they were never candidates, and each held a model's 512x512 render texture. NAsset
  now frees its content's GL objects too (it didn't when destroyed), and Pipeline's variant table
  (keyed by every material ever drawn) drops materials marked Gone.
- The font atlas is held by NFont directly: HideFlags.DontUnloadUnusedAsset.
- 68 changes after: ~70 textures, ~2000 meshes, RSS flat at ~1.25 GB (1.7 GB and climbing at 48
  before). "Error Env Play()" boxes in that test come from warping out of the story's order (shown
  with the unloader off too).

## Imported copy without Memoria's game code (0.1.15, issue 20 again)

- 0.1.13's Backend = 2 didn't help the tester: their Assembly-CSharp.dll is the plain game's (its
  log has the plain game's "40 BundleScene.Awake" line, there is no Memoria.log, and SoundLib
  still chose SdLibAPIWithProLicense), with a Memoria.Prime.dll left beside it (a PC copy whose
  game files Steam put back), so the import's check passed.
- GameInstall.memoriaPatched(): Memoria's Assembly-CSharp.dll has "SdLibAPIWithSoloud", the plain
  game's doesn't (cached by size and time). Without it the copy isn't ready, the status says
  "Memoria missing", and Game files has Install Memoria: JobService.MEMORIA fetches Memoria's
  installer (as the Steam download does) and installs it into the copy, keeping Memoria.ini's
  settings, then configureMemoria.
- Thor: the plain DLL put in place showed the message and button; Install Memoria (1411 files)
  gave back the same Assembly-CSharp.dll byte for byte, and the title took input.

## Select (Active Time Events) and Unity's named buttons (0.1.16, issue 16)

- Input.GetButton/GetButtonDown/GetButtonUp returned false. FF9's Input Manager buttons are named
  after pad buttons ("JoystickButton6"), and HonoInputManager reads its joystick keys through them
  as well as through key codes. Select (Back, joystick button 6) came only by name, so an Active
  Time Event never opened. Named "JoystickButtonN" now reads the pad (FF9_TRACE_BUTTONS=1 logs
  presses asked for by name).
- A press by name counts for the first ask in a frame only: while catching up (a field loading),
  the game runs several update loops in one frame and each saw the press, so one A during the
  Tantalus scene's start went through several lines. With the guard the same presses step through
  the scene exactly as before the change (checked against the old engine on the Thor's autosave in
  that scene); A advances one line, Back does nothing in a dialog. An ATE itself was not reached.

## The panels in the game's font (0.1.17)

- The bottom screen's panels (CompanionView) drew in Android's sans-serif. The engine now copies
  the first font file the game loads (TBUDGoStd-Bold, its text font, 3.5 MB) from the player's
  game files to files/worldmap/game_font.ttf (Text/Fonts.cs ShareWithPanels; written again only
  when its size differs), and the panels switch to it once it is there (checked once a second
  until found). The font is bold already, so the panels' regular and bold text both use it.
- Java's System.getenv doesn't see Os.setenv's variables: the panel builds the folder path from
  GameInstall, as GameActivity does for FF9_WORLDMAP_DIR.

## Get mods: the list scrolls on its own (0.1.18)

- The whole Get mods page was one ScrollView, so the categories went up with the list. Now the
  header and search stay, and the categories and the list are each in their own ScrollView
  (refresh keeps both positions, as the one page ScrollView's was kept). Touch and the d-pad both
  scroll the list only; the focused row is kept in view.
- Testing on the Thor: a tap meant for the list landed on a mod's Install (Alternate Fantasy got
  installed and turned on); its folder, .version and the Memoria.ini change were taken out again.
  Use key events, not taps, on Get mods.

## The battle swirl (0.1.19, issue 24)

The swirl stayed black. Three things, all needed:
- WaitForEndOfFrame coroutines ran after eglSwapBuffers, when the back buffer is undefined (black
  on Adreno): SFX_Rush.CreateScreen's ReadPixels captured black. Player now renders, runs the
  EndOfFrame coroutines, then presents (Render / Present), as Unity does.
- OnRenderImage wasn't supported. SwirlScene's camera has BattleSwirl, whose OnRenderImage calls
  SFX_Rush.PostProcess (Blits through the Rush_RT_ render textures to the screen). RenderCamera
  now draws a camera with image effects into a temporary render texture and passes it through
  them, the last into the camera's own target (lookup cached per script type).
- The swirl and the Event Fader Panel's subtractive fade (FF9Wipe_FadeOutEx(5) in SFX_Rush's
  constructor) step together at 30 fps from Time.deltaTime. _Swirl calls UnloadUnusedAssets as
  the swirl starts; ours takes ~100 ms at once, so the first frame caught up several steps with
  the fade nearly black. Unloader.Run now calls Clock.Resync() (Unity's unload is asynchronous).
- Result on the Thor (FF9_TEST_FIELDS=105,104:400:6 changes fields with SwirlInBlack): the
  captured screen zooms and turns, brightens, then fades to black over ~8 frames at 60 fps (the
  game's own timing). A real battle's swirl was not reached here.
- That short swirl is Memoria.ini's BattleSwirlFrames = 0 (Memoria's default: SFX_Rush starts the
  fade-out at frame 0; the launcher's "Skip the battle swirl" is on). At 115 (PlayStation) the
  same test shows the full swirl, brightening and turning then darkening: SwirlScene to Loading
  4.2 s on the Thor. Issue 24's later "no swirl" with 0.1.23 was this setting.
- FF9_TRACE_FX=1 logs brightness of the camera picture, the Rush textures, the capture, the
  screen after each stage, the cameras, the fader's colours; FF9_TRACE_FX=shots writes the final
  screen of each image-effect frame to files/fx_N.png.

## Battle effects gone in 0.1.19 (0.1.20, issue 25)

- The battle camera has BattleMapCameraController, whose OnRenderImage is PSXTextureMgr.PostBlur
  (a Blit of the picture, keeping a copy for blur effects). Since 0.1.19 ran OnRenderImage, that
  camera drew into a temporary texture, but its OnPostRender (SFX.PostRender -> SFXRender.Render)
  starts with RenderTexture.active = null and drew the effects straight to the screen, which the
  Blit then covered. Unity's null target during such a camera is the camera's own (CameraTarget):
  RenderTargets.CameraTarget now stands in for null while a camera with image effects draws.
- Checked on the Thor with -memoria-battle 336 -memoria-seed 1 (which now gets to the battle: it
  goes through the swirl, which used to stall at the title): Blank's attack shows its hit flash.

## Voice acting: Thread.Start(object) (0.1.21, issues 22, 23, 26)

- KupoMail's voices never played and dialogs raced ahead and stacked. Memoria.log: each line's
  VoicePlayer.StartSound threw InvalidOperationException (ThreadWrongThreadStart): it makes its
  watcher with `new Thread(ThreadStart)` and calls `Start(soundProfile)`. .NET Framework / Unity's
  Mono ignored the argument; .NET 8 throws, before the sound is played. With
  AutoDismissDialogAfterCompletion the dialog then closed at once.
- Native/Compat.cs replaces Thread.Start(object, bool, bool) (the one Start(object) calls) with
  the same steps minus the ThreadStart check (reflection on _startHelper, StartCore).
  FF9_TEST_THREADSTART=1 checks both kinds of thread at startup (both ran on the Thor). Not yet
  played with KupoMail itself.

## Moguri backgrounds in 32-pixel tiles; 30 fps movies (0.1.22, issues 28, 27)

- Issue 28: every field background was scrambled squares, half black. The Moguri Mod from its
  older installer (MoguriFiles, MoguriSoundtrack, MoguriVideo) with `[Graphics] TileSize = 32`:
  its atlas (2924x2992 = 43x44 tiles of 64 + 2 border) was cut in 36-pixel cells. Moguri 9
  (MoguriMain) sets 64 in its own Memoria.ini, which Memoria reads over the main one (the Thor:
  main 32 + MoguriMain's 64 drew right). Settings.tileSize, at each start: an active Moguri
  backgrounds folder without its own TileSize gets 64; 32 comes back when it is off (the pref
  tile_size_64 says the launcher set it).
- Issue 27, 30 fps movies (MoguriVideo) choppy. Two causes, both measured with
  FF9_TRACE_MOVIE (1: draws without a new frame / skipping frames; 2: each draw's target and
  wall time) and FF9_TRACE_PROFILE=n (FF9_PROFILE's name in env.txt; it now logs the slowest scripts and
  the frame's parts):
  - MovieMaterial.Open sets playSpeed = 15 / the movie's fps (0.5 here). With Memoria's VSync on
    the clock adds deltaTime * playSpeed, so it ran at half speed and only the jumps to the
    audio position (66 ms behind) moved it on. Compat replaces MovieMaterial.Load (the same
    steps, then playSpeed 1 unless the FMV fixed-step path, VSync off, is the one used).
  - The opening's field (map 70) has no walkmesh (FieldMap.LoadFieldMap returns before making
    one). With PSXMovementMethod on, MovePC's slope line threw in every frame, inside
    HonoBehaviorSystem's loop over new behaviours, before that list was cleared: the same
    behaviours were added to its main list again each frame (4411, 16072, 27694... every 300
    frames), and the game slowed from 30 fps to 15 through the movie. Compat.FieldTick turns
    PSXMovementMethod off in memory while the field has no walkmesh (or no field map yet).
  - On the Thor, the opening FMV in MoguriVideo: before, 2822 draws in 121 s, 778 without a new
    frame, 755 skipping; after, 3632 draws, 68 and 4, frame time flat. The decoder also shows
    the frame nearest the target now (half a frame ahead), not the last one before it.
- Not answered here: issue 29 (a second Blank after Steiner's attack) with AlternateFantasy and
  BlackCat active.

## Portraits with older Memorias, mod rows on 1240x1080, panels follow the game (0.1.23, issues 32, 30, 31)

- Issue 32, letters instead of portraits on the panels. The reporter's game has Memoria
  2024-08-24 (Memoria.log), which has no FF9UIDataTool.FaceAtlas (added later; it returns the
  status screen portrait's atlas: StatusScene.CharacterDetailPanel child 0, child 0, UISprite).
  Companion.ExportSprite got null and logged "ui sprite Face/faceNN: not in the atlas".
  Companion.StatusFaceAtlas reads that sprite's atlas itself when the property isn't there.
  FF9_TEST_FACE_FALLBACK=1 takes that path on a newer Memoria: on the Thor the exported
  ui_Face_face00/01.png were byte-identical to the ones from FaceAtlas.
  The same report's black bars in the Ice Cavern garden (map 306) are Memoria's: NarrowMapList
  gives that field 320 pixels, so it stays 4:3 on PC too. Its Memoria also skips the 0.1.22
  movie clock ("this Memoria's MovieMaterial differs").
- Issue 30, Mods page on a Retroid Pocket Classic (1240x1080, 320 dpi): the name had a few
  pixels beside the buttons. NameAndButtons (from FF7Recomp's #58) puts the buttons under the
  name when they'd leave it under 200 dp; the buttons are a FlowRow. Checked with test.SCREEN
  1240x1080@320 and at the Thor's own width (unchanged).
- Issue 31, Ayaneo Pocket DS: the game stays on the small (main) screen with Swap screens on.
  Not reproduced: on the Thor the game goes to the second display. FF7Recomp's #49 showed the
  Pocket DS moving the game between screens after it starts. GameActivity now writes the screens
  (id, name, size, flags), the game's display and the swap setting to display.txt, and places
  the panels again when the game is on another display than they were placed for
  (onConfigurationChanged; on the Thor a move recreates the activity, which places them anew).
  Thor: am display move-stack of the game's task to 0 and back: panels followed, game kept going.

## The battle swirl plays on new installs (0.1.24, issue 24)

- Memoria ships BattleSwirlFrames = 0 (skip the swirl); players compared with the game without
  Memoria and took it for a bug. MemoriaInstall.install: when the game had no Memoria.ini (a Steam
  download, or Install Memoria on a game without Memoria), newIniDefaults sets it to 115 (PlayStation,
  Steam). An existing or imported Memoria.ini keeps its value; the launcher's "Skip the battle swirl"
  changes it either way.
- Tested on the Thor with TestControl STEAM_DOWNLOAD op memoria into scratch folders: an empty one
  got 115, one holding a copy of the player's Memoria.ini (0) kept 0. The regex keeps CRLF endings.

## Object lookups slowed with play time (issue 33)

- Registry ids are never reused (Unity's instance ids aren't), and Registry.All walked every id
  ever given out. The reporter's log after ~45 minutes: 40,000 objects, 567,000 ids, unloads
  167 -> 740 ms. Battle scripts call GameObject.Find / FindObjectsOfType every frame
  (HonoluluBattleMain.Update, SceneDirector.Update), and each walked the whole table.
- Registry keeps its records in a list in id order; All walks that (skipping removed ones), and
  Compact drops removed records at the end of each unload and when the list doubles. Not while
  the Unloader has records hidden (Registry.Hiding).
- FF9_TEST_REGISTRY_PAD=n starts with n ids used. Thor, battle 336, FF9_TRACE_PROFILE=120:
  update 3.7 ms per frame unpadded; 6.1 ms with 550000 (HonoluluBattleMain.Update 1.0 -> 2.65,
  SceneDirector.Update 0.8 -> 1.8); 4.0 ms with the fix. FF9_TEST_FIELDS: unloads stay ~110 ms.
- The same report: thermal status 3 (severe) on an S22 Ultra (Snapdragon 8 Gen 1); 18-26 FPS in
  its battle screenshots is probably mostly throttling.

## Heavy battle effects: frame drops during Meteor (issue 33)

- Behemoth's Meteor Counter (battle 957, Memoria) on the Thor went to 14-20 FPS. FF9_TRACE_PROFILE showed
  BattleMapCameraController.OnPostRender at ~50 ms a frame. Meteor plays in Memoria's SFXDataMesh.Runtime
  mode: each frame runs the game's effect plugin (SFX_Update, x86-64 in x64emu) and rebuilds ~16 meshes
  (~200,000 vertices) drawn with Material.SetPass + Graphics.DrawMeshNow.
- Fixed in the engine:
  - Mesh.vertices/triangles recomputed bounds on every set (~14 ms); bounds are now computed when read.
  - NMesh.Upload allocated a new ~800 KB vertex array per upload (12 MB of large objects a frame, 2-3
    full GCs per 30 frames); it now reuses one staging array and its index buffers.
  - GameObject.Find("Battle Camera") scanned every object several times a frame; plain-name lookups are
    cached while the answer is still the first match (GameObjectIcalls.HierarchyVersion, bumped by
    activation changes, renames and reparenting).
  - x64emu: step() and its decode/flag helpers forced inline into the run loop (8.7 -> 7.0 ns per
    instruction). A lazy parity flag made no measurable difference and was not kept.
- Worst 30-frame windows of Meteor on the Thor: 64 ms -> ~25 ms a frame (16 -> ~40 FPS); field and world
  map unchanged (60 FPS). What is left: SFX_Update ~10 ms (~1.3 M guest instructions a frame), Memoria's
  managed mesh building ~5 ms, uploads/draws ~3 ms. Reaching 60 during Meteor would need a faster x64emu
  (pre-decoded or translated code).
- Profiling added (FF9_TRACE_PROFILE): "prof now" (DrawMeshNow bind/upload/draw, vertices, texture uploads,
  ReadPixels), "prof calls" (mesh setters, Material.shader, Camera.main, GameObject.Find and
  OnPostRender's steps, which Native/ProfPostRender.cs splits by replacing it with the same steps),
  "prof sfx plugin" (time in each plugin export, game callbacks, guest instructions; ff9_sfx_prof in
  sfx_plugin.c). simpleperf needs <profileable android:shell="true"/> in a local build.
- FF9_TEST_IMMORTAL=1 holds the party's HP at 9999 in battle, for late-game test battles. Battle+
  (Memoria's Battle Assistance booster) is not enough: btl_para only ignores damage that wouldn't kill.

### The effect plugin's hot loops (src/host/sfx_fast.c)

- SFX_Update runs only on frames where an effect plays; on those (battle 957, 100 rounds, several Meteor
  Counters) it ran ~2.3 M guest instructions in 14.4 ms. FF9_TRACE_X64_HIST (instructions per guest
  address) showed 56% of them in two loops: ClearOTagR at RVA 0x2d60 (4095 ordering-table entries, each
  given the 24-bit address of the one before by searching the plugin's region table: 44%) and the walk
  back along those addresses at 0x1850 (12%).
- Both run as C now: the loop's first instruction becomes D6 <n> (not an x86-64 instruction; x64emu runs
  hook n), and the hook leaves the registers, flags and memory the guest code would, then continues where
  it would. Only if the loop's code bytes hash to the known DLL's; otherwise everything is interpreted.
  FF9_TRACE_X64_CHECK=1 runs each call both ways and compares (over 1 M loop runs on the Thor, including
  Meteor, no difference); FF9_TRACE_X64_SLOW=1 turns the hooks off.
- Same build, hooks off vs on, frames with an effect running: SFX_Update 14.4 -> 9.9 ms, the frame
  29.5 -> 25.6 ms; the 20 worst frames 43 -> 41 ms.
- Tried and not kept: a decoded-instruction cache (each common instruction decoded once into a 48-byte
  record, run from it after). It matched the interpreter on 940 M instructions but was no faster: with
  simpleperf, 30% of x64_run went to the load of the cache record (the records don't fit the CPU caches
  the way the few KB of hot guest code bytes do) and 16% to the dispatch branch. The interpreter's time
  is dispatch and state traffic, not decoding. What's left is spread out: the plugin's effect-script
  dispatcher (0xe250 and its handlers), the GTE-style vertex transform (0x3e80), and more; the next real
  step is translating guest code to ARM64.

### Translating the plugin to ARM64 (src/host/x64jit.c)

- The player's plugin DLL is translated on the device as it runs: each basic block, the first time
  it's reached, becomes ARM64 code (32 MB of RWX memory; if mmap refuses it the interpreter runs as
  before). Nothing translated is stored or shipped.
- Guest registers stay in X64Cpu (loaded/stored per instruction); in a block the five flags live in
  w20-w24 and a flag is only computed when a later instruction or the block's end can read it.
  Translated: moves, ALU (not adc/sbb), test, inc/dec, imul, shifts (when their flags are dead),
  cmov/setcc, push/pop, jcc/jmp, call/ret inside the image, cbw/cwd. A block stops before anything
  else (SSE, string ops, prefixes, indirect call/jmp, the D6 hooks) and the interpreter runs it:
  ~21 K of ~2.4 M instructions per update on battle 957.
- FF9_TRACE_X64_JITCHECK=1 replays every block in the interpreter from the same state (its memory
  writes undone) and compares registers, flags, rip and written memory: 209 M blocks over a 90-round
  battle 957 (six Meteor Counters) and 124 M more in battles 568, 922, 940 and 950, no difference.
  FF9_TRACE_X64_NOJIT=1 turns it off; FF9_TRACE_X64_JITSTATS reports coverage.
- Battle 957, 100 rounds, same build, interpreter vs translated, frames with an effect running:
  SFX_Update 10.1 -> 3.2 ms (4.0 -> 1.4 ns per guest instruction), frame 25.8 -> 19.7 ms, the 20 worst
  frames 40 -> 30 ms. What's left of those frames is mostly managed code (Memoria's mesh building,
  our uploads and draws). More from the translator (blocks jumping straight to each other, guest
  registers kept in host registers) could save about another 1.5 ms there.

## Battle smear; a mod's sound backend (0.1.28, issues 33, 38, 35)

- Issues 33/38, smeared doors and spell effects that stayed in battle: the Battle Camera clears
  depth only and has an image effect (BattleMapCameraController.OnRenderImage, PostBlur), so since
  0.1.19 it drew into a pooled temporary texture holding an old frame. Where the battle scene draws
  nothing (a doorway, the opening above Prima Vista's stairs, above the walls) old frames showed
  through and moving things left trails. Unity gives a camera that doesn't clear colour the
  target's current picture: Pipeline.RenderCamera now copies the target (or the screen) into the
  temporary texture first (glBlitFramebuffer). Thor, battle 336: white spikes left in the opening
  above the stairs before, clean black after; 59 FPS.
- Issue 35, Ferny Fantasy IX: no cursor at the title. Memoria reads each active mod's Memoria.ini
  over the main one, and that mod's sets [Audio] Backend = 1 (SaXAudio), so SoundLib.Awake threw
  as in issue 20. Compat replaces Configuration.Audio.Backend's getter with 2 (SoLoud) and logs
  "sound backend: ... asks for N" once. Thor: a scratch mod folder with Backend = 1 in its
  Memoria.ini, then the title with its cursor, moving with the pad, no SoundLib exception.

## Font choice (0.1.29, issue 36)

- Memoria's [Font]: Enabled = 0 is the game's font. Its launcher's "Use PSX font" sets Enabled = 1,
  Names = "Alexandria", "Garnet": EncryptFontManager.SetDefaultFont takes Names[0] from Memoria's
  own font bundle (EmbeddedAsset/FA), which already worked here. Any other name must be an OS font
  (Font.GetOSInstalledFontNames, then Font.CreateDynamicFontFromOSFont).
- Text/Fonts.cs: the OS fonts are files/fonts (the player's, next to the game folder),
  /system/fonts and /product/fonts, .ttf and .otf, each named by its file name without the
  extension. Internal_CreateDynamicFont loads the first of the names found there (FontFile reads
  glyf and CFF); otherwise it borrows the first loaded font's outlines as before.
- Launcher: Memoria settings > Interface > Font (MemoriaSettings.fontOpt): the game's, PlayStation,
  then FontList.java's fonts (outlines and a cmap with 'A' and 'a': no colour emoji, no fonts
  without Latin letters). A row with more than 6 choices opens a list.
- MemoriaSettings.apply turned a section's Enabled back to 1 after a choice set it to 0; a choice
  that sets Enabled itself now keeps it.
- Tested on the Thor: PlayStation font, ComingSoon from /system/fonts and a copy in files/fonts
  draw the battle menu (-memoria-battle 336); the list opens on the current font, and the game's /
  PlayStation choices write Memoria's values.

## Language, stretched picture, portrait (0.1.30, issues 4, 32, 2, 8)

- Language (Settings > Language): the game keeps its language in its system save data
  (SettingsState.ReadSystemData: metaData.SelectedLanguage; PlayerPrefs "Language" is only written
  after). Memoria's [VoiceActing] ForceLanguage overrides it, but Memoria reads no key of a section
  whose Enabled is 0 (and turning voice acting on changes how dialogs close). The launcher keeps
  "language" (-1, or 0-6: US UK JP GR FR IT ES); GameActivity passes FF9_LANGUAGE; the engine
  (Native/Compat.LanguageTick, end of the first frame) sets Configuration.VoiceActing.ForceLanguage
  before GUIManager.Awake reads the system data. Tested: Español gives the Spanish title
  (CONTINUAR, ES); back to "As chosen on the title screen" gives English again.
- Picture (Settings > Display > Picture): 1 stretches narrow areas: Memoria's PSXCameraAspect
  narrows the field camera's Camera.rect (NarrowMapList fields; 4:3 without widescreen); with
  FF9_STRETCH the pipeline draws a full-height camera at least half wide across the whole width,
  keeping its projection (Aspect from its rect). 2 also renders 16:9 on wider screens and lets the
  display stretch it (GameActivity: setFixedSize from the view's layout, with Resolution).
  Battles already fill a 2316x1080 screen; fields with narrow cameras did not. Tested with
  wm size 2316x1080 in Baku's room (narrow): both fill; speech bubbles still point at the speaker.
- Portrait (Settings > Display): FF7Recomp's layout. SENSOR_PORTRAIT; while the window is tall the
  game is a 16:9 strip on top (GameFrame), CompanionView under it (weight 2), the pad under that
  (weight 3); sideways it is the usual layout. The second-screen Presentation is not used. The
  Thor's main screen never turns upright (wm size and user-rotation don't change it); tested in a
  tall multi-window (am start --windowingMode 6): the layout came up, 60 FPS, panels with
  portraits, the pad's Down moved the battle cursor (a press of 150 ms; adb's tap is too short).

## On-screen right stick, L3 and R3 (0.1.33, as FF7Recomp's GitHub #62)

- Settings > On-screen controls > Size and layout: "Right stick, L3 and R3" (pref `touch_extra`,
  off by default; TestControl SET `--ez touch_extra true`). TouchControls adds L3 / R3 under
  SELECT / START (XInput 0x40 / 0x80: Unity joystick buttons 8 / 9, Memoria's L3BindCheat /
  R3BindCheat) and an analog stick above the face buttons, a fifth draggable group (the layout
  string's optional fifth offset, so older layouts still load). The finger that lands on the
  stick holds it until it lifts; the knob goes out as RX/RY (±32767, Y up), used by GameActivity
  while the real right stick is centred (Memoria's world map camera, [AnalogControl] RightStickCamera).
- Tested on the Thor (touch_controls 1, touch_extra true, world map save 1:2): the stick, L3 and R3
  show; dragging the stick turns the world map camera, which stops when the finger lifts (the knob
  re-centres). L3's high speed mode had no visible effect to measure; its bit takes the same path
  as the other on-screen buttons.

## On-screen BOOST button (0.1.34, GitHub #44)

- Phones have no bottom screen, so the Extras page's booster buttons weren't reachable in
  landscape. TouchControls has a BOOST pill under START (under R3 with the extras; part of the
  START group for the layout editor). It opens a row at the top middle: High speed, No battles,
  Battle assist, 9999 damage, each with ON/OFF read from the companion state (`speed`, `noenc`,
  `bplus`, `dmg`, refreshed every 0.5 s while open). A tap sends CompanionView's command
  (GameActivity.commandFromPanel 1/4/5/6, engine Core/Companion.cs RunCommands); BOOST again closes
  it. Fingers on BOOST or the row press no pad button (BOOST is bit 22, masked off).
- Tested on the Thor (touch_controls 1, world map save 1:2): the row opens, High speed and 9999
  damage turn on and off (log "panel: booster HighSpeedMode on/off", "Attack9999 on/off", the
  pills turn gold), BOOST closes the row, and Y still opens the menu.

## Steam Cloud Upload goes to Steam at once (0.1.35, GitHub #21)

- GameActivity watches the Cloud folder (FileObserver CLOSE_WRITE, not MOVED_TO: the sync's own
  download is a rename) when Steam Cloud saves are on and Game files > Saves > "Upload to Steam
  right away" (pref `cloud_upload_now`, default true) is ticked. When the engine's Upload writes
  FF9_CloudSaveFile_ww.sav (or _jp), a thread in the game's process runs SteamFiles.syncCloud,
  with toasts: "Sending your Cloud save to Steam...", then "Your Cloud save is on Steam.", or why
  not (offline, or the error; the launcher's sync after the game is the safety net). A second
  write while sending sends once more afterwards. With the option off nothing is watched.
- Tested on the Thor, signed in to a Steam account that doesn't own FF9 (ownership ticket
  AccessDenied for 377840), so nothing could reach Steam: writing the Cloud file into the folder
  started the watcher, showed the first toast, and logged the sync's "does not own" failure and
  its toast. With the option off, the same write started nothing. A real Upload to Steam with an
  owning account is untested.
- Why Upload was greyed out on the Thor (looked into after the release): not a bug. The Cloud
  menu opens with "Connecting to server...", then (no Cloud file yet) "No saved data in cloud."
  for 1.5 s (CloudUI.DisplayCorruptAccessDialog, LastErrno 7 CloudFileNotFound), then the
  buttons. Upload sends the slot saves: WinIosAndrSharedDataSerializer.LoadCloudSyncPreview reads
  only the latest slot (SharedDataBytesStorage.GetLatestSlotAndSave), and CloudUI.CheckData
  enables Upload only when that preview exists. The Thor's saves have no slot save (only
  Memoria's autosave, at the start of the game before the first moogle; Load Game's folders don't
  open), so LatestSlot is -1 and Upload stays grey, as on the PC. Pressing the real Upload needs
  a slot save first.

## Ability > Equip and Key items lists drew empty (0.1.36, GitHub #45)

- Symptom: Ability > Equip showed an empty box (scroll arrows and the cursor in the bottom-right
  corner, AP stars in Info); Item > Key the same. Use, Item > Use and battle lists were fine.
- Cause: Memoria's interface options (MenuAbilityRowCount 9, MenuItemRowCount 12) resize these
  lists with GOSubPanel.ChangeDims while their panels are still inactive. The list then fills on
  its first SetActive(true): RecycleListPopulator.InitTableView builds the rows and calls
  Invoke("RepositionList", 0). The engine ran that invoke the same frame, after Update, but the
  newly activated UITable/UIScrollView only got Start the next frame, and UITable.Start's own
  Reposition laid the rows out again, off the clipped panel (no draw calls for its SubPanel).
- Unity queues Start for behaviours enabled during Update on the delayed-call queue ahead of
  Invoke and yield-null coroutines, so it runs that frame before them and before LateUpdate.
  Player.Frame now runs RunStarts again after Update, before the invokes.
- Tested on the Thor: Ability > Equip (Zidane, 3 columns of 9 rows), Item > Key, Item > Use,
  battles 336 and 73 (command menu, item list), field and world map.

## On-screen left stick (0.1.37, GitHub #44)

- Settings > On-screen controls > Size and layout: "Left stick instead of the D-pad" (pref
  `touch_left_stick`, off by default; TestControl SET `--ez touch_left_stick true`). TouchControls
  draws a knob in the D-pad's ring; the finger that lands on it holds it until it lifts, and the
  knob goes out as LX/LY (Listener.pad now carries lx/ly), used by GameActivity while the real left
  stick is centred. Same group as the D-pad in the layout editor.
- Tested on the Thor (touch_controls 1, world map save 1:2): a full push left runs and the knob
  re-centres on release (he stops), a light push moves the knob part way, and in the menu two
  short pushes down moved the cursor (with the menu's own hold repeat, Item to Card). With the
  option off the D-pad still draws and lights the pressed arrow.

## Start queue after menus; touch vibration (0.1.38, GitHub #47, #44)

- #47, the game slower after the Ability menu: behaviours enabled while their object was then
  deactivated before Start (the menus' rows and panels) stayed in Player.pendingStart for the rest
  of the session, and RunStarts sorted and walked that queue twice a frame. Thor, world map save
  1:2, FF9_TRACE_PROFILE=120: "waiting to start" 284 -> 1008 after Ability > Equip, logic 6.10 ->
  8.5 ms in the menu and 6.54 ms after it. RunStarts now drops entries that can't run (Awaken
  queues them again on enable/activation) and destroyed behaviours leave the queue
  (Player.Forget). After: waiting 4 -> 2 -> 0, logic 5.9 -> 7.6 in the menu -> 6.0 after; battle
  336 fine. The reporter's own sharper slowdown didn't reproduce with this save.
- #44: Settings > On-screen controls > Size and layout: "Vibrate when a button is pressed" (pref
  `touch_vibrate`, on by default; TestControl SET `--ez touch_vibrate false`) turns off the
  touch controls' haptic feedback (setHapticFeedbackEnabled).

## Second-screen panels blank with some saves (0.1.39, GitHub #49, #46)

- Companion.Build's `ready` needed party.member[0]: a party whose first slot is empty (Zidane
  alone in Lindblum in slot 2; Dagger and Zidane at Dali with Vivi's slot empty) kept "ready" 0,
  so the panels stayed black and ignored touch for as long as that party lasted (it looked
  tied to imported saves). Now any member counts. Both reporters' saves on the Thor: black on
  0.1.38, panels on 0.1.39.

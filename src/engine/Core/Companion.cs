// State for the app's second-screen panels (AYN Thor's bottom screen, CompanionView.java): a small
// JSON snapshot, ten times a second, handed to the host (ff9_companion_set) for Java to poll.
// Read through reflection so the engine does not bind to Memoria's types at compile time; the
// members are looked up once and cached.
//
// Besides the header, party and boosters: what Scan would tell about each enemy (elements, what is
// left to steal, drops), the abilities the party's gear still teaches, the inventory, and the
// counters the game keeps but never shows. Portraits and item icons are cut from the player's own
// game at runtime (the UI atlases) into FF9_WORLDMAP_DIR, like the world map.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace FF9Engine.Core;

public static class Companion {
    [DllImport("__Internal")] static extern void ff9_companion_set([MarshalAs(UnmanagedType.LPUTF8Str)] string json);
    [DllImport("__Internal")] static extern int ff9_companion_take();

    // the panels' buttons (CompanionView): boosters as the game's own booster menu sets them
    const int CMD_SPEED = 1, CMD_LOAD = 3, CMD_BATTLES = 4, CMD_BPLUS = 5, CMD_DMG = 6;
    // command -> BoosterType (BattleAssistance 0, HighSpeedMode 1, Attack9999 3, NoRandomEncounter 4)
    static readonly int[] BoosterOf = { -1, 1, -1, -1, 4, 0, 3 };

    static void RunCommands() {
        for (int cmd; (cmd = ff9_companion_take()) != 0; ) {
            try {
                if (cmd == CMD_LOAD) { TestLoad.LoadAutosaveNow(); continue; }
                if (cmd < 0 || cmd >= BoosterOf.Length || BoosterOf[cmd] < 0) continue;
                int type = BoosterOf[cmd];
                var settings = Get(typeof(FF9StateSystem), "Settings")!;
                var active = (bool[])Get(settings, "IsBoosterButtonActive")!;
                bool on = !active[type];
                var bt = T("BoosterType")!;
                object boosterValue = Enum.ToObject(bt, type);
                settings.GetType().GetMethod("CallBoosterButtonFuntion", F)!.Invoke(settings, new object[] { boosterValue, on });
                active[type] = on;
                var ui = typeof(PersistenSingleton<>).MakeGenericType(T("UIManager")!).GetProperty("Instance")!.GetValue(null)!;
                var booster = Get(ui, "Booster");
                booster?.GetType().GetMethod("SetBoosterHudIcon", F)?.Invoke(booster, new object[] { boosterValue, on });
                Log.Info($"panel: booster {boosterValue} {(on ? "on" : "off")}");
            } catch (Exception e) { Log.Warn("panel command " + cmd + ": " + e.Message); }
        }
    }

    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly bool enabled = Environment.GetEnvironmentVariable("FF9_COMPANION") != null;
    static float next, nextSlow;
    static bool failedOnce;

    // ---- cached reflection
    static readonly Dictionary<(Type, string), MemberInfo?> members = new();
    static readonly Dictionary<string, Type?> types = new();
    static readonly Dictionary<(Type, string), MethodInfo?> methods = new();

    static Type? T(string name) {
        if (!types.TryGetValue(name, out var t)) types[name] = t = Type.GetType(name + ", Assembly-CSharp");
        return t;
    }

    static bool lastCine;

    static object? Get(object? o, string name) {
        if (o == null) return null;
        var t = o as Type ?? o.GetType();
        var target = o is Type ? null : o;
        if (!members.TryGetValue((t, name), out var m))
            members[(t, name)] = m = (MemberInfo?)t.GetField(name, F) ?? t.GetProperty(name, F, null, null, Type.EmptyTypes, null);
        return m switch { FieldInfo f => f.GetValue(target), PropertyInfo p => p.GetValue(target), _ => null };
    }

    static object? Call(Type? t, string name, params object?[] args) {
        if (t == null) return null;
        if (!methods.TryGetValue((t, name), out var m))
            methods[(t, name)] = m = Array.Find(t.GetMethods(F), x => x.Name == name && x.GetParameters().Length == args.Length);
        return m?.Invoke(null, args);
    }

    static object? CallOn(object? o, string name, params object?[] args) {
        if (o == null) return null;
        var t = o.GetType();
        if (!methods.TryGetValue((t, name), out var m))
            methods[(t, name)] = m = Array.Find(t.GetMethods(F), x => x.Name == name && x.GetParameters().Length == args.Length);
        return m?.Invoke(o, args);
    }

    static long L(object? v) => v == null ? 0 : Convert.ToInt64(v);
    static string S(object? v) => v as string ?? "";

    public static void Tick() {
        if (!enabled) return;
        RunCommands();
        if (Clock.Unscaled < next) return;
        next = Clock.Unscaled + 0.1f;
        bool slow = Clock.Unscaled >= nextSlow;
        if (slow) nextSlow = Clock.Unscaled + 1f;
        try {
            ff9_companion_set(Build(slow));
        } catch (Exception e) {
            if (!failedOnce) { failedOnce = true; Log.Warn("companion: " + e); }
        }
        ExportQueued();
    }

    // ---- the world map and UI sprites on the panels: saved from the player's own copy into FF9_WORLDMAP_DIR
    static readonly string? mapDir = Environment.GetEnvironmentVariable("FF9_WORLDMAP_DIR");
    static readonly HashSet<string> exported = new();
    static readonly Queue<(string atlas, string sprite)> spriteQueue = new();
    static readonly HashSet<string> spriteSeen = new();

    static void WorldMapInfo(StringBuilder b) {
        var ui = T("WMUIData");
        if (ui == null) return;
        try {
            int active = Convert.ToInt32(Get(ui, "ActiveMapNo") ?? 1);
            string name = active == 1 ? "world_map_full_all" : "world_map_full_mistcontinent";
            b.Append(",\"wmap\":"); Str(b, name);
            if (Get(ui, "MainCharacterPosition") is UnityEngine.Vector2 pos) b.Append(",\"wx\":").Append(Fl(pos.x)).Append(",\"wy\":").Append(Fl(pos.y));
            b.Append(",\"wrot\":").Append(Fl(Convert.ToSingle(Get(ui, "MainCharacterRotationY") ?? 0f)));
            try {
                if (Get(ui, "ChocoboIsAvailable") is true && Get(ui, "ChocoboPosition") is UnityEngine.Vector2 c)
                    b.Append(",\"chx\":").Append(Fl(c.x)).Append(",\"chy\":").Append(Fl(c.y));
                if (Get(ui, "PlaneIsAvailable") is true && Get(ui, "PlanePosition") is UnityEngine.Vector2 a)
                    b.Append(",\"asx\":").Append(Fl(a.x)).Append(",\"asy\":").Append(Fl(a.y));
            } catch { }
            if (mapDir != null && exported.Add(name)) ExportMap(name);
        } catch (Exception e) { if (!failedOnce) { failedOnce = true; Log.Warn("companion world map: " + e.Message); } }
    }

    static string Fl(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    static void ExportMap(string name) {
        string outPath = System.IO.Path.Combine(mapDir!, name + ".png");
        if (System.IO.File.Exists(outPath)) return;
        var am = T("AssetManager")!;
        var load = Array.Find(am.GetMethods(F), m => m.Name == "Load" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2)!
            .MakeGenericMethod(typeof(UnityEngine.Sprite));
        var sprite = load.Invoke(null, new object[] { "EmbeddedAsset/UI/Sprites/" + name, false }) as UnityEngine.Sprite;
        var ns = Gfx.Sprites.Of(sprite);
        var tex = ns?.Texture == null ? null : Gfx.Textures.OfOrNull(ns.Texture);
        if (ns == null || tex?.Pixels == null) { Log.Warn($"world map {name}: no pixels"); return; }
        int x0 = (int)ns.TextureRect.x, y0 = (int)ns.TextureRect.y, w = (int)ns.TextureRect.width, h = (int)ns.TextureRect.height;
        if (w <= 0 || h <= 0) { x0 = 0; y0 = 0; w = tex.Width; h = tex.Height; }
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++) Buffer.BlockCopy(tex.Pixels, ((y0 + y) * tex.Width + x0) * 4, px, y * w * 4, w * 4);
        System.IO.Directory.CreateDirectory(mapDir!);
        Gfx.Png.Write(outPath, px, w, h, flipY: true);
        Log.Info($"world map {name} {w}x{h} -> {outPath}");
    }

    /// A sprite of one of the game's UI atlases (Face, Icon) is wanted on the panels: queue its export.
    static string WantSprite(string atlas, string sprite) {
        if (mapDir == null || string.IsNullOrEmpty(sprite)) return "";
        string file = "ui_" + atlas + "_" + sprite;
        if (spriteSeen.Add(file)) spriteQueue.Enqueue((atlas, sprite));
        return file;
    }

    // a few per tick: an atlas is read back from the GPU once, then cut
    static void ExportQueued() {
        for (int n = 0; n < 4 && spriteQueue.Count > 0; n++) {
            var (atlas, sprite) = spriteQueue.Dequeue();
            try { ExportSprite(atlas, sprite); }
            catch (Exception e) { Log.Once("uisprite" + atlas + sprite, $"ui sprite {atlas}/{sprite}: {e.Message}"); }
        }
    }

    static readonly Dictionary<Gfx.NTexture, byte[]> atlasPixels = new();

    static void ExportSprite(string atlasName, string sprite) {
        string outPath = System.IO.Path.Combine(mapDir!, "ui_" + atlasName + "_" + sprite + ".png");
        if (System.IO.File.Exists(outPath)) return;
        // FF9_TEST_FACE_FALLBACK=1: the older Memorias' way, on a newer one
        var atlas = (atlasName == "Face" && faceFallbackTest ? null : Get(T("Assets.Sources.Scripts.UI.Common.FF9UIDataTool"), atlasName + "Atlas"))
            ?? (atlasName == "Face" ? StatusFaceAtlas() : null);
        var sd = CallOn(atlas, "GetSprite", sprite);
        if (sd == null) { Log.Once("uisprite" + atlasName + sprite, $"ui sprite {atlasName}/{sprite}: not in the atlas"); return; }
        var tex = (Get(sd, "texture") ?? Get(atlas, "texture")) as UnityEngine.Texture;
        var nt = Gfx.Textures.OfOrNull(tex);
        if (nt == null) return;
        if (!atlasPixels.TryGetValue(nt, out var pix)) {
            if (nt.Pixels == null && nt.GlTex == 0) nt.Bind();
            if (nt.Pixels != null) pix = nt.Pixels;
            else {
                nt.EnsurePixels();
                pix = nt.Pixels!;
                if (!nt.Readable) nt.Pixels = null;
            }
            atlasPixels[nt] = pix;
        }
        int sx = (int)L(Get(sd, "x")), sy = (int)L(Get(sd, "y")), w = (int)L(Get(sd, "width")), h = (int)L(Get(sd, "height"));
        if (w <= 0 || h <= 0 || sx < 0 || sy < 0 || sx + w > nt.Width || sy + h > nt.Height) return;
        // the sprite's y counts from the atlas's top; the pixels are stored bottom-up
        var px = new byte[w * h * 4];
        int bottom = nt.Height - (sy + h);
        for (int r = 0; r < h; r++) Buffer.BlockCopy(pix, ((bottom + r) * nt.Width + sx) * 4, px, r * w * 4, w * 4);
        System.IO.Directory.CreateDirectory(mapDir!);
        Gfx.Png.Write(outPath, px, w, h, flipY: true);
    }

    /// Memorias before late 2024 have no FF9UIDataTool.FaceAtlas (GitHub issue 32: no portraits on
    /// the panels): the atlas of the status screen's portrait, which is where the newer getter
    /// takes it from too.
    static readonly bool faceFallbackTest = Environment.GetEnvironmentVariable("FF9_TEST_FACE_FALLBACK") == "1";

    static object? StatusFaceAtlas() {
        var ui = Get(typeof(PersistenSingleton<>).MakeGenericType(T("UIManager")!), "Instance");
        var panel = Get(Get(ui, "StatusScene"), "CharacterDetailPanel") as UnityEngine.GameObject;
        if (panel == null || panel.transform.childCount == 0) return null;
        var first = panel.transform.GetChild(0);
        if (first.childCount == 0) return null;
        var sprite = first.GetChild(0).gameObject.GetComponent(T("UISprite")!);
        return Get(sprite, "atlas");
    }

    static void Str(StringBuilder b, string s) {
        b.Append('"');
        foreach (char c in s) {
            if (c == '"' || c == '\\') b.Append('\\').Append(c);
            else if (c < 0x20) b.Append(' ');
            else b.Append(c);
        }
        b.Append('"');
    }

    /// The game's text tags ([FEED=1], {W123H4}) and control characters out of a name.
    static string Clean(string? text) {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        int depth = 0;
        foreach (char c in text) {
            if (c == '[' || c == '{') { depth++; continue; }
            if ((c == ']' || c == '}') && depth > 0) { depth--; continue; }
            if (depth > 0 || c < ' ') continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    static Type? TextTool => T("Assets.Sources.Scripts.UI.Common.FF9TextTool");
    static string ItemName(object item) { try { return Clean(S(Call(TextTool, "ItemName", item))); } catch { return ""; } }
    static bool NoItem(object? item) => item == null || item.ToString() == "NoItem";

    // ---- per party member, refreshed once a second: portrait, gear, and what the gear still teaches
    static readonly Dictionary<object, string> gearJson = new(ReferenceEqualityComparer.Instance);

    static string Gear(object p, bool refresh) {
        if (!refresh && gearJson.TryGetValue(p, out var cached)) return cached;
        var b = new StringBuilder();
        try {
            var info = Get(p, "info");
            var serial = Get(info, "serial_no");
            if (serial != null) {
                string face = S(Call(T("Assets.Sources.Scripts.UI.Common.FF9UIDataTool"), "AvatarSpriteName", serial));
                b.Append(",\"face\":"); Str(b, WantSprite("Face", face));
            }
            var equip = Get(p, "equip");
            var names = new List<string>();
            var items = new List<object>();
            for (int s = 0; s < 5; s++) {
                var it = CallOn(equip, "get_Item", s);
                if (NoItem(it)) continue;
                items.Add(it!);
                names.Add(ItemName(it!));
            }
            b.Append(",\"gear\":"); Str(b, string.Join(" · ", names));
            b.Append(",\"learn\":[");
            var abil = T("FF9.ff9abil");
            var data = Get(T("ff9item"), "_FF9Item_Data") as IDictionary;
            if (abil != null && data != null && Call(abil, "FF9Abil_HasAp", p) is true) {
                var seen = new HashSet<int>();
                bool first = true;
                foreach (var it in items) {
                    if (!data.Contains(it) || Get(data[it], "ability") is not int[] ids) continue;
                    foreach (int id in ids) {
                        if (id == 0 || !seen.Add(id) || Convert.ToInt32(Call(abil, "FF9Abil_GetIndex", p, id)) < 0) continue;
                        bool active = Call(abil, "IsAbilityActive", id) is true;
                        string name = active
                            ? S(Call(TextTool, "ActionAbilityName", Call(abil, "GetActiveAbilityFromAbilityId", id)))
                            : S(Call(TextTool, "SupportAbilityName", Call(abil, "GetSupportAbilityFromAbilityId", id)));
                        if (!first) b.Append(',');
                        first = false;
                        b.Append("{\"n\":"); Str(b, Clean(name));
                        b.Append(",\"ap\":").Append(L(Call(abil, "FF9Abil_GetAp", p, id)));
                        b.Append(",\"max\":").Append(L(Call(abil, "FF9Abil_GetMax", p, id)));
                        b.Append(",\"done\":").Append(Call(abil, "FF9Abil_IsMaster", p, id) is true ? 1 : 0);
                        b.Append(",\"sa\":").Append(active ? 0 : 1).Append('}');
                    }
                }
            }
            b.Append(']');
        } catch (Exception e) { Log.Once("companiongear", "companion gear: " + e.Message); }
        return gearJson[p] = b.ToString();
    }

    static readonly string[] ElementNames = { "Fire", "Ice", "Thunder", "Earth", "Water", "Wind", "Holy", "Shadow" };

    static void Elements(StringBuilder b, string key, object? flags) {
        int v = flags == null ? 0 : Convert.ToInt32(flags);
        b.Append(",\"").Append(key).Append("\":[");
        bool first = true;
        for (int i = 0; i < 8; i++) {
            if ((v & (1 << i)) == 0) continue;
            if (!first) b.Append(',');
            first = false;
            Str(b, ElementNames[i]);
        }
        b.Append(']');
    }

    static void ItemList(StringBuilder b, string key, object? arr) {
        b.Append(",\"").Append(key).Append("\":[");
        bool first = true;
        if (arr is IEnumerable e) foreach (var it in e) {
            if (NoItem(it)) continue;
            if (!first) b.Append(',');
            first = false;
            Str(b, ItemName(it!));
        }
        b.Append(']');
    }

    /// What Scan would tell: elements, what is left to steal, what the enemy drops.
    static void EnemyInfo(StringBuilder b, object u) {
        try {
            b.Append(",\"lv\":").Append(L(Get(u, "Level")));
            Elements(b, "weak", Get(u, "WeakElement"));
            Elements(b, "absorb", Get(u, "AbsorbElement"));
            Elements(b, "immune", Get(u, "GuardElement"));
            Elements(b, "half", Get(u, "HalfElement"));
            var enemy = Get(u, "Enemy");
            ItemList(b, "steal", Get(enemy, "StealableItems"));
            ItemList(b, "drop", Get(enemy, "DroppableItems"));
        } catch (Exception e) { Log.Once("companionenemy", "companion enemy: " + e.Message); }
    }

    // ---- the inventory: rebuilt only when it changes
    static int itemSig = int.MinValue, keySig = int.MinValue;
    static string itemsJson = "[]", keysJson = "[]";

    static string Items(object ff9) {
        if (Get(ff9, "item") is not IList list) return "[]";
        int sig = 17;
        foreach (var it in list) { if (it == null) continue; long n = L(Get(it, "count")); if (n > 0) sig = sig * 31 + (Convert.ToInt32(Get(it, "id")) * 257 + (int)n); }
        if (sig == itemSig) return itemsJson;
        var b = new StringBuilder("[");
        var data = Get(T("ff9item"), "_FF9Item_Data") as IDictionary;
        bool first = true;
        foreach (var it in list) {
            if (it == null) continue;
            long n = L(Get(it, "count"));
            if (n <= 0) continue;
            var id = Get(it, "id")!;
            if (!first) b.Append(',');
            first = false;
            b.Append("{\"n\":"); Str(b, ItemName(id));
            b.Append(",\"c\":").Append(n);
            if (data != null && data.Contains(id)) {
                var d = data[id];
                b.Append(",\"icon\":"); Str(b, WantSprite("Icon", $"item{L(Get(d, "shape")):00}_{L(Get(d, "color")):00}"));
            }
            b.Append('}');
        }
        itemSig = sig;
        return itemsJson = b.Append(']').ToString();
    }

    static string KeyItems(object ff9) {
        if (Get(ff9, "rare_item_obtained") is not IEnumerable got) return "[]";
        var used = Get(ff9, "rare_item_used") as IEnumerable;
        var usedSet = new HashSet<int>();
        if (used != null) foreach (var x in used) usedSet.Add(Convert.ToInt32(x));
        var ids = new List<int>();
        foreach (var x in got) ids.Add(Convert.ToInt32(x));
        ids.Sort();
        int sig = 23;
        foreach (int id in ids) sig = sig * 31 + id * 2 + (usedSet.Contains(id) ? 1 : 0);
        if (sig == keySig) return keysJson;
        var b = new StringBuilder("[");
        for (int i = 0; i < ids.Count; i++) {
            if (i > 0) b.Append(',');
            string name = "";
            try { name = Clean(S(Call(TextTool, "ImportantItemName", ids[i]))); } catch { }
            b.Append("{\"n\":"); Str(b, name);
            b.Append(",\"used\":").Append(usedSet.Contains(ids[i]) ? 1 : 0).Append('}');
        }
        keySig = sig;
        return keysJson = b.Append(']').ToString();
    }

    /// The counters the game keeps but never shows.
    static void Counters(StringBuilder b, object ff9) {
        try {
            b.Append(",\"counters\":{");
            b.Append("\"steals\":").Append(L(Get(ff9, "steal_no")));
            b.Append(",\"frogs\":").Append(L(Get(Get(ff9, "Frogs"), "Number")));
            try {
                var dragon = Enum.Parse(T("Memoria.Data.EnemyCategoryToken")!, "Dragon");
                b.Append(",\"dragons\":").Append(L(CallOn(Get(ff9, "categoryKillCount"), "get_Item", dragon)));
            } catch { }
            var ev = Get(typeof(FF9StateSystem), "EventState");
            try { b.Append(",\"treasure\":").Append(L(CallOn(ev, "GetTreasureHunterPoints"))); } catch { }
            if (Get(ev, "gEventGlobal") is byte[] g && g.Length > 189) {
                int choco = 0;
                for (int i = 187; i <= 189; i++) for (int v = g[i]; v != 0; v >>= 1) choco += v & 1;
                b.Append(",\"chocographs\":").Append(choco);
            }
            var mg = Get(Get(typeof(FF9StateSystem), "MiniGame"), "SavedData");
            if (mg != null) b.Append(",\"cardW\":").Append(L(Get(mg, "sWin"))).Append(",\"cardL\":").Append(L(Get(mg, "sLose"))).Append(",\"cardD\":").Append(L(Get(mg, "sDraw")));
            b.Append('}');
        } catch (Exception e) { Log.Once("companioncounters", "companion counters: " + e.Message); b.Append('}'); }
    }

    static string Build(bool slow) {
        var b = new StringBuilder(1024);
        b.Append('{');
        var ssType = typeof(FF9StateSystem);
        object? ff9 = Get(Get(ssType, "Common"), "FF9");
        var inst = typeof(PersistenSingleton<>).MakeGenericType(ssType).GetProperty("Instance")?.GetValue(null);
        int mode = (int)L(Get(inst, "mode"));
        object? party = Get(ff9, "party");
        // in play only: a field, the world map or a battle, not the title screen and its menus
        // (the party data exists there too) nor the opening movie (field 70)
        string scene = Player.LastSceneLoaded;
        bool inPlay = scene is "FieldMap" or "WorldMap" or "BattleMap";
        bool opening = (mode == 1 || mode == 5) && L(Get(ff9, "fldMapNo")) == 70;
        bool ready = inPlay && !opening && party != null && Get(party, "member") is Array mem && mem.Length > 0 && mem.GetValue(0) != null;
        string m = mode == 2 ? "battle" : mode == 3 ? "world" : mode == 1 || mode == 5 ? "field" : "other";
        b.Append("\"ready\":").Append(ready ? 1 : 0).Append(",\"mode\":"); Str(b, m);
        if (ff9 != null) {
            b.Append(",\"gil\":").Append(L(Get(party, "gil")));
            string loc = S(Get(ff9, "mapNameStr"));
            if (mode == 3) {
                // on the world map: the region's name, as Memoria's window title shows it
                try { loc = S(Call(T("ff9"), "w_worldLocationName")); } catch { loc = ""; }
            }
            b.Append(",\"loc\":"); Str(b, loc);
            b.Append(",\"map\":").Append(L(Get(ff9, "fldMapNo")));
        }
        // a movie playing (MBG): the panels dim
        bool cine = false;
        try {
            var mbg = Get(typeof(MBG), "Instance") ?? Get(typeof(MBG), "instance");
            // "played": set while a movie (FMV or a field's movie background) runs, cleared at Stop
            cine = mbg != null && Get(mbg, "played") is true && mode != 2;
        } catch { }
        // or a field or world map scene that has taken the player's control (an event, a talk):
        // the game's own HUD is up (not a menu) and its player control is off
        if (!cine && ready && mode != 2) {
            try {
                var ui = Get(typeof(PersistenSingleton<>).MakeGenericType(T("UIManager")!), "Instance");
                string? st = Get(ui, "State")?.ToString();
                cine = st is "FieldHUD" or "WorldHUD" && Get(ui, "IsPlayerControlEnable") is false;
            } catch { }
        }
        if (cine != lastCine) { lastCine = cine; Log.Info($"panels: {(cine ? "dim (no control)" : "lit (control)")}"); }
        b.Append(",\"cine\":").Append(cine ? 1 : 0);
        // boosters and play time (the panels' buttons and header)
        try {
            var settings = Get(ssType, "Settings")!;
            if (Get(settings, "IsBoosterButtonActive") is bool[] act && act.Length > 4)
                b.Append($",\"speed\":{(act[1] ? 1 : 0)},\"noenc\":{(act[4] ? 1 : 0)},\"bplus\":{(act[0] ? 1 : 0)},\"dmg\":{(act[3] ? 1 : 0)}");
            double t = Convert.ToDouble(Get(settings, "time")) + (Clock.Now - Convert.ToDouble(Get(settings, "StartGameTime")));
            b.Append(",\"time\":").Append((long)Math.Max(0, t));
        } catch { }
        if (!ready) { b.Append('}'); return b.ToString(); }
        // FF9's classic grey window skin for the panels (GrayAtlas: its stone texture and silver frame)
        b.Append(",\"skinBg\":"); Str(b, WantSprite("Gray", "dialog_bg"));
        b.Append(",\"skinFrame\":"); Str(b, WantSprite("Gray", "dialog_frame_info"));
        if (!listedAtlases) {   // once: the UI atlases' sprite names in the log (for the panels' window skin)
            listedAtlases = true;
            foreach (var an in new[] { "Window", "General", "Gray", "Blue" }) {
                try {
                    var atlas = Get(T("Assets.Sources.Scripts.UI.Common.FF9UIDataTool"), an + "Atlas");
                    var names = new List<string>();
                    if (Get(atlas, "spriteList") is IEnumerable sl) foreach (var sd in sl) names.Add($"{S(Get(sd, "name"))}:{L(Get(sd, "width"))}x{L(Get(sd, "height"))}");
                    Log.Info($"atlas {an}: " + string.Join(" ", names));
                } catch (Exception e) { Log.Info($"atlas {an}: {e.Message}"); }
            }
        }
        if (mode == 3) WorldMapInfo(b);
        // the party (outside battle) from the save data; in battle, from the battle units
        if (mode == 2) {
            object? battle = Get(Get(ssType, "Battle"), "FF9Battle");
            var en = CallOn(battle, "EnumerateBattleUnits") as IEnumerable;
            var pl = new StringBuilder(); var em = new StringBuilder();
            if (en != null) foreach (var u in en) {
                bool isPlayer = Get(u, "IsPlayer") is true;
                if (!isPlayer && (Get(u, "IsDisappear") is true || Get(u, "IsTargetable") is not true)) continue;
                var sb = isPlayer ? pl : em;
                if (sb.Length > 0) sb.Append(',');
                sb.Append("{\"name\":"); Str(sb, Clean(S(Get(u, "Name"))));
                sb.Append(",\"hp\":").Append(L(Get(u, "CurrentHp"))).Append(",\"mhp\":").Append(L(Get(u, "MaximumHp")));
                if (isPlayer) {
                    sb.Append(",\"mp\":").Append(L(Get(u, "CurrentMp"))).Append(",\"mmp\":").Append(L(Get(u, "MaximumMp")));
                    sb.Append(",\"atb\":").Append(L(Get(u, "CurrentAtb"))).Append(",\"matb\":").Append(Math.Max(1, L(Get(u, "MaximumAtb"))));
                    sb.Append(",\"lv\":").Append(L(Get(u, "Level")));
                    sb.Append(",\"trance\":").Append(Get(u, "HasTrance") is true ? L(Get(u, "Trance")) : -1);
                    var p = Get(u, "Player");
                    if (p != null) sb.Append(Gear(p, slow));
                } else {
                    sb.Append(",\"target\":1");
                    EnemyInfo(sb, u);
                }
                sb.Append('}');
            }
            b.Append(",\"party\":[").Append(pl).Append("],\"enemies\":[").Append(em).Append(']');
        } else if (party != null && Get(party, "member") is Array members) {
            b.Append(",\"party\":[");
            bool first = true;
            foreach (var p in members) {
                if (p == null) continue;
                if (!first) b.Append(',');
                first = false;
                object? cur = Get(p, "cur"), max = Get(p, "max");
                b.Append("{\"name\":"); Str(b, Clean(S(Get(p, "Name"))));
                b.Append(",\"lv\":").Append(L(Get(p, "level")));
                b.Append(",\"hp\":").Append(L(Get(cur, "hp"))).Append(",\"mhp\":").Append(L(Get(max, "hp")));
                b.Append(",\"mp\":").Append(L(Get(cur, "mp"))).Append(",\"mmp\":").Append(L(Get(max, "mp")));
                b.Append(",\"trance\":").Append(L(Get(p, "trance")));
                b.Append(Gear(p, slow));
                b.Append('}');
            }
            b.Append(']');
        }
        if (ff9 != null) {
            try { b.Append(",\"items\":").Append(Items(ff9)); } catch { b.Append(",\"items\":[]"); }
            try { b.Append(",\"keys\":").Append(KeyItems(ff9)); } catch { b.Append(",\"keys\":[]"); }
            if (slow || lastCounters == null) {
                var cb = new StringBuilder();
                Counters(cb, ff9);
                lastCounters = cb.ToString();
            }
            b.Append(lastCounters);
        }
        b.Append('}');
        return b.ToString();
    }

    static string? lastCounters;
    static bool listedAtlases;
}

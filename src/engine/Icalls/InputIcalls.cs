// UnityEngine.Input from the gamepad (the same state the game also reads through XInput) and
// touch. Keyboard and mouse are absent on the Thor.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace FF9Engine.Icalls;

public static class Pad {
    [StructLayout(LayoutKind.Sequential)]
    public struct Raw { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }

    [DllImport("__Internal", EntryPoint = "XInputGamePadGetState")]
    static extern uint GetState(uint player, ref Raw s);

    public static Raw Now, Prev;

    [DllImport("__Internal")] static extern void ff9_pad_frame(int frame);

    public static void Poll() {
        ff9_pad_frame(Core.Clock.Frame);
        Prev = Now;
        var s = new Raw();
        if (GetState(0, ref s) == 0) Now = s; else Now = default;
    }

    // XInput button bits
    public const ushort Up = 0x1, Down = 0x2, Left = 0x4, Right = 0x8, Start = 0x10, Back = 0x20, LThumb = 0x40, RThumb = 0x80,
        LB = 0x100, RB = 0x200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    public static bool Held(ushort b) => (Now.Buttons & b) != 0;
    public static bool Pressed(ushort b) => (Now.Buttons & b) != 0 && (Prev.Buttons & b) == 0;
    public static bool Released(ushort b) => (Now.Buttons & b) == 0 && (Prev.Buttons & b) != 0;
    public static float Axis(short v) => Math.Abs(v) < 7849 ? 0 : Math.Clamp(v / 32767f, -1, 1);
}

static class InputIcalls {
    // Unity joystick button numbering for an Xbox pad on Windows
    static readonly ushort[] joyButtons = { Pad.A, Pad.B, Pad.X, Pad.Y, Pad.LB, Pad.RB, Pad.Back, Pad.Start, Pad.LThumb, Pad.RThumb };

    static ushort? KeyButton(string name) {
        name = name.ToLowerInvariant();
        if (name.StartsWith("joystick button ") || name.StartsWith("joystick 1 button ")) {
            int n = int.Parse(name[(name.LastIndexOf(' ') + 1)..]);
            return n < joyButtons.Length ? joyButtons[n] : null;
        }
        return null;
    }

    static float AxisValue(string axis) => axis switch {
        "Horizontal" => Pad.Axis(Pad.Now.LX) + (Pad.Held(Pad.Right) ? 1 : 0) - (Pad.Held(Pad.Left) ? 1 : 0),
        "Vertical" => Pad.Axis(Pad.Now.LY) + (Pad.Held(Pad.Up) ? 1 : 0) - (Pad.Held(Pad.Down) ? 1 : 0),
        _ => 0,
    };

    [Icall("UnityEngine.Input::GetAxis")]
    [Icall("UnityEngine.Input::GetAxisRaw")] static float GetAxis(string a) => Math.Clamp(AxisValue(a), -1, 1);
    // Unity's Input Manager buttons. FF9's are named after the pad button they stand for
    // ("JoystickButton6"); HonoInputManager reads its joystick keys this way (and the key codes
    // too, for some). Select (Back, joystick button 6) came only this way, so Active Time Events
    // never opened (GitHub issue 16).
    static ushort? NamedButton(string name) =>
        name.StartsWith("JoystickButton", StringComparison.Ordinal) && int.TryParse(name.AsSpan(14), out int n) && n >= 0 && n < joyButtons.Length
            ? joyButtons[n] : null;

    static readonly bool traceButtons = Environment.GetEnvironmentVariable("FF9_TRACE_BUTTONS") != null;
    static bool Trace(string kind, string name, bool v) { if (v && traceButtons) Log.Info($"input: {kind}(\"{name}\")"); return v; }

    // A press counts for the first ask in a frame only: while it catches up (a slow frame, a field
    // loading) the game runs several update loops in one frame, and each saw the press again (one
    // press of A during a field's start went through several lines of dialog).
    static readonly Dictionary<(string, bool), long> seen = new();
    static bool Once(string name, bool down, bool v) {
        if (!v) return false;
        long f = Core.Clock.Frame;
        if (seen.TryGetValue((name, down), out long at) && at == f) return false;
        seen[(name, down)] = f;
        return true;
    }

    [Icall("UnityEngine.Input::GetButton")] static bool GetButton(string b) => NamedButton(b) is { } x && Pad.Held(x);
    [Icall("UnityEngine.Input::GetButtonDown")] static bool GetButtonDown(string b) => Trace("GetButtonDown", b, Once(b, true, NamedButton(b) is { } x && Pad.Pressed(x)));
    [Icall("UnityEngine.Input::GetButtonUp")] static bool GetButtonUp(string b) => Once(b, false, NamedButton(b) is { } x && Pad.Released(x));
    [Icall("UnityEngine.Input::GetKeyString")] static bool GetKey(string k) => KeyButton(k) is { } b && Pad.Held(b);
    [Icall("UnityEngine.Input::GetKeyDownString")] static bool GetKeyDown(string k) => KeyButton(k) is { } b && Pad.Pressed(b);
    [Icall("UnityEngine.Input::GetKeyUpString")] static bool GetKeyUp(string k) => KeyButton(k) is { } b && Pad.Released(b);

    static ushort? KeyCodeButton(int key) => key >= (int)KeyCode.JoystickButton0 && key <= (int)KeyCode.JoystickButton9
        ? joyButtons[key - (int)KeyCode.JoystickButton0]
        : key >= (int)KeyCode.Joystick1Button0 && key <= (int)KeyCode.Joystick1Button9 ? joyButtons[key - (int)KeyCode.Joystick1Button0] : null;

    [Icall("UnityEngine.Input::GetKeyInt")] static bool GetKeyInt(int k) => KeyCodeButton(k) is { } b && Pad.Held(b);
    [Icall("UnityEngine.Input::GetKeyDownInt")] static bool GetKeyDownInt(int k) => KeyCodeButton(k) is { } b && Pad.Pressed(b);
    [Icall("UnityEngine.Input::GetKeyUpInt")] static bool GetKeyUpInt(int k) => KeyCodeButton(k) is { } b && Pad.Released(b);
    [Icall("UnityEngine.Input::GetJoystickNames")] static string[] Joysticks() => new[] { "Controller (XBOX 360 For Windows)" };
    [Icall("UnityEngine.Input::GetMouseButton")] static bool Mouse(int b) => b == 0 && Touches.Down;
    [Icall("UnityEngine.Input::GetMouseButtonDown")] static bool MouseDown(int b) => b == 0 && Touches.Down && !Touches.WasDown;
    [Icall("UnityEngine.Input::GetMouseButtonUp")] static bool MouseUp(int b) => b == 0 && !Touches.Down && Touches.WasDown;
    [Icall("UnityEngine.Input::ResetInputAxes")] static void Reset() { }
    [Icall("UnityEngine.Input::INTERNAL_get_mousePosition")] static void MousePos(ref Vector3 v) => v = new Vector3(Touches.X, Touches.Y, 0);
    [Icall("UnityEngine.Input::INTERNAL_get_mouseScrollDelta")] static void Scroll(ref Vector2 v) => v = default;
    [Icall("UnityEngine.Input::get_anyKey")] static bool AnyKey() => Pad.Now.Buttons != 0;
    [Icall("UnityEngine.Input::get_anyKeyDown")] static bool AnyKeyDown() => (Pad.Now.Buttons & ~Pad.Prev.Buttons) != 0;
    [Icall("UnityEngine.Input::get_inputString")] static string InputString() => "";
    [Icall("UnityEngine.Input::get_touchCount")] static int TouchCount() => 0;
    [Icall("UnityEngine.Input::GetTouch")] static Touch GetTouch(int i) => default;
    [Icall("UnityEngine.Input::set_imeCompositionMode")] static void Ime(IMECompositionMode m) { }
    [Icall("UnityEngine.Input::get_compositionString")] static string Composition() => "";
    [Icall("UnityEngine.Input::INTERNAL_set_compositionCursorPos")] static void CompPos(ref Vector2 v) { }
    [Icall("UnityEngine.Input::get_mousePresent")] static bool MousePresent() => false;
    [Icall("UnityEngine.Input::get_touchSupported")] static bool TouchSupported() => true;
    [Icall("UnityEngine.Input::get_multiTouchEnabled")] static bool MultiTouch() => false;
    [Icall("UnityEngine.Input::set_multiTouchEnabled")] static void SetMultiTouch(bool v) { }
}

public static class Touches {
    public static bool Down, WasDown;
    public static float X, Y;
}

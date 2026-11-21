namespace QuicNet.Video;

// DOM physical key names mapped to the PC AT scan-code set. The high byte
// distinguishes keys that share a scan code with the numeric keypad.
internal static class RemoteKeyboard
{
    private static readonly Dictionary<string, ushort> Codes = Build();

    internal static bool TryScan(string? code, out ushort scan)
    {
        scan = 0;
        return code != null && Codes.TryGetValue(code, out scan);
    }

    private static Dictionary<string, ushort> Build()
    {
        var codes = new Dictionary<string, ushort>(StringComparer.Ordinal)
        {
            ["Escape"] = 1, ["Minus"] = 12, ["Equal"] = 13, ["Backspace"] = 14, ["Tab"] = 15,
            ["BracketLeft"] = 26, ["BracketRight"] = 27, ["Enter"] = 28, ["ControlLeft"] = 29,
            ["Semicolon"] = 39, ["Quote"] = 40, ["Backquote"] = 41, ["ShiftLeft"] = 42,
            ["Backslash"] = 43, ["Comma"] = 51, ["Period"] = 52, ["Slash"] = 53, ["ShiftRight"] = 54,
            ["NumpadMultiply"] = 55, ["AltLeft"] = 56, ["Space"] = 57, ["CapsLock"] = 58,
            ["NumLock"] = 69, ["ScrollLock"] = 70, ["NumpadSubtract"] = 74, ["NumpadAdd"] = 78,
            ["NumpadDecimal"] = 83, ["IntlBackslash"] = 86, ["F11"] = 87, ["F12"] = 88,
            ["NumpadEnter"] = 0xe01c, ["ControlRight"] = 0xe01d, ["NumpadDivide"] = 0xe035,
            ["PrintScreen"] = 0xe037, ["AltRight"] = 0xe038, ["Home"] = 0xe047, ["ArrowUp"] = 0xe048,
            ["PageUp"] = 0xe049, ["ArrowLeft"] = 0xe04b, ["ArrowRight"] = 0xe04d, ["End"] = 0xe04f,
            ["ArrowDown"] = 0xe050, ["PageDown"] = 0xe051, ["Insert"] = 0xe052, ["Delete"] = 0xe053,
            ["MetaLeft"] = 0xe05b, ["MetaRight"] = 0xe05c, ["ContextMenu"] = 0xe05d
        };
        for (int i = 1; i <= 10; i++)
        {
            codes["Digit" + i % 10] = (ushort)(i + 1);
            codes["F" + i] = (ushort)(58 + i);
        }
        for (int i = 13; i <= 23; i++) codes["F" + i] = (ushort)(0x64 + i - 13);
        codes["F24"] = 0x76;
        foreach (var (row, start) in new[] { ("QWERTYUIOP", 16), ("ASDFGHJKL", 30), ("ZXCVBNM", 44) })
            for (int i = 0; i < row.Length; i++) codes["Key" + row[i]] = (ushort)(start + i);
        ushort[] numpad = [82, 79, 80, 81, 75, 76, 77, 71, 72, 73];
        for (int i = 0; i < numpad.Length; i++) codes["Numpad" + i] = numpad[i];
        return codes;
    }
}

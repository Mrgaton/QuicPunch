using System;
using System.Runtime.InteropServices;

public static class AnsiHelper
{
    private const int STD_OUTPUT_HANDLE = -11;

    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 4;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
    public static void InitConsle()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                IntPtr handle = GetStdHandle(STD_OUTPUT_HANDLE);
                if (handle != IntPtr.Zero && handle != (IntPtr)(-1))
                {
                    if (GetConsoleMode(handle, out uint mode))
                    {
                        SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
                    }
                }
            }
            catch { }
        }
    }

    public static void InitConsole() => InitConsle();

    //private static string Pastel(string text, int r, int g, int b) => "\u001b[38;2;" + r + ";" + g + ";" + b + "m" + text;

    public static string SetPosition(int row, int collum) => "\u001b[" + row + ";" + collum + "H";

    public static string ResetColor = "\u001b[0m";
    public static string HideScrollbar => "\u001b[?30l";
    public static string ShowScrollbar => "\u001b[?30h";

    public static string HideCursor => "\u001b[?25l";
    public static string ShowCursor => "\u001b[?25h";

    public static string ClearScreen => "\u001b[2J";
    public static string ClearCursorToEnd => "\u001b[0J";
    public static string ClearCursorToBeginning => "\u001b[1J";
    public static string ClearLineToEnd => "\u001b[0K";
    public static string ClearStartToLine => "\u001b[1K";
    public static string ClearLine => "\u001b[2K";

    public static string MoveCursorUp(int n) => $"\u001b[{n}A";

    public static string MoveCursorDown(int n) => $"\u001b[{n}B";

    public static string MoveCursorForward(int n) => $"\u001b[{n}C";

    public static string MoveCursorBackward(int n) => $"\u001b[{n}D";

    public static string MoveCursorToPosition(int row, int col) => $"\u001b[{row};{col}H";

    public static string SaveCursorPosition => "\u001b[s";
    public static string RestoreCursorPosition => "\u001b[u";

    public static string ScrollUp(int n) => $"\u001b[{n}S";

    public static string ScrollDown(int n) => $"\u001b[{n}T";

    public static string SetTitle(string title) => "\u001b]0;" + title + "\a";

    public static string SetSize(int x, int y) => "\u001b[8;" + y + ";" + x + "t";

    //private static string Pastel(string text, int r, int g, int b) => "\u001b[38;2;" + r + ";" + g + ";" + b + "m" + text;

    public static string FRGB(char? c, int r, int g, int b) => "\u001b[38;2;" + r + ";" + g + ";" + b + "m" + c;

    public static string BRGB(char? c, int r, int g, int b) => "\u001b[48;2;" + r + ";" + g + ";" + b + "m" + c;

    public static class AnsiColors
    {
        public static readonly string[] All = new[]
        {
            Red, Green, Yellow, Blue, Magenta, Cyan,
            BrightBlack, BrightRed, BrightGreen, BrightYellow,
            BrightBlue, BrightMagenta, BrightCyan, BrightWhite
        };

        public const string Black = "\x1b[30m";
        public const string Red = "\x1b[31m";
        public const string Green = "\x1b[32m";
        public const string Yellow = "\x1b[33m";
        public const string Blue = "\x1b[34m";
        public const string Magenta = "\x1b[35m";
        public const string Cyan = "\x1b[36m";
        public const string White = "\x1b[37m";
        public const string BrightBlack = "\x1b[90m";
        public const string BrightRed = "\x1b[91m";
        public const string BrightGreen = "\x1b[92m";
        public const string BrightYellow = "\x1b[93m";
        public const string BrightBlue = "\x1b[94m";
        public const string BrightMagenta = "\x1b[95m";
        public const string BrightCyan = "\x1b[96m";
        public const string BrightWhite = "\x1b[97m";
        public const string Reset = "\x1b[0m";

        public const string Orange = "\x1b[38;5;208m";
        public const string LightGray = "\x1b[38;5;250m";
        public const string DarkGray = "\x1b[38;5;240m";
        public const string LightRed = "\x1b[38;5;217m";
        public const string LightGreen = "\x1b[38;5;121m";
        public const string LightBlue = "\x1b[38;5;159m";
        public const string LightYellow = "\x1b[93m";
        public const string DarkYellow = "\x1b[33m";
        public const string LightPurple = "\x1b[95m";
        public const string DarkPurple = "\x1b[35m";
        public const string LightCyan = "\x1b[96m";
        public const string DarkCyan = "\x1b[36m";
        public const string Pink = "\x1b[38;5;200m";
        public const string Brown = "\x1b[38;5;130m";
        public const string Gold = "\x1b[38;5;172m";
        public const string Silver = "\x1b[38;5;145m";
        public const string Beige = "\x1b[38;5;230m";
        public const string Olive = "\x1b[38;5;64m";
    }
}
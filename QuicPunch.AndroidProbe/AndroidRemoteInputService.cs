using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Views.Accessibility;
using QuicNet.Video;
using Path = Android.Graphics.Path;

namespace QuicPunch.AndroidProbe;

[Service(Label = "Control remoto de QuicNet", Exported = true, Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE")]
[IntentFilter(["android.accessibilityservice.AccessibilityService"])]
[MetaData("android.accessibilityservice", Resource = "@xml/remote_input_service")]
public sealed class AndroidRemoteInputService : AccessibilityService
{
    private static AndroidRemoteInputService? _connected;
    internal static AndroidRemoteInputService? Connected => Volatile.Read(ref _connected);
    protected override void OnServiceConnected() => Volatile.Write(ref _connected, this);
    public override void OnAccessibilityEvent(AccessibilityEvent? e) { }
    public override void OnInterrupt() { }
    public override bool OnUnbind(Intent? intent)
    {
        Interlocked.CompareExchange(ref _connected, null, this);
        return base.OnUnbind(intent);
    }
    public override void OnDestroy()
    {
        Interlocked.CompareExchange(ref _connected, null, this);
        base.OnDestroy();
    }

    internal sealed class Device(AndroidRemoteInputService service) : IRemoteInputDevice
    {
        private readonly Handler _main = new(Looper.MainLooper!);
        private readonly List<PointF> _stroke = [];
        private readonly HashSet<Keycode> _keys = [];
        private volatile bool _disposed;
        private long _started;
        public bool KeyboardAvailable => OperatingSystem.IsAndroidVersionAtLeast(33);

        public void Apply(RemoteInputArea area, RemoteDesktopInput input) => _main.Post(() =>
        {
            if (_disposed || !ReferenceEquals(Connected, service) || AndroidScreenCaptureService.RemoteArea != area) return;
            if (input.kind == "release") { ReleaseKeys(); return; }
            if (input.kind is "text" or "key" or "keydown" or "keyup") { Type(input); return; }
            float x = area.X + (float)(input.x * (area.Width - 1));
            float y = area.Y + (float)(input.y * (area.Height - 1));
            switch (input.kind)
            {
                case "down":
                    ClearStroke();
                    _started = SystemClock.UptimeMillis();
                    _stroke.Add(new(x, y));
                    break;
                case "move" when _stroke.Count is > 0 and < 40:
                    _stroke.Add(new(x, y));
                    break;
                case "up" when _stroke.Count > 0:
                    using (var path = new Path())
                    {
                        path.MoveTo(_stroke[0].X, _stroke[0].Y);
                        foreach (var point in _stroke.Skip(1)) path.LineTo(point.X, point.Y);
                        path.LineTo(x, y);
                        Gesture(path, input.button == 2 ? 550 : Math.Clamp(SystemClock.UptimeMillis() - _started, 50, 600));
                    }
                    ClearStroke();
                    break;
                case "scroll":
                    using (var path = new Path())
                    {
                        path.MoveTo(x, y);
                        path.LineTo(x, Math.Clamp(y - Math.Sign(input.delta) * area.Height * .28f, area.Y, area.Y + area.Height - 1));
                        Gesture(path, 180);
                    }
                    break;
            }
        });

        private void Gesture(Path path, long duration)
        {
            using var stroke = new GestureDescription.StrokeDescription(path, 0, duration);
            using var builder = new GestureDescription.Builder();
            builder.AddStroke(stroke);
            using var gesture = builder.Build();
            if (gesture != null) service.DispatchGesture(gesture, null, null);
        }

        private void Type(RemoteDesktopInput input)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(33) || service.InputMethod?.CurrentInputConnection is not { } connection) return;
            if (input.kind == "text") { connection.CommitText(input.text!, 1, null); return; }
            var key = MapKey(input.key);
            if (key == Keycode.Unknown) return;
            if (input.kind == "key") { SendKey(key, true); SendKey(key, false); }
            else SendKey(key, input.kind == "keydown");
        }

        private void SendKey(Keycode key, bool down)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(33) || service.InputMethod?.CurrentInputConnection is not { } connection) return;
            int repeat = down && !_keys.Add(key) ? 1 : 0;
            if (!down) _keys.Remove(key);
            MetaKeyStates meta = 0;
            if (_keys.Contains(Keycode.CtrlLeft) || _keys.Contains(Keycode.CtrlRight)) meta |= MetaKeyStates.CtrlOn;
            if (_keys.Contains(Keycode.ShiftLeft) || _keys.Contains(Keycode.ShiftRight)) meta |= MetaKeyStates.ShiftOn;
            if (_keys.Contains(Keycode.AltLeft) || _keys.Contains(Keycode.AltRight)) meta |= MetaKeyStates.AltOn;
            if (_keys.Contains(Keycode.MetaLeft) || _keys.Contains(Keycode.MetaRight)) meta |= MetaKeyStates.MetaOn;
            long now = SystemClock.UptimeMillis();
            using var keyEvent = new KeyEvent(now, now, down ? KeyEventActions.Down : KeyEventActions.Up, key, repeat, meta);
            connection.SendKeyEvent(keyEvent);
        }

        private static Keycode MapKey(string? code)
        {
            if (code is { Length: 4 } && code.StartsWith("Key", StringComparison.Ordinal) && code[3] is >= 'A' and <= 'Z')
                return Keycode.A + (code[3] - 'A');
            if (code is { Length: 6 } && code.StartsWith("Digit", StringComparison.Ordinal) && code[5] is >= '0' and <= '9')
                return Keycode.Num0 + (code[5] - '0');
            if (code is { Length: 7 } && code.StartsWith("Numpad", StringComparison.Ordinal) && code[6] is >= '0' and <= '9')
                return Keycode.Numpad0 + (code[6] - '0');
            if (code is { Length: >= 2 and <= 3 } && code[0] == 'F' && int.TryParse(code.AsSpan(1), out int f) && f is >= 1 and <= 12)
                return Keycode.F1 + (f - 1);
            return code switch
            {
                "Backspace" => Keycode.Del, "Enter" => Keycode.Enter, "NumpadEnter" => Keycode.NumpadEnter,
                "Delete" => Keycode.ForwardDel, "Escape" => Keycode.Escape, "Tab" => Keycode.Tab, "Space" => Keycode.Space,
                "ArrowLeft" => Keycode.DpadLeft, "ArrowRight" => Keycode.DpadRight, "ArrowUp" => Keycode.DpadUp, "ArrowDown" => Keycode.DpadDown,
                "Home" => Keycode.MoveHome, "End" => Keycode.MoveEnd, "Insert" => Keycode.Insert,
                "PageUp" => Keycode.PageUp, "PageDown" => Keycode.PageDown,
                "ControlLeft" => Keycode.CtrlLeft, "ControlRight" => Keycode.CtrlRight, "ShiftLeft" => Keycode.ShiftLeft,
                "ShiftRight" => Keycode.ShiftRight, "AltLeft" => Keycode.AltLeft, "AltRight" => Keycode.AltRight,
                "MetaLeft" => Keycode.MetaLeft, "MetaRight" => Keycode.MetaRight, "CapsLock" => Keycode.CapsLock,
                "Comma" => Keycode.Comma, "Period" => Keycode.Period, "Minus" => Keycode.Minus, "Equal" => Keycode.Equals,
                "Semicolon" => Keycode.Semicolon, "Quote" => Keycode.Apostrophe, "Backquote" => Keycode.Grave,
                "BracketLeft" => Keycode.LeftBracket, "BracketRight" => Keycode.RightBracket,
                "Slash" => Keycode.Slash, "Backslash" => Keycode.Backslash,
                _ => Keycode.Unknown
            };
        }

        private void ClearStroke() { foreach (var point in _stroke) point.Dispose(); _stroke.Clear(); }
        private void ReleaseKeys()
        {
            ClearStroke();
            foreach (var key in _keys.ToArray()) SendKey(key, false);
            _keys.Clear();
        }
        public void Release() => _main.Post(ReleaseKeys);
        public void Dispose()
        {
            _disposed = true;
            Release();
        }
    }
}

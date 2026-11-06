using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

namespace QuicPunch.AndroidProbe;

/// <summary>Keeps the entire web viewport clear of Android's system UI.</summary>
internal sealed class SystemInsetLayout(Context context) : FrameLayout(context)
{
    public override WindowInsets? OnApplyWindowInsets(WindowInsets? insets)
    {
        // Older Android versions fit the activity content to system bars themselves.
        if (insets == null || !OperatingSystem.IsAndroidVersionAtLeast(30))
            return base.OnApplyWindowInsets(insets);

        int types = WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout() | WindowInsets.Type.Ime();
        var safe = insets.GetInsets(types);
        SetPadding(safe.Left, safe.Top, safe.Right, safe.Bottom);

        // The parent has already resized the WebView. Forward zero insets so newer
        // WebViews don't add CSS safe-area padding or shrink again for the keyboard.
        // Keep dispatching updates when the keyboard closes or the device rotates.
        using var builder = new WindowInsets.Builder(insets);
        return builder.SetInsets(types, Insets.None)!.Build();
    }
}

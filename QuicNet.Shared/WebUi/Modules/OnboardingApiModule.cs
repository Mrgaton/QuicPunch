using System.Net;
using System.Text.Json;
using QuicNet.Notifications;
using QuicNet.Settings;

namespace QuicNet.WebUi.Modules;

internal sealed record OnboardingState(bool required, bool saved, string? mode, string displayName,
    string deviceName, string torName, bool startupSupported, bool startWithSystem, bool runInBackground);

internal sealed record OnboardingChoices(string? mode, string? displayName, bool? startWithSystem, bool? runInBackground);

internal sealed class OnboardingApiModule(
    AppPreferencesStore preferences,
    Func<IAppPlatform?> getPlatform,
    Func<AppPreferences, Task> startNode,
    string torName)
{
    private readonly SemaphoreSlim _completion = new(1, 1);
    private volatile bool _required = !preferences.Snapshot().OnboardingCompleted;

    internal bool Required => _required;

    internal async Task HandleAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.HttpMethod == "POST")
        {
            using var body = await WebUiContext.ReadJsonAsync(request, "/api/onboarding").ConfigureAwait(false);
            var choices = WebUiContext.ReadJsonValue<OnboardingChoices>(body.RootElement);
            await _completion.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_required)
                {
                    var current = preferences.Snapshot();
                    if (!current.OnboardingCompleted)
                        SaveChoices(current, choices);

                    // Only start the node once the mode and profile are durable.
                    await startNode(preferences.Snapshot()).ConfigureAwait(false);
                    _required = false;
                }
            }
            finally { _completion.Release(); }
        }
        else if (request.HttpMethod != "GET")
        {
            response.StatusCode = 405;
            response.Headers["Allow"] = "GET, POST";
            return;
        }

        var saved = preferences.Snapshot();
        await WebUiContext.WriteJsonAsync(response, new OnboardingState(_required, saved.OnboardingCompleted,
            saved.OnboardingCompleted ? (saved.TorEnabled ? "tor" : "wan") : null,
            saved.DisplayName, LocalUserProfile.DeviceName, torName, getPlatform()?.StartupSupported == true,
            saved.StartWithSystem, saved.RunInBackground)).ConfigureAwait(false);
    }

    private void SaveChoices(AppPreferences current, OnboardingChoices? choices)
    {
        if (choices?.mode is not ("wan" or "tor"))
            throw new InvalidDataException("Elige WAN o Solo Tor.");
        if (!LocalUserProfile.IsValid(choices.displayName))
            throw new InvalidDataException("Usa un nombre de hasta 64 caracteres, sin caracteres de control.");
        if (choices.startWithSystem is not bool startup || choices.runInBackground is not bool background)
            throw new InvalidDataException("Indica tus preferencias de inicio y segundo plano.");

        var platform = getPlatform();
        bool changeStartup = platform?.StartupSupported == true && startup != current.StartWithSystem;
        if (changeStartup) platform!.SetStartWithSystem(startup);
        try
        {
            preferences.Update(p =>
            {
                p.StartWithSystem = changeStartup ? startup : current.StartWithSystem;
                p.RunInBackground = background;
                p.DisplayName = LocalUserProfile.Normalize(choices.displayName);
                p.WanEnabled = choices.mode == "wan";
                p.TorEnabled = choices.mode == "tor";
                p.OnboardingCompleted = true;
            });
        }
        catch
        {
            if (changeStartup) platform!.SetStartWithSystem(current.StartWithSystem);
            throw;
        }
    }
}

namespace TanukiBCL.VoiceProbe;

// TanukiBCL v3.2.7 common/pushToTalkOptions.ts wire values.
internal enum MicrophoneActivationMode
{
    Voice = 0,
    PushToTalk = 1,
    PushToMute = 2
}

internal static class MicrophoneActivationPolicy
{
    public static bool AllowsAudio(MicrophoneActivationMode mode, bool shortcutPressed, bool manuallyMuted,
        bool deafened = false) =>
        !manuallyMuted && !deafened && (mode switch
        {
            MicrophoneActivationMode.Voice => true,
            MicrophoneActivationMode.PushToTalk => shortcutPressed,
            MicrophoneActivationMode.PushToMute => !shortcutPressed,
            _ => false
        });
}

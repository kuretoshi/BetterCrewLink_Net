using TanukiBCL.VoiceProbe;

namespace TanukiBCL.Client;

internal static class AudioDeviceSelection
{
    internal static AudioDeviceInfo? Choose(IEnumerable<AudioDeviceInfo> devices, string? name, int? id)
    {
        var available = devices.ToArray();
        return available.FirstOrDefault(device => device.Name == name && device.Id == id) ??
            available.FirstOrDefault(device => device.Name == name) ?? available.FirstOrDefault();
    }

    internal static void Verify()
    {
        AudioDeviceInfo[] devices = [new(0, "Identical microphone"), new(1, "Identical microphone"),
            new(2, "Other microphone")];
        if (Choose(devices, "Identical microphone", 1)?.Id != 1 ||
            Choose(devices, "Identical microphone", 3)?.Id != 0 ||
            Choose(devices, "Other microphone", 1)?.Id != 2)
            throw new InvalidOperationException("Selected microphone ID or name fallback was lost.");
    }
}

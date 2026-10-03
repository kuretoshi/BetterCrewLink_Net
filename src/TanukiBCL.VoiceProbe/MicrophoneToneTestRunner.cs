using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TanukiBCL.VoiceProbe;

// Measures an active Windows capture endpoint without storing microphone audio.
// This isolates a device/input failure from an application's voice transport.
internal static class MicrophoneToneTestRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var microphoneName = ReadValue(args, "--microphone-name");
        var secondsText = ReadValue(args, "--seconds");
        var seconds = secondsText is null ? 6 : int.Parse(secondsText);
        if (seconds is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(args), "--seconds must be between 1 and 120.");

        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToArray();
        MMDevice device;
        if (microphoneName is null)
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        }
        else
        {
            var matches = devices.Where(candidate => candidate.FriendlyName.Contains(
                microphoneName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw new ArgumentException($"Microphone match count={matches.Length}; available: " +
                    string.Join(" | ", devices.Select(candidate => candidate.FriendlyName)));
            device = matches[0];
        }

        using (device)
        using (var capture = new WasapiCapture(device))
        {
            var analyzer = new SpeakerLoopbackTestRunner.ToneAnalyzer(capture.WaveFormat);
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.DataAvailable += (_, eventArgs) => analyzer.Push(eventArgs.Buffer.AsSpan(0, eventArgs.BytesRecorded));
            capture.RecordingStopped += (_, eventArgs) =>
            {
                if (eventArgs.Exception is { } error) stopped.TrySetException(error);
                else stopped.TrySetResult();
            };
            Console.WriteLine($"Microphone capture: {device.FriendlyName}, {capture.WaveFormat}, {seconds}s; no audio is saved.");
            capture.StartRecording();
            try { await Task.Delay(TimeSpan.FromSeconds(seconds)); }
            finally
            {
                capture.StopRecording();
                await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }

            var result = analyzer.Result;
            Console.WriteLine($"Microphone 440 Hz peak={result.ToneAmplitude:0.0000}, " +
                $"adjacent={result.AdjacentAmplitude:0.0000}, frames={result.AnalysisFrames}");
            var passed = result.AnalysisFrames > 0 && result.ToneAmplitude >= 0.003 &&
                         result.ToneAmplitude >= result.AdjacentAmplitude * 3;
            Console.WriteLine(passed ? "[PASS] 440 Hz reached the selected microphone endpoint." :
                "[FAIL] No distinct 440 Hz tone reached the selected microphone endpoint.");
            return passed ? 0 : 1;
        }
    }

    private static string? ReadValue(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {option}.");
        return args[index + 1];
    }
}

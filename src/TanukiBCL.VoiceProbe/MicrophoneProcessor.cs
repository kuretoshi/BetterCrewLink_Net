using SpeexDSPSharp.Core;
using System.Runtime.InteropServices;

namespace TanukiBCL.VoiceProbe;

// SpeexDSP operates on the same 20 ms, 48 kHz mono PCM16 frames that we send
// to WebRTC. It is not Chromium's APM, so subjective 3.2.7 comparisons remain
// necessary. Synthetic tests check outcomes, not acoustic or Chromium parity.
public sealed class MicrophoneProcessor : IDisposable
{
    public const int SamplesPerFrame = 960;
    public const int BytesPerFrame = SamplesPerFrame * sizeof(short);
    private const int PlaybackDelayFrames = 2;
    private const int MaxQueuedPlaybackFrames = 10;
    private readonly object gate = new();
    private readonly SpeexDSPEchoCanceler? echoCanceler;
    private readonly SpeexDSPPreprocessor? preprocessor;
    private readonly short[] playbackFrame = new short[SamplesPerFrame];
    private readonly short[] playbackReferences;
    private readonly byte[] silentPlaybackFrame;
    private readonly byte[] echoOutput = new byte[BytesPerFrame];
    private int playbackFrameLength;
    private int playbackReferenceHead;
    private int playbackReferenceCount;
    private bool disposed;

    internal int PendingPlaybackReferenceFrames
    {
        get { lock (gate) return playbackReferenceCount; }
    }

    public MicrophoneProcessor(bool echoCancellation, bool noiseSuppression, bool autoGainControl)
    {
        playbackReferences = echoCancellation ? new short[SamplesPerFrame * MaxQueuedPlaybackFrames] : [];
        silentPlaybackFrame = echoCancellation ? new byte[BytesPerFrame] : [];
        try
        {
            if (echoCancellation)
            {
                echoCanceler = new SpeexDSPEchoCanceler(SamplesPerFrame, 48_000 / 5);
                var sampleRate = 48_000;
                echoCanceler.Ctl(EchoCancellationCtl.SPEEX_ECHO_SET_SAMPLING_RATE, ref sampleRate);
            }

            if (noiseSuppression || autoGainControl)
            {
                preprocessor = new SpeexDSPPreprocessor(SamplesPerFrame, 48_000);
                var denoise = noiseSuppression ? 1 : 0;
                var agc = autoGainControl ? 1 : 0;
                preprocessor.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_DENOISE, ref denoise);
                preprocessor.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_AGC, ref agc);
                if (autoGainControl)
                {
                    var target = 30_000;
                    preprocessor.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_AGC_TARGET, ref target);
                }
            }
        }
        catch
        {
            preprocessor?.Dispose();
            echoCanceler?.Dispose();
            throw;
        }
    }

    // Called by the render device, after mixing and master/deafen volume.
    // WinMM capture and render callbacks are not synchronized. Keep Speex's
    // two-frame reference delay, but bound the queue ourselves so one callback
    // cannot make Speex's internal playback buffer underflow or overflow.
    public void SubmitPlayback(float[] stereo, int offset, int count)
    {
        if (echoCanceler is null) return;
        lock (gate)
        {
            if (disposed) return;
            for (var index = offset; index + 1 < offset + count; index += 2)
            {
                var mono = (stereo[index] + stereo[index + 1]) * 0.5f;
                playbackFrame[playbackFrameLength++] = (short)Math.Clamp(
                    (int)Math.Round(mono * short.MaxValue), short.MinValue, short.MaxValue);
                if (playbackFrameLength != SamplesPerFrame) continue;
                QueuePlaybackFrame();
                playbackFrameLength = 0;
            }
        }
    }

    private void QueuePlaybackFrame()
    {
        if (playbackReferenceCount == MaxQueuedPlaybackFrames)
        {
            playbackReferenceHead = (playbackReferenceHead + 1) % MaxQueuedPlaybackFrames;
            playbackReferenceCount--;
        }
        var tail = (playbackReferenceHead + playbackReferenceCount) % MaxQueuedPlaybackFrames;
        playbackFrame.CopyTo(playbackReferences.AsSpan(tail * SamplesPerFrame, SamplesPerFrame));
        playbackReferenceCount++;
    }

    private Span<byte> NextPlaybackReference()
    {
        if (playbackReferenceCount <= PlaybackDelayFrames) return silentPlaybackFrame;
        var reference = MemoryMarshal.AsBytes(playbackReferences.AsSpan(
            playbackReferenceHead * SamplesPerFrame, SamplesPerFrame));
        playbackReferenceHead = (playbackReferenceHead + 1) % MaxQueuedPlaybackFrames;
        playbackReferenceCount--;
        return reference;
    }

    public void ProcessCapture(byte[] pcm16)
    {
        if (echoCanceler is null && preprocessor is null) return;
        lock (gate)
        {
            if (disposed) return;
            for (var offset = 0; offset + BytesPerFrame <= pcm16.Length; offset += BytesPerFrame)
            {
                var frame = pcm16.AsSpan(offset, BytesPerFrame);
                if (echoCanceler is not null)
                {
                    echoCanceler.EchoCancel(frame, NextPlaybackReference(), echoOutput);
                    echoOutput.CopyTo(frame);
                }
                preprocessor?.Run(frame);
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            preprocessor?.Dispose();
            echoCanceler?.Dispose();
        }
    }
}

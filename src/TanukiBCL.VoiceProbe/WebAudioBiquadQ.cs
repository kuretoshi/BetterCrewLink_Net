namespace TanukiBCL.VoiceProbe;

// Web Audio interprets low-pass and high-pass BiquadFilterNode.Q as dB;
// NAudio's BiQuadFilter expects the equivalent linear Q factor.
internal static class WebAudioBiquadQ
{
    public static float ToLinear(float decibels) => (float)Math.Pow(10d, decibels / 20d);
}

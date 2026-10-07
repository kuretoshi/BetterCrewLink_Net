namespace TanukiBCL.VoiceProbe.GameMemory;

internal sealed class HeldNosRadio
{
    private readonly List<int> held = [];

    public int? Kind => held.Count == 0 ? null : held[^1];

    public void Set(int kind, bool pressed)
    {
        if (pressed && !held.Contains(kind)) held.Add(kind);
        if (!pressed) held.Remove(kind);
    }

    public void Clear() => held.Clear();
}

using System.Text.Json;
using NAudio.Wave;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class BitCharacterSelfTest
{
    public static int Run(string? fixture)
    {
        void Check(bool ok,string name) { if (!ok) throw new Exception(name); }
        var samples=Enumerable.Range(0,48000).Select(i=>(float)(.18*Math.Sin(i*2*Math.PI*220/48000))).ToArray();
        double previousError=double.PositiveInfinity;
        for(int stage=0;stage<6;stage++)
        {
            var effect=new BitVoiceSampleProvider(new Input(samples));effect.SetStage(stage);
            var output=new float[samples.Length];Check(effect.Read(output,0,output.Length)==output.Length,"sample count");
            Check(output.All(float.IsFinite) && output.Max()>.03,"finite intelligible voice");
            double error=output.Select((v,i)=>Math.Pow(v-samples[i],2)).Average();
            Check(error<previousError,"progressive quality");previousError=error;
            var split=new BitVoiceSampleProvider(new Input(samples));split.SetStage(stage);
            var chunks=new float[samples.Length];
            for(int start=0;start<samples.Length;start+=127) split.Read(chunks,start,Math.Min(127,samples.Length-start));
            Check(output.SequenceEqual(chunks),"block boundary continuity");
        }
        var normal=new BitVoiceSampleProvider(new Input(samples));var dry=new float[samples.Length];normal.Read(dry,0,dry.Length);
        Check(samples.SequenceEqual(dry),"normal voice exact passthrough");
        var silent=new BitVoiceSampleProvider(new Input(new float[1000]));silent.SetStage(0);var silence=new float[1000];silent.Read(silence,0,silence.Length);
        Check(silence.All(x=>x==0),"silence has no DC artifact");
        if(fixture is not null)
        {
            using var json=JsonDocument.Parse(File.ReadAllText(fixture));
            var input=json.RootElement.GetProperty("input").EnumerateArray().Select(p=>p.GetSingle()).ToArray();
            var expected=json.RootElement.GetProperty("expected").EnumerateArray().Select(p=>p.GetSingle()).ToArray();
            var voice=new BitVoiceSampleProvider(new Input(input));voice.SetStage(0);var actual=new float[input.Length];voice.Read(actual,0,actual.Length);
            Check(actual.Zip(expected).All(p=>Math.Abs(p.First-p.Second)<1e-6),"actual TypeScript PCM matches NET");
        }
        var speaker=new Player { Id=1,IsLocal=true };
        var state=new AmongUsState {Mod=AmongUsModType.NebulaOnTheShip,GameState=GameState.Tasks,Players=[speaker],NosBitCharacters=[new(){PlayerId=1,Stage=0}]};
        Check(NosBitCharacterState.For(state,speaker)==0,"active bit stage");
        state.GameState=GameState.Discussion;Check(NosBitCharacterState.For(state,speaker)==-1,"meeting bypass");
        state.GameState=GameState.Tasks;state.NosBitCharacters[0].Stage=5;Check(NosBitCharacterState.For(state,speaker)==5,"64bit remains active");
        state.NosBitCharacters[0].Stage=6;Check(NosBitCharacterState.For(state,speaker)==-1,"final stage bypass");
        state.NosBitCharacters[0].Stage=5;
        var wire=JsonSerializer.SerializeToElement(MobileGameStateWire.Create(state,new LobbySettings(),null));
        Check(wire.GetProperty("gameState").GetProperty("nosBitCharacters")[0].GetProperty("stage").GetInt32()==5,"64bit state reaches Web/mobile wire");
        Console.WriteLine("PASS NET bit voice: six quality stages including64bit, quiet speech, PCM continuity, silence, passthrough, TS parity and meeting/final/mobile wire");
        return 0;
    }
    private sealed class Input(float[] data):ISampleProvider
    {
        private int cursor;
        public WaveFormat WaveFormat=>WaveFormat.CreateIeeeFloatWaveFormat(48000,1);
        public int Read(float[] buffer,int offset,int count) {int n=Math.Min(count,data.Length-cursor);Array.Copy(data,cursor,buffer,offset,n);cursor+=n;return n;}
    }
}

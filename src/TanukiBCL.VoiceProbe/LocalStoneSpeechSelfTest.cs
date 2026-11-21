using NAudio.Wave;
namespace TanukiBCL.VoiceProbe;
internal static class LocalStoneSpeechSelfTest
{
    public static async Task<int> Run()
    {
        if (!LocalStoneSpeech.Available) throw new InvalidOperationException("Prepare the shared speech cache first");
        string preference=Path.Combine(LocalStoneSpeech.Root,"preferences.json");
        byte[]? previous=File.Exists(preference)?File.ReadAllBytes(preference):null;
        try
        {
            if (!LocalStoneSpeech.Save(true)) throw new Exception("Local preference");
            string corpus=Path.Combine(Path.GetDirectoryName(LocalStoneSpeech.Root)!,"evaluation");
            foreach (var (file,word) in new[] {("Microsoft_Haruka_Desktop-0.wav","インポスター"),("Microsoft_Haruka_Desktop-2.wav","クルーメイト")})
            {
                using var wave=new WaveFileReader(Path.Combine(corpus,file));
                using var pcm=new MemoryStream();wave.CopyTo(pcm);
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(18));
                string? result=await LocalStoneSpeech.Transcribe(pcm.ToArray(),8,timeout.Token);
                if (result!=word) throw new Exception("Native local word recognition failed");
            }
            LocalStoneSpeech.Close();
            Console.WriteLine("PASS .NET local SenseVoice: shared worker, Japanese role words, no API, bounded timeout and cleanup");return 0;
        }
        finally {LocalStoneSpeech.Close();if(previous==null)File.Delete(preference);else File.WriteAllBytes(preference,previous);}
    }
}

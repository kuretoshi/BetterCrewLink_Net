using NAudio.Wave;
namespace TanukiBCL.VoiceProbe;
internal static class LocalStoneSpeechSelfTest
{
    public static async Task<int> Run()
    {
        string[] directories = ["runtime", "System32"];
        if (VcRuntime.HasFiles(directories, _ => false) ||
            VcRuntime.HasFiles(directories, p => Path.GetFileName(p) != "msvcp140_1.dll") ||
            !VcRuntime.HasFiles(directories, p => p.StartsWith("System32")) ||
            !VcRuntime.HasFiles(directories, p => p.StartsWith("runtime")) ||
            VcRuntime.SetupUrl(false) != VcRuntime.DownloadPage || VcRuntime.SetupUrl(true) != VcRuntime.ModelInstaller)
            throw new Exception("Shared speech prerequisite detection or download routing failed");
        LocalStoneSpeech.RefreshStatus();
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
            foreach (var (file,kind,words) in new[] { ("Microsoft_Haruka_Desktop-6.wav","hero",new[]{"変身"}), ("Microsoft_Haruka_Desktop-3.wav","ng",new[]{"タスク"}), ("Microsoft_Haruka_Desktop-5.wav","ng",new[]{"インポスター"}) })
            {
                using var wave = new WaveFileReader(Path.Combine(corpus,file));
                using var pcm = new MemoryStream(); wave.CopyTo(pcm);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
                string? text = await LocalStoneSpeech.TranscribeText(pcm.ToArray(),timeout.Token);
                if (text == null || !(kind == "hero" ? NosHeroSpeech.HeardTransformation(text,words,true) : NosHeroSpeech.HeardForbiddenWord(text,words,true))) throw new Exception("Shared full-transcript hero/NG recognition failed");
            }
            LocalStoneSpeech.Close();
            Console.WriteLine("PASS .NET local SenseVoice: shared worker, Japanese role words, no API, bounded timeout and cleanup");return 0;
        }
        finally {LocalStoneSpeech.Close();if(previous==null)File.Delete(preference);else File.WriteAllBytes(preference,previous);}
    }
}

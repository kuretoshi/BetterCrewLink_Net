using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TanukiBCL.Client;

internal enum InquiryTag { Question, Bug, Request }

internal sealed record InquiryAttachment(string Path, string Name, long Size);

// The v3.2.7 inquiry form posts a Discord forum thread with the selected tag,
// optional files and the application's support log. Keep this destination
// separate from the voice server and never send without the form's Send action.
internal static class InquirySubmission
{
    private const long AttachmentLimit = 24L * 1024 * 1024;
    private const int LogTailBytes = 1024 * 1024;
    private const string DefaultWebhook =
        "https://discord.com/api/webhooks/1520080489564602400/JosqdjaanmRHyA0Ba3dbbmBNqL1NlcoV4Qdxuf2_fvSr0DoEfaD70-CPcJ9I0AHKWLtH";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(60) };

    internal static IReadOnlyList<InquiryAttachment> GetSupportLogs()
    {
        var path = SupportLog.DefaultPath;
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length > 0
                ? [new InquiryAttachment(path, file.Name, Math.Min(file.Length, LogTailBytes))]
                : [];
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    internal static async Task SubmitAsync(string subject, string body, InquiryTag tag,
        IEnumerable<InquiryAttachment> selectedFiles, CancellationToken cancellationToken = default,
        HttpClient? httpClient = null, Uri? destination = null,
        IEnumerable<InquiryAttachment>? supportLogs = null)
    {
        subject = Sanitize(subject, 100);
        body = Sanitize(body, 1800);
        if (subject.Length == 0 || body.Length == 0)
            throw new InvalidOperationException("件名と本文を入力してください。");
        var tagId = tag switch
        {
            InquiryTag.Question => "1506509370312233040",
            InquiryTag.Bug => "1506509601950929017",
            InquiryTag.Request => "1506509658234294292",
            _ => throw new ArgumentOutOfRangeException(nameof(tag))
        };
        var logs = (supportLogs ?? GetSupportLogs()).ToArray();
        var files = selectedFiles.Concat(logs).ToArray();
        long total = 0;
        foreach (var file in files)
        {
            var info = new FileInfo(file.Path);
            if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
                throw new InvalidOperationException($"添付できないファイルです: {file.Name}");
            total = checked(total + (logs.Contains(file) ? Math.Min(info.Length, LogTailBytes) : info.Length));
            if (total > AttachmentLimit)
                throw new InvalidOperationException("添付ファイルの合計サイズは24MB以下にしてください。");
        }
        var payload = JsonSerializer.Serialize(new
        {
            thread_name = subject,
            content = body,
            applied_tags = new[] { tagId },
            allowed_mentions = new { parse = Array.Empty<string>() }
        });
        var configured = Environment.GetEnvironmentVariable("BETTERCREWLINK_DISCORD_FORUM_WEBHOOK_URL");
        var webhook = destination ?? new Uri(string.IsNullOrWhiteSpace(configured) ? DefaultWebhook : configured);
        var builder = new UriBuilder(webhook);
        builder.Query = string.IsNullOrEmpty(builder.Query) ? "wait=true" : builder.Query.TrimStart('?') + "&wait=true";
        using var request = new HttpRequestMessage(HttpMethod.Post, builder.Uri);
        if (files.Length == 0)
        {
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        }
        else
        {
            var multipart = new MultipartFormDataContent();
            multipart.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");
            for (var index = 0; index < files.Length; index++)
            {
                var file = files[index];
                byte[] bytes;
                if (logs.Contains(file))
                    bytes = await ReadTailAsync(file.Path, LogTailBytes, cancellationToken);
                else
                    bytes = await File.ReadAllBytesAsync(file.Path, cancellationToken);
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                multipart.Add(content, $"files[{index}]", System.IO.Path.GetFileName(file.Path));
            }
            request.Content = multipart;
        }
        using var response = await (httpClient ?? Client).SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Discordへの投稿に失敗しました。HTTP {(int)response.StatusCode}");
    }

    private static string Sanitize(string value, int limit)
    {
        var normalized = value.Trim().Replace("\r\n", "\n");
        return normalized[..Math.Min(normalized.Length, limit)];
    }

    private static async Task<byte[]> ReadTailAsync(string path, int limit, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(Math.Max(0, stream.Length - limit), SeekOrigin.Begin);
        var bytes = new byte[(int)Math.Min(stream.Length, limit)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return bytes;
    }

    internal static async Task VerifyAsync()
    {
        using var handler = new TestHandler();
        using var client = new HttpClient(handler);
        await SubmitAsync("  件名  ", " 本文 ", InquiryTag.Bug, [], httpClient: client,
            destination: new Uri("https://example.invalid/submit"), supportLogs: []);
        using var payload = JsonDocument.Parse(handler.Content!);
        if (handler.Count != 1 || handler.Method != HttpMethod.Post ||
            !handler.Uri!.Query.Contains("wait=true") ||
            payload.RootElement.GetProperty("thread_name").GetString() != "件名" ||
            payload.RootElement.GetProperty("content").GetString() != "本文" ||
            payload.RootElement.GetProperty("applied_tags")[0].GetString() != "1506509601950929017" ||
            payload.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength() != 0)
            throw new InvalidOperationException("Inquiry wire payload differs from v3.2.7");
        try
        {
            await SubmitAsync(" ", "body", InquiryTag.Question, [], httpClient: client,
                destination: new Uri("https://example.invalid/submit"), supportLogs: []);
            throw new InvalidOperationException("Blank inquiry was accepted");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("件名と本文")) { }
        if (handler.Count != 1) throw new InvalidOperationException("Invalid inquiry was transmitted");
        var attachmentPath = System.IO.Path.GetTempFileName();
        var logPath = System.IO.Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(attachmentPath, "chosen-file", Encoding.UTF8);
            await File.WriteAllBytesAsync(logPath, Enumerable.Repeat((byte)'a', LogTailBytes + 5).ToArray());
            await SubmitAsync("subject", "body", InquiryTag.Request,
                [new InquiryAttachment(attachmentPath, "chosen.txt", new FileInfo(attachmentPath).Length)],
                httpClient: client, destination: new Uri("https://example.invalid/submit"),
                supportLogs: [new InquiryAttachment(logPath, "debug.log", LogTailBytes)]);
            if (handler.Count != 2 || handler.Content is null ||
                !handler.Content.Contains("files[0]") || !handler.Content.Contains("files[1]") ||
                !handler.Content.Contains("chosen-file") || !handler.Content.Contains("1506509658234294292"))
                throw new InvalidOperationException("Inquiry attachments or request tag were lost");
        }
        finally
        {
            File.Delete(attachmentPath);
            File.Delete(logPath);
        }
        Console.WriteLine("[PASS] Inquiry form validates and composes v3.2.7 forum payload without network transmission");
    }

    private sealed class TestHandler : HttpMessageHandler
    {
        internal int Count;
        internal HttpMethod? Method;
        internal Uri? Uri;
        internal string? Content;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            Method = request.Method;
            Uri = request.RequestUri;
            Content = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

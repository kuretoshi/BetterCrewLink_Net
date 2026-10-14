using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TanukiBCL.Client;

internal enum InquiryTag { Question, Bug, Request }

internal sealed record InquiryAttachment(string Path, string Name, long Size);

// The relay chooses the product-specific Issue repository and Discord destination.
// Never ship a Discord webhook URL in the desktop application.
internal static class InquirySubmission
{
    private const long AttachmentLimit = 24L * 1024 * 1024;
    private const int LogTailBytes = 1024 * 1024;
    private static readonly Uri DefaultDestination =
        new("https://debug-auth.kuretoshi.work/v1/inquiries");
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly Regex IssueUrlPattern = new(
        @"\Ahttps://github\.com/kuretoshi/BetterCrewLink_Net/issues/[1-9][0-9]*\z",
        RegexOptions.CultureInvariant);

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

    internal static async Task<Uri> SubmitAsync(string subject, string body, InquiryTag tag,
        IEnumerable<InquiryAttachment> selectedFiles, bool publicIssueConsent,
        CancellationToken cancellationToken = default,
        HttpClient? httpClient = null, Uri? destination = null,
        IEnumerable<InquiryAttachment>? supportLogs = null)
    {
        if (!publicIssueConsent)
            throw new InvalidOperationException("公開される内容を確認してください。");
        subject = Sanitize(subject, 100);
        body = Sanitize(body, 1800);
        if (subject.Length == 0 || body.Length == 0)
            throw new InvalidOperationException("件名と本文を入力してください。");

        var tagName = tag switch
        {
            InquiryTag.Question => "question",
            InquiryTag.Bug => "bug",
            InquiryTag.Request => "request",
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
            product = "net",
            public_issue_consent = true,
            tag = tagName,
            subject,
            body
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, destination ?? DefaultDestination);
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
                content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                multipart.Add(content, $"files[{index}]", System.IO.Path.GetFileName(file.Path));
            }
            request.Content = multipart;
        }

        using var response = await (httpClient ?? Client).SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"お問い合わせの送信に失敗しました。HTTP {(int)response.StatusCode}");
        using var result = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (!result.RootElement.TryGetProperty("issue_url", out var issueUrl) ||
            issueUrl.ValueKind != JsonValueKind.String ||
            !IssueUrlPattern.IsMatch(issueUrl.GetString() ?? string.Empty))
            throw new InvalidOperationException(".NET版の公開Issueへの投稿を確認できませんでした。");
        return new Uri(issueUrl.GetString()!);
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
        var createdIssue = await SubmitAsync("  件名  ", " 本文 ", InquiryTag.Bug, [], true, httpClient: client,
            destination: new Uri("https://example.invalid/submit"), supportLogs: []);
        using var payload = JsonDocument.Parse(handler.Content!);
        if (handler.Count != 1 || handler.Method != HttpMethod.Post ||
            handler.Uri != new Uri("https://example.invalid/submit") ||
            payload.RootElement.GetProperty("product").GetString() != "net" ||
            payload.RootElement.GetProperty("public_issue_consent").GetBoolean() != true ||
            payload.RootElement.GetProperty("tag").GetString() != "bug" ||
            payload.RootElement.GetProperty("subject").GetString() != "件名" ||
            payload.RootElement.GetProperty("body").GetString() != "本文" ||
            createdIssue.AbsoluteUri != "https://github.com/kuretoshi/BetterCrewLink_Net/issues/123")
            throw new InvalidOperationException("Inquiry relay payload differs from the server contract");
        try
        {
            await SubmitAsync(" ", "body", InquiryTag.Question, [], true, httpClient: client,
                destination: new Uri("https://example.invalid/submit"), supportLogs: []);
            throw new InvalidOperationException("Blank inquiry was accepted");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("件名と本文")) { }
        if (handler.Count != 1) throw new InvalidOperationException("Invalid inquiry was transmitted");
        try
        {
            await SubmitAsync("subject", "body", InquiryTag.Question, [], false, httpClient: client,
                destination: new Uri("https://example.invalid/submit"), supportLogs: []);
            throw new InvalidOperationException("Inquiry without publication consent was accepted");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("公開される内容")) { }
        if (handler.Count != 1) throw new InvalidOperationException("Unapproved inquiry was transmitted");

        var attachmentPath = System.IO.Path.GetTempFileName();
        var logPath = System.IO.Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(attachmentPath, "chosen-file", Encoding.UTF8);
            await File.WriteAllBytesAsync(logPath, Enumerable.Repeat((byte)'a', LogTailBytes + 5).ToArray());
            await SubmitAsync("subject", "body", InquiryTag.Request,
                [new InquiryAttachment(attachmentPath, "chosen.txt", new FileInfo(attachmentPath).Length)],
                true,
                httpClient: client, destination: new Uri("https://example.invalid/submit"),
                supportLogs: [new InquiryAttachment(logPath, "debug.log", LogTailBytes)]);
            if (handler.Count != 2 || handler.Content is null ||
                !handler.Content.Contains("files[0]") || !handler.Content.Contains("files[1]") ||
                !handler.Content.Contains("chosen-file") || !handler.Content.Contains("\"product\":\"net\"") ||
                !handler.Content.Contains("\"tag\":\"request\""))
                throw new InvalidOperationException("Inquiry attachments or request tag were lost");
            handler.ResponseIssueUrl = "https://github.com/kuretoshi/TanukiBCL/issues/123";
            try
            {
                await SubmitAsync("subject", "body", InquiryTag.Bug, [], true, httpClient: client,
                    destination: new Uri("https://example.invalid/submit"), supportLogs: []);
                throw new InvalidOperationException("Wrong repository Issue URL was accepted");
            }
            catch (InvalidOperationException error) when (error.Message.Contains(".NET版の公開Issue")) { }
        }
        finally
        {
            File.Delete(attachmentPath);
            File.Delete(logPath);
        }
        Console.WriteLine("[PASS] Inquiry form validates and composes relay payload without network transmission");
    }

    private sealed class TestHandler : HttpMessageHandler
    {
        internal int Count;
        internal HttpMethod? Method;
        internal Uri? Uri;
        internal string? Content;
        internal string ResponseIssueUrl = "https://github.com/kuretoshi/BetterCrewLink_Net/issues/123";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            Method = request.Method;
            Uri = request.RequestUri;
            Content = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { issue_url = ResponseIssueUrl }))
            };
        }
    }
}

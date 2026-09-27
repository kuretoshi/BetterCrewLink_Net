using System.Net;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterCrewLinkKai.DotNet.Services;

public sealed class ObsOverlayService : IDisposable
{
    public const string Url = "http://127.0.0.1:47777/";

    private readonly object syncRoot = new();
    private static readonly object avatarCacheLock = new();
    private static readonly Dictionary<string, byte[]> AvatarCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    private HttpListener? listener;
    private CancellationTokenSource? cancellationTokenSource;
    private Task? listenTask;
    private ObsOverlaySnapshot snapshot = ObsOverlaySnapshot.Empty;
    private string requiredSecret = string.Empty;

    public bool IsRunning => listener?.IsListening == true;

    public void Start(string secret)
    {
        if (IsRunning)
        {
            requiredSecret = secret;
            return;
        }

        requiredSecret = secret;
        cancellationTokenSource = new CancellationTokenSource();
        listener = new HttpListener();
        listener.Prefixes.Add(Url);
        listener.Start();
        listenTask = Task.Run(() => ListenAsync(cancellationTokenSource.Token));
    }

    public void Stop()
    {
        cancellationTokenSource?.Cancel();
        if (listener is not null)
        {
            try
            {
                listener.Stop();
                listener.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        listener = null;
        cancellationTokenSource?.Dispose();
        cancellationTokenSource = null;
        listenTask = null;
    }

    public void UpdateSnapshot(ObsOverlaySnapshot nextSnapshot)
    {
        lock (syncRoot)
        {
            snapshot = nextSnapshot;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener is { IsListening: true } currentListener)
        {
            HttpListenerContext context;
            try
            {
                context = await currentListener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(context), cancellationToken);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        if (!IsAuthorized(context.Request))
        {
            await WriteUnauthorizedAsync(context);
            return;
        }

        var path = context.Request.Url?.AbsolutePath ?? "/";
        if (string.Equals(path, "/state", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context);
            return;
        }

        if (string.Equals(path, "/avatar", StringComparison.OrdinalIgnoreCase))
        {
            await WriteAvatarAsync(context);
            return;
        }

        await WriteHtmlAsync(context);
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        return string.IsNullOrWhiteSpace(requiredSecret) ||
               string.Equals(request.QueryString["secret"], requiredSecret, StringComparison.Ordinal);
    }

    private static async Task WriteUnauthorizedAsync(HttpListenerContext context)
    {
        var bytes = Encoding.UTF8.GetBytes("Unauthorized");
        context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private async Task WriteJsonAsync(HttpListenerContext context)
    {
        ObsOverlaySnapshot currentSnapshot;
        lock (syncRoot)
        {
            currentSnapshot = snapshot;
        }

        var json = JsonSerializer.Serialize(currentSnapshot, jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        context.Response.Headers["Pragma"] = "no-cache";
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static async Task WriteHtmlAsync(HttpListenerContext context)
    {
        var bytes = Encoding.UTF8.GetBytes(Html);
        context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        context.Response.Headers["Pragma"] = "no-cache";
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static async Task WriteAvatarAsync(HttpListenerContext context)
    {
        var main = context.Request.QueryString["main"] ?? "#50EF39";
        var shadow = context.Request.QueryString["shadow"] ?? "#15A742";
        if (!TryParseHexColor(main, out var mainColor) ||
            !TryParseHexColor(shadow, out var shadowColor))
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            context.Response.Close();
            return;
        }

        var cacheKey = $"{mainColor.R:X2}{mainColor.G:X2}{mainColor.B:X2}-{shadowColor.R:X2}{shadowColor.G:X2}{shadowColor.B:X2}";
        byte[] bytes;
        lock (avatarCacheLock)
        {
            if (!AvatarCache.TryGetValue(cacheKey, out bytes!))
            {
                bytes = CreateAvatarPng(mainColor, shadowColor);
                AvatarCache[cacheKey] = bytes;
            }
        }

        context.Response.ContentType = "image/png";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static byte[] CreateAvatarPng(Color main, Color shadow)
    {
        var uri = new Uri("pack://application:,,,/Assets/avatar/player-template.png", UriKind.Absolute);
        var resource = Application.GetResourceStream(uri) ??
                       throw new InvalidOperationException("Avatar template resource was not found.");
        using var stream = resource.Stream;
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var formatted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var stride = formatted.PixelWidth * 4;
        var pixels = new byte[stride * formatted.PixelHeight];
        formatted.CopyPixels(pixels, stride, 0);

        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var b = pixels[i];
            var g = pixels[i + 1];
            var r = pixels[i + 2];
            var a = pixels[i + 3];
            if (a == 0)
            {
                continue;
            }

            var (h, s, _) = RgbToHsv(r, g, b);
            if (s > 0.4 && (IsBetween(h, 240, 30) || IsBetween(h, 0, 100) || IsBetween(h, 120, 40)))
            {
                var color = Mix(Colors.Black, shadow, b / 255d);
                color = Mix(color, main, r / 255d);
                color = Mix(color, Color.FromRgb(0x9A, 0xCA, 0xD5), g / 255d);
                pixels[i] = color.B;
                pixels[i + 1] = color.G;
                pixels[i + 2] = color.R;
            }
        }

        var source = BitmapSource.Create(
            formatted.PixelWidth,
            formatted.PixelHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static bool TryParseHexColor(string value, out Color color)
    {
        color = Colors.Transparent;
        var hex = value.Trim().TrimStart('#');
        if (hex.Length != 6)
        {
            return false;
        }

        try
        {
            color = Color.FromRgb(
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static Color Mix(Color current, Color next, double weight)
    {
        weight = Math.Clamp(weight, 0, 1);
        var inverse = 1 - weight;
        return Color.FromRgb(
            (byte)Math.Round((current.R * inverse) + (next.R * weight)),
            (byte)Math.Round((current.G * inverse) + (next.G * weight)),
            (byte)Math.Round((current.B * inverse) + (next.B * weight)));
    }

    private static bool IsBetween(double h, double h1, double maxDifference)
    {
        return 180 - Math.Abs(Math.Abs(h - h1) - 180) < maxDifference;
    }

    private static (double H, double S, double V) RgbToHsv(byte r, byte g, byte b)
    {
        var red = r / 255d;
        var green = g / 255d;
        var blue = b / 255d;
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var chroma = max - min;
        var hue = 0d;

        if (chroma != 0)
        {
            if (max == red)
            {
                hue = 60 * (((green - blue) / chroma) % 6);
            }
            else if (max == green)
            {
                hue = 60 * (((blue - red) / chroma) + 2);
            }
            else
            {
                hue = 60 * (((red - green) / chroma) + 4);
            }
        }

        if (hue < 0)
        {
            hue += 360;
        }

        return (hue, max == 0 ? 0 : chroma / max, max);
    }

    private const string Html = """
<!doctype html>
<html lang="ja">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>BetterCrewLinkKai.net OBS Overlay</title>
  <style>
    html, body {
      margin: 0;
      width: 100%;
      height: 100%;
      min-height: 100%;
      background: transparent;
      overflow: hidden;
      font-family: "Varela", "Segoe UI", "Meiryo", sans-serif;
    }
    #overlay {
      position: fixed;
      display: flex;
      gap: 14px;
      padding: 0;
      color: white;
      z-index: 2;
    }
    #overlay.hidden {
      display: none;
    }
    #overlay.right, #overlay.right-box {
      right: 18px;
      top: 50%;
      transform: translateY(-50%);
      flex-direction: column;
      align-items: flex-end;
    }
    #overlay.left, #overlay.left-box {
      left: 18px;
      top: 50%;
      transform: translateY(-50%);
      flex-direction: column;
      align-items: flex-start;
    }
    #overlay.top {
      left: 50%;
      top: 8px;
      transform: translateX(-50%);
      flex-direction: row;
      align-items: center;
      justify-content: center;
      gap: 10px;
      min-width: 320px;
      min-height: 72px;
    }
    #overlay.bottom-left {
      left: 10px;
      bottom: 10px;
      flex-direction: row;
      align-items: center;
      justify-content: flex-start;
      gap: 10px;
    }
    #overlay.box {
      padding: 10px;
      background: rgba(18, 16, 23, .54);
    }
    .row {
      display: flex;
      align-items: center;
      justify-content: flex-end;
      gap: 12px;
      opacity: var(--opacity, 1);
      width: max-content;
      max-width: 360px;
    }
    .left .row,
    .left-box .row,
    .bottom-left .row {
      flex-direction: row-reverse;
      justify-content: flex-start;
    }
    .top .row,
    .bottom-left .row,
    .right-box .row,
    .left-box .row {
      gap: 0;
    }
    .row.hidden {
      display: none;
    }
    .name {
      max-width: 230px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-size: 14px;
      font-weight: 800;
      line-height: 1;
      color: #fff;
      background: rgba(32, 30, 36, .42);
      border-radius: 14px;
      padding: 6px 9px;
      text-shadow: 0 2px 0 #000, 0 0 5px #000, 0 0 8px #000;
    }
    .top .name,
    .bottom-left .name,
    .right-box .name,
    .left-box .name {
      display: none;
    }
    .crew-wrap {
      position: relative;
      width: 64px;
      height: 64px;
      flex: 0 0 auto;
      border-radius: 50%;
      background: rgba(18, 16, 23, .54);
      overflow: hidden;
      box-shadow: 0 0 0 1px rgba(255,255,255,.15), 0 0 var(--glow, 0) #32ff7e;
      transition: box-shadow .18s ease, filter .18s ease;
    }
    .top .crew-wrap,
    .bottom-left .crew-wrap {
      background: transparent;
      box-shadow: none;
    }
    .crew-wrap img {
      position: absolute;
      display: block;
      height: auto;
      pointer-events: none;
    }
    .avatar {
      left: 0;
      top: 0;
      width: 100%;
      height: 100%;
      object-fit: contain;
    }
    .talking .crew-wrap {
      box-shadow: 0 0 0 2px var(--talk-color, #32ff7e), 0 0 18px var(--talk-color, #32ff7e), 0 0 30px var(--talk-color, #32ff7e);
      filter: brightness(1.1);
    }
    .warning {
      position: absolute;
      left: 50%;
      top: 50%;
      width: 20px;
      height: 20px;
      transform: translate(-50%, -50%);
      border-radius: 50%;
      background: var(--warning-bg, #ea3c2a);
      border: 2px solid var(--warning-border, #690a00);
      box-shadow: 0 1px 4px #000;
      box-sizing: border-box;
      padding: 2px;
    }
    .warning svg {
      width: 100%;
      height: 100%;
      fill: white;
      display: block;
    }
    .local .crew-wrap {
      width: 64px;
      height: 64px;
    }
    .local .name {
      font-size: 14px;
    }
    .cosmetic.body {
      transform: scale(2) translate(7px, -12px);
      transform-origin: 50% 50%;
    }
    .local .cosmetic.body {
      transform: scale(2) translate(12px, -22px);
    }
    #meeting {
      position: fixed;
      left: 8.8%;
      top: 33%;
      display: flex;
      flex-direction: column;
      gap: 22px;
      z-index: 1;
    }
    .meeting-card {
      width: 345px;
      height: 76px;
      border-radius: 8px;
      border: 4px solid var(--meeting-color, #32ff7e);
      box-shadow:
        0 0 8px var(--meeting-color, #32ff7e),
        0 0 18px var(--meeting-color, #32ff7e);
      opacity: .92;
      box-sizing: border-box;
    }
  </style>
</head>
<body>
  <div id="meeting"></div>
  <div id="overlay"></div>
  <script>
    const secret = new URLSearchParams(location.search).get('secret') || '';
    async function refresh() {
      const response = await fetch('/state?secret=' + encodeURIComponent(secret), { cache: 'no-store' });
      if (!response.ok) {
        document.getElementById('overlay').innerHTML = '';
        return;
      }
      const state = await response.json();
      const root = document.getElementById('overlay');
      const meetingRoot = document.getElementById('meeting');
      if (!state.visible) {
        root.innerHTML = '';
        meetingRoot.innerHTML = '';
        return;
      }

      const local = state.localPlayer;
      const players = state.players || [];
      const position = normalizePosition(state.position);
      root.className = position + (position.endsWith('-box') ? ' box' : '');
      const compact = !!state.compactOverlay;
      const rows = [local, ...players].filter(player => !compact || player.talking);
      root.innerHTML = rows.map((player, index) => renderRow(player, index === 0)).join('');
      meetingRoot.innerHTML = state.discussion && state.meetingOverlay
        ? (state.meetingPlayers || []).filter(player => player.talking).map(renderMeetingCard).join('')
        : '';
    }

    function renderMeetingCard(player) {
      return `<div class="meeting-card" style="--meeting-color:${escapeAttribute(player.mainColor || '#32ff7e')}"></div>`;
    }

    function renderRow(player, isLocal) {
      const opacity = player.audible === false ? .45 : 1;
      const talking = player.talking ? 'talking' : '';
      return `<div class="row ${isLocal ? 'local' : ''} ${talking}" style="--opacity:${opacity};--talk-color:${escapeAttribute(player.mainColor || '#32ff7e')}">
        <div class="name">${escapeHtml(player.name)}</div>
        ${renderCrew(player)}
      </div>`;
    }

    function normalizePosition(value) {
      switch (String(value || '').toLowerCase()) {
        case 'hidden':
          return 'hidden';
        case 'top':
          return 'top';
        case 'bottom-left':
          return 'bottom-left';
        case 'left':
          return 'left';
        case 'left-box':
          return 'left-box';
        case 'right-box':
          return 'right-box';
        default:
          return 'right';
      }
    }

    function renderCrew(player) {
      return `<div class="crew-wrap">
        ${renderCosmetic(player.hatBackImage, player.hatPlacement, true)}
        <img class="avatar" src="${avatarUrl(player)}" alt="">
        ${renderCosmetic(player.skinImage, player.skinPlacement, true)}
        ${renderCosmetic(player.visorImage, player.visorPlacement, false)}
        ${renderCosmetic(player.hatFrontImage, player.hatPlacement, true)}
        ${renderWarning(player)}
      </div>`;
    }

    function renderWarning(player) {
      if (!player.connectionWarningVisible || !player.connectionWarningIconPath) {
        return '';
      }

      return `<span class="warning" style="--warning-bg:${player.connectionWarningBackground};--warning-border:${player.connectionWarningBorder}">
        <svg viewBox="0 0 24 24" aria-hidden="true"><path d="${escapeAttribute(player.connectionWarningIconPath)}"></path></svg>
      </span>`;
    }

    function avatarUrl(player) {
      return '/avatar?secret=' + encodeURIComponent(secret) +
        '&main=' + encodeURIComponent(player.mainColor || '#50EF39') +
        '&shadow=' + encodeURIComponent(player.shadowColor || '#15A742');
    }

    function renderCosmetic(src, placement, bodyLayer) {
      if (!src || !placement) {
        return '';
      }

      const left = Number.isFinite(placement.leftPercent) ? placement.leftPercent : 0;
      const top = Number.isFinite(placement.topPercent) ? placement.topPercent : 0;
      const width = Number.isFinite(placement.widthPercent) ? placement.widthPercent : 100;
      return `<img class="cosmetic ${bodyLayer ? 'body' : ''}" src="${escapeAttribute(src)}" style="left:${left}%;top:${top}%;width:${width}%">`;
    }

    function escapeHtml(value) {
      return String(value ?? '').replace(/[&<>"']/g, (c) => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;' }[c]));
    }

    function escapeAttribute(value) {
      return escapeHtml(value).replace(/`/g, '&#96;');
    }

    refresh();
    setInterval(refresh, 250);
  </script>
</body>
</html>
""";
}

public sealed record ObsOverlaySnapshot(
    bool Visible,
    bool Discussion,
    bool MeetingOverlay,
    bool CompactOverlay,
    string Position,
    ObsOverlayLocalPlayer LocalPlayer,
    IReadOnlyList<ObsOverlayPlayer> Players,
    IReadOnlyList<ObsOverlayMeetingPlayer> MeetingPlayers)
{
    public static ObsOverlaySnapshot Empty { get; } = new(
        false,
        false,
        false,
        false,
        "right",
        new ObsOverlayLocalPlayer("ROBBER", "MENU", "#50EF39", "#15A742", false, false, false, "", "", "", "", ObsOverlayCosmetic.Empty, ObsOverlayCosmetic.Empty, ObsOverlayCosmetic.Empty),
        [],
        []);
}

public sealed record ObsOverlayLocalPlayer(
    string Name,
    string LobbyCode,
    string MainColor,
    string ShadowColor,
    bool Talking,
    bool Muted,
    bool Deafened,
    string HatBackImage,
    string HatFrontImage,
    string SkinImage,
    string VisorImage,
    ObsOverlayCosmetic HatPlacement,
    ObsOverlayCosmetic SkinPlacement,
    ObsOverlayCosmetic VisorPlacement);

public sealed record ObsOverlayPlayer(
    string Name,
    string MainColor,
    string ShadowColor,
    bool Talking,
    bool UsingRadio,
    bool Audible,
    bool Muted,
    bool ConnectionWarningVisible,
    string ConnectionWarningText,
    string ConnectionWarningIconPath,
    string ConnectionWarningBackground,
    string ConnectionWarningBorder,
    string HatBackImage,
    string HatFrontImage,
    string SkinImage,
    string VisorImage,
    ObsOverlayCosmetic HatPlacement,
    ObsOverlayCosmetic SkinPlacement,
    ObsOverlayCosmetic VisorPlacement);

public sealed record ObsOverlayMeetingPlayer(
    int PlayerId,
    string MainColor,
    bool Talking);

public sealed record ObsOverlayCosmetic(
    double LeftPercent,
    double TopPercent,
    double WidthPercent)
{
    public static ObsOverlayCosmetic Empty { get; } = new(0, 0, 0);
}

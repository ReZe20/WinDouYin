using Microsoft.UI.Xaml.Media.Imaging;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;
using WinDouYin.Contracts;

namespace WinDouYin.Controls;

/// <summary>
/// 模糊封面铺底：拉封面图 → 缩到 128 宽 → 三趟盒式模糊（近似高斯）→ SoftwareBitmapSource。
/// 同一条取图+解码管线也服务作者头像（<see cref="LoadAvatarAsync"/>，缩到 96 宽、不模糊）。
/// WinUI 3 没有内置模糊特效（Microsoft.WinUI.xml 里既没有 BlurEffect 也没有 CompositionEffectBrush），
/// Win2D 又不在依赖里，所以走这套 CPU 方案：128 宽的图铺满上千像素的舞台，本身就是重糊，盒式模糊只是再抹掉残余细节。
/// </summary>
internal static class BlurCover
{
    private const int TargetWidth = 128;
    private const int AvatarWidth = 96;
    private const int BlurRadius = 6;
    private const int MaxEntries = 24;

    private static readonly HttpClient Http = new();
    private static readonly Dictionary<string, SoftwareBitmapSource> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> CacheOrder = new();

    /// <summary>模糊铺底封面。必须在 UI 线程调用（SoftwareBitmapSource 是 XAML 对象）；拿不到返回 null，铺底退化成黑。</summary>
    public static Task<SoftwareBitmapSource?> LoadAsync(string url) => LoadCoreAsync(url, TargetWidth, blur: true);

    /// <summary>作者头像：只缩小、不模糊。同样必须从 UI 线程调用。</summary>
    public static Task<SoftwareBitmapSource?> LoadAvatarAsync(string url) => LoadCoreAsync(url, AvatarWidth, blur: false);

    private static async Task<SoftwareBitmapSource?> LoadCoreAsync(string url, int width, bool blur)
    {
        if (url.Length == 0)
        {
            return null;
        }

        try
        {
            if (Cache.TryGetValue(url, out SoftwareBitmapSource? cached))
            {
                return cached;
            }

            SoftwareBitmap? bitmap = await Task.Run(() => FetchAsync(url, width, blur));
            if (bitmap is null)
            {
                System.Diagnostics.Debug.WriteLine("[BlurCover] 解码返回 null：" + url);
                return null;
            }

            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap);
            // 这里绝不能 Dispose 掉 bitmap：SetBitmapAsync 没有"拷贝/不拷贝"两种重载，无法假定它把像素抄走了，
            // SoftwareBitmapSource 是把这张位图当图源留着，Image 每次重新挂上这个 source 都要回去读它一次。
            // 读到已 Close 的位图就是从 Microsoft.UI.Xaml.dll 里返回 RO_E_CLOSED(0x80000013)，
            // 再被裹成 0xC000027B 存根异常带走进程——而"重新挂上"恰恰只发生在缓存命中那一侧（往回切），
            // 首次挂载（往前切）时位图还在 SetBitmapAsync 刚上传的当口，所以表现为往回切必崩、往前切不崩。
            // 128 宽的位图约 110KB，跟着下面的缓存活到被淘汰为止。
            Remember(url, source);
            return source;
        }
        catch (Exception ex)
        {
            /* 封面拿不到不影响播放 */
            System.Diagnostics.Debug.WriteLine("[BlurCover] 加载失败：" + ex.Message + " | " + url);
            return null;
        }
    }

    private static async Task<SoftwareBitmap?> FetchAsync(string url, int width, bool blur)
    {
        try
        {
            return await DecodeAsync(url, width, blur);
        }
        catch (Exception) when (url.Contains("webp", StringComparison.Ordinal))
        {
            // 抖音封面模板后缀多为 webp，而 Windows 的 WebP 解码支持是要另装的可选组件；
            // 换 jpeg 后缀让图床转码再解一次，WIC 对 JPEG 是必装能力。
            int at = url.LastIndexOf("webp", StringComparison.Ordinal);
            return await DecodeAsync(url[..at] + "jpeg" + url[(at + 4)..], width, blur);
        }
    }

    private static async Task<SoftwareBitmap?> DecodeAsync(string url, int width, bool blur)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        Douyin.Feed.ApplyMediaHeaders(request);
        request.Headers.TryAddWithoutValidation("Accept", "image/*");

        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            System.Diagnostics.Debug.WriteLine("[BlurCover] HTTP " + (int)response.StatusCode + " · " + url);
        }

        response.EnsureSuccessStatusCode();

        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        using var random = new InMemoryRandomAccessStream();
        await random.WriteAsync(bytes.AsBuffer());
        random.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(random);

        int height = Math.Max(1, (int)Math.Round(decoder.PixelHeight * (double)width / decoder.PixelWidth));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)width,
            ScaledHeight = (uint)height,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        PixelDataProvider data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        byte[] pixels = data.DetachPixelData();
        if (blur)
        {
            Blur(pixels, width, height);
        }
        return SoftwareBitmap.CreateCopyFromBuffer(
            CryptographicBuffer.CreateFromByteArray(pixels),
            BitmapPixelFormat.Bgra8,
            width,
            height,
            BitmapAlphaMode.Premultiplied);
    }

    /// <summary>三趟横竖盒式模糊，等效高斯；BGRA8 预乘格式直接对四通道滑窗求均值就是对的。</summary>
    private static void Blur(byte[] pixels, int width, int height)
    {
        var scratch = new byte[pixels.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BlurHorizontal(pixels, scratch, width, height);
            BlurVertical(scratch, pixels, width, height);
        }
    }

    private static void BlurHorizontal(byte[] src, byte[] dst, int width, int height)
    {
        int window = 2 * BlurRadius + 1;
        for (int y = 0; y < height; y++)
        {
            int row = y * width * 4;
            int b = 0;
            int g = 0;
            int r = 0;
            int a = 0;
            for (int i = -BlurRadius; i <= BlurRadius; i++)
            {
                int o = row + Math.Clamp(i, 0, width - 1) * 4;
                b += src[o];
                g += src[o + 1];
                r += src[o + 2];
                a += src[o + 3];
            }

            for (int x = 0; x < width; x++)
            {
                int o = row + x * 4;
                dst[o] = (byte)(b / window);
                dst[o + 1] = (byte)(g / window);
                dst[o + 2] = (byte)(r / window);
                dst[o + 3] = (byte)(a / window);

                int leaving = row + Math.Clamp(x - BlurRadius, 0, width - 1) * 4;
                int entering = row + Math.Clamp(x + BlurRadius + 1, 0, width - 1) * 4;
                b += src[entering] - src[leaving];
                g += src[entering + 1] - src[leaving + 1];
                r += src[entering + 2] - src[leaving + 2];
                a += src[entering + 3] - src[leaving + 3];
            }
        }
    }

    private static void BlurVertical(byte[] src, byte[] dst, int width, int height)
    {
        int window = 2 * BlurRadius + 1;
        int stride = width * 4;
        for (int x = 0; x < width; x++)
        {
            int column = x * 4;
            int b = 0;
            int g = 0;
            int r = 0;
            int a = 0;
            for (int i = -BlurRadius; i <= BlurRadius; i++)
            {
                int o = column + Math.Clamp(i, 0, height - 1) * stride;
                b += src[o];
                g += src[o + 1];
                r += src[o + 2];
                a += src[o + 3];
            }

            for (int y = 0; y < height; y++)
            {
                int o = column + y * stride;
                dst[o] = (byte)(b / window);
                dst[o + 1] = (byte)(g / window);
                dst[o + 2] = (byte)(r / window);
                dst[o + 3] = (byte)(a / window);

                int leaving = column + Math.Clamp(y - BlurRadius, 0, height - 1) * stride;
                int entering = column + Math.Clamp(y + BlurRadius + 1, 0, height - 1) * stride;
                b += src[entering] - src[leaving];
                g += src[entering + 1] - src[leaving + 1];
                r += src[entering + 2] - src[leaving + 2];
                a += src[entering + 3] - src[leaving + 3];
            }
        }
    }

    private static void Remember(string url, SoftwareBitmapSource source)
    {
        if (Cache.ContainsKey(url))
        {
            return;
        }

        Cache[url] = source;
        CacheOrder.Enqueue(url);
        while (CacheOrder.Count > MaxEntries)
        {
            Cache.Remove(CacheOrder.Dequeue());
        }
    }
}

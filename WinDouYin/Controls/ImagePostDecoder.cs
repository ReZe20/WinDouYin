
using WinDouYin.Contracts;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace WinDouYin.Controls;

/// <summary>
/// 图文帖解码：图多为 WebP（RIFF/WEBP），XAML 的 BitmapImage 对裸 WebP 加载不稳（常见空白），
/// 所以走系统 WIC（BitmapDecoder）解成 BGRA8 像素，经 SoftwareBitmapSource 交给 XAML。
/// WIC 的 WebP 解码来自可选组件；没装时会把图模板后缀换成 jpeg 让图床转码再解一次。
/// </summary>
/// <summary>解出来的一张图 + 它在原 entries 里的下标（实况地址要与图下标对齐）。</summary>
internal sealed record DecodedImage(int Index, SoftwareBitmapSource Source);

internal static class ImagePostDecoder
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>逐张解码；一张失败就跳过，别拖整组。下标带出去，别让后面的实况错位。</summary>
    public static async Task<IReadOnlyList<DecodedImage>> DecodeAsync(IReadOnlyList<string> urls)
    {
        var result = new List<DecodedImage>(urls.Count);
        for (int i = 0; i < urls.Count; i++)
        {
            string url = urls[i];
            try
            {
                // 整组图动辄几十兆，解码挪到线程池，别在 UI 线程上卡住翻页和播放。
                SoftwareBitmap? bitmap = await Task.Run(() => FetchAsync(url));
                if (bitmap is null)
                {
                    continue;
                }

                SoftwareBitmapSource src = new();
                await src.SetBitmapAsync(bitmap);
                result.Add(new DecodedImage(i, src));
            }
            catch (Exception ex)
            {
                // 一张炸掉不拖整组
                MfLog.Write("app: 图文图解码失败 " + ex.GetType().Name + " " + ex.Message + " | " + url);
            }
        }

        return result;
    }

    private static async Task<SoftwareBitmap?> FetchAsync(string url)
    {
        try
        {
            return await DecodeAsync(url);
        }
        catch (Exception) when (url.Contains("webp", StringComparison.Ordinal))
        {
            // Windows 的 WebP 解码是要另装的可选组件，没装时 WIC 会报 COMPONENTNOTFOUND；
            // 换 jpeg 后缀让图床转码再解一次，WIC 对 JPEG 是必装能力（与 BlurCover 同一条退路）。
            int at = url.LastIndexOf("webp", StringComparison.Ordinal);
            return await DecodeAsync(url[..at] + "jpeg" + url[(at + 4)..]);
        }
    }

    private static async Task<SoftwareBitmap> DecodeAsync(string url)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, url);
        Douyin.Feed.ApplyMediaHeaders(req);
        using HttpResponseMessage resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        byte[] bytes = await resp.Content.ReadAsByteArrayAsync();

        using var random = new InMemoryRandomAccessStream();
        await random.WriteAsync(bytes.AsBuffer());
        random.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(random);

        // SoftwareBitmapSource 只收 BGRA8+Premultiplied，让 WIC 解码时直接出这个格式，省一次整图重画。
        // 返回的 SoftwareBitmap 不能 Dispose：SoftwareBitmapSource 会一直拿它当图源。
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
    }
}

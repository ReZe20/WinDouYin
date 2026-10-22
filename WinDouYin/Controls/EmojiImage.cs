using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinDouYin.Contracts;

namespace WinDouYin.Controls;

/// <summary>
/// 官方表情图 → **PNG 字节**。RichEditBox 的 <c>InsertImage</c> 只认 PNG/JPEG，
/// 而抖音图床对这批表情一律回 <c>image/webp</c>（试过 Accept、x-image-process、加后缀，都不给换格式）。
/// 本机装了 Microsoft.WebpImageExtension（1.2.31，实测），所以 BitmapDecoder 能解 webp，就在本地转一道 PNG。
/// 一次运行一份缓存：同一个表情反复插也不重复下载/转码。
/// </summary>
internal static class EmojiImage
{
    private static readonly HttpClient Http = new();
    private static readonly Dictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

    /// <summary>取回并转成 PNG；拿不到返回 null（调用方如实提示，不假装插上了）。</summary>
    public static async Task<byte[]?> LoadPngAsync(string url)
    {
        if (url.Length == 0)
        {
            return null;
        }

        if (Cache.TryGetValue(url, out byte[]? cached))
        {
            return cached;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            Douyin.Feed.ApplyMediaHeaders(request);
            request.Headers.TryAddWithoutValidation("Accept", "image/*");

            using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            byte[] webp = await response.Content.ReadAsByteArrayAsync();

            using var source = new InMemoryRandomAccessStream();
            await source.WriteAsync(webp.AsBuffer());
            source.Seek(0);

            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(source);
            using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync();

            using var output = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            output.Seek(0);

            using var reader = new DataReader(output.GetInputStreamAt(0));
            await reader.LoadAsync((uint)output.Size);
            byte[] png = new byte[output.Size];
            reader.ReadBytes(png);

            Cache[url] = png;
            return png;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[EmojiImage] 转码失败：" + ex.Message + " | " + url);
            return null;
        }
    }
}

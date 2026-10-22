using System.Text.Json;
using System.Text.Json.Serialization;
using WinDouYin.Models;

namespace WinDouYin.Service;

/// <summary>设置读写的唯一入口。</summary>
public interface IConfigService
{
    Task<AppSettings> LoadAsync();
    Task SaveAsync(AppSettings settings);
}

/// <summary>
/// 非打包应用没有系统管理的应用数据存储（官方指明用文件 I/O 代替），
/// 路径与 WebView2/bridge 的既有惯例保持一致。
/// </summary>
public sealed class ConfigService : IConfigService
{
    private static readonly string FolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDouYin");

    private static readonly string FilePath = Path.Combine(FolderPath, "settings.json");

    public async Task<AppSettings> LoadAsync()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var text = await File.ReadAllTextAsync(FilePath);
                return JsonSerializer.Deserialize(text, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch
        {
            // 读不出就用默认值，不阻断启动
        }
        return new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            await File.WriteAllTextAsync(FilePath,
                JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
        }
        catch
        {
            // 写盘失败不打断交互：改动在本次会话内照旧生效
        }
    }
}

[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}

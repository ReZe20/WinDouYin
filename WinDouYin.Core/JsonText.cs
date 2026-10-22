using System.Text.Json;

namespace WinDouYin;

/// <summary>
/// 把一段裸文本安全拼进 JSON/JS 字面量里（带引号、按 JSON 规则转义）。
///
/// 这三处调用点（bridge 脚本里的 URL 模板、签名页探针的参数、DevTools 的 requestId）原先用
/// <c>JsonSerializer.Serialize(string)</c>——那是反射式序列化器，Native AOT 下不可用（IL2026/IL3050）。
/// 需求本身只是"转义一个字符串"，所以走 <see cref="JsonEncodedText"/>：它是序列化器内部用的同一套编码器，
/// 无反射、无动态代码，输出与改造前逐字符一致。
/// 纯静态无状态，public 只为跨程序集共用（闭源那半与开源那半都调它），共享 <c>WinDouYin.Core</c>。
/// </summary>
public static class JsonText
{
    /// <summary>返回值已含首尾双引号，可直接拼进 JSON 文本或 JS 源码。</summary>
    public static string Quote(string? value) => "\"" + JsonEncodedText.Encode(value ?? string.Empty) + "\"";
}

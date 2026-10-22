using System.Security.Cryptography;
using System.Text;

namespace WinDouYin.Service;

/// <summary>
/// 本机搜索历史：一行一条，新的在前，最多留 20 条，按抖音 uid 分本落
/// <c>%LOCALAPPDATA%\WinDouYin\search_history.&lt;uid哈希&gt;.txt</c>。
///
/// 官方那本记在浏览器 localStorage 里、按 uid 分账；我们既然也做多账号，就不再让 A 号的词
/// 出现在 B 号的建议面板里。文件仍是明文——本地数据只挡"随手改"，不当可信输入，不值得为搜索词上 DPAPI。
/// 退出/删除当前账号不会抹掉那本历史；真要清空就在对应账号下点"清空"。
/// </summary>
internal static class SearchHistory
{
    private const int Keep = 20;

    private static readonly string FolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDouYin");

    private static readonly string LegacyFilePath = Path.Combine(FolderPath, "search_history.txt");

    /// <summary>读当前账号的历史（去重、按行序即时间序，新的在前）。没登录就用 <see cref="AnonymousKey"/> 那本。</summary>
    public static List<string> Load(string uid)
    {
        var words = new List<string>();
        try
        {
            string path = FilePathFor(uid);
            if (!File.Exists(path) && !string.Equals(uid, string.Empty, StringComparison.Ordinal))
            {
                path = LegacyFilePath;
            }

            if (!File.Exists(path))
            {
                return words;
            }

            foreach (string line in File.ReadAllLines(path))
            {
                string word = line.Trim();
                if (word.Length == 0 || words.Contains(word))
                {
                    continue;
                }

                words.Add(word);
                if (words.Count >= Keep)
                {
                    break;
                }
            }
        }
        catch (IOException)
        {
            /* 读不到就当没历史，不拖搜索框 */
        }

        return words;
    }

    /// <summary>记一条：同一个词只留最新那一份，滚出 20 条之外的丢掉。</summary>
    public static void Append(string uid, string keyword)
    {
        string word = keyword.Trim();
        if (word.Length == 0)
        {
            return;
        }

        List<string> words = Load(uid);
        words.Remove(word);
        words.Insert(0, word);
        if (words.Count > Keep)
        {
            words.RemoveRange(Keep, words.Count - Keep);
        }

        Save(uid, words);
    }

    public static void Clear(string uid) => Save(uid, []);

    private static void Save(string uid, List<string> words)
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            File.WriteAllLines(FilePathFor(uid), words);
        }
        catch (IOException)
        {
            /* 记不上就不记，别拖搜索 */
        }
    }

    private static string FilePathFor(string uid)
    {
        string key = uid.Length == 0
            ? "anonymous"
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uid)))[..16];
        return Path.Combine(FolderPath, "search_history." + key + ".txt");
    }
}

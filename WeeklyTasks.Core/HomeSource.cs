using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WeeklyTasks.Core;

public static partial class HomeSource
{
    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.+)$");
    private static readonly Regex Check = new(@"^(\s*)[-*+]\s+\[([ xX])\]\s+(.*)$");
    private static readonly Regex PrivateSection = new(@"FUJITSU-RESTRICTED|社外共有不可|社外秘|外部送信禁止", RegexOptions.IgnoreCase);

    public static SourceDocument Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("ホームが大きすぎます。4MB以下のMarkdownを指定してください。");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return Parse(reader.ReadToEnd(), path);
    }

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static SourceDocument Parse(string text, string path = "ホーム.md")
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new List<SourceItem>();
        var headings = new SortedDictionary<int, string>();
        bool skipCheckedBlock = false, inFence = false;
        int blockedLevel = 0, excludedSections = 0, blockStart = -1;
        string blockHeading = "";
        var block = new StringBuilder();

        string CurrentHeading() => string.Join(" / ", headings.Values);
        void Flush()
        {
            if (blockStart >= 0 && block.Length > 0)
            {
                string raw = block.ToString().Trim();
                if (!PrivateSection.IsMatch(raw)) Add(blockStart + 1, blockHeading, raw);
                else excludedSections++;
            }
            block.Clear(); blockStart = -1;
        }
        void Add(int line, string heading, string content)
        {
            content = Redact(content);
            if (content.Length > 4500) content = content[..4500] + "\n[補足は長いため省略。本文の未確認事項を確定扱いにしないこと]";
            result.Add(new SourceItem($"L{line}", line, Redact(heading), content));
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { Flush(); inFence = !inFence; continue; }
            if (inFence) continue;
            var h = Heading.Match(line);
            if (h.Success)
            {
                Flush(); skipCheckedBlock = false;
                int level = h.Groups[1].Length;
                foreach (int key in headings.Keys.Where(k => k >= level).ToArray()) headings.Remove(key);
                headings[level] = h.Groups[2].Value;
                if (blockedLevel > 0 && level <= blockedLevel) blockedLevel = 0;
                continue;
            }
            if (PrivateSection.IsMatch(line))
            {
                block.Clear(); blockStart = -1;
                if (blockedLevel == 0) { excludedSections++; blockedLevel = headings.Count > 0 ? headings.Keys.Max() : 1; }
                continue;
            }
            if (blockedLevel > 0) continue;
            var c = Check.Match(line);
            if (c.Success && c.Groups[1].Length == 0)
            {
                Flush(); skipCheckedBlock = c.Groups[2].Value != " ";
                if (!skipCheckedBlock) { blockStart = i; blockHeading = CurrentHeading(); block.AppendLine(line); }
                continue;
            }
            if (c.Success && skipCheckedBlock) continue;
            if (blockStart >= 0)
            {
                if (line.StartsWith("|", StringComparison.Ordinal) || line.Trim() == "---") Flush();
                else { block.AppendLine(line); continue; }
            }
            string section = CurrentHeading();
            if (line.StartsWith('|') && (section.Contains("締切") || section.Contains("予定") || section.Contains("要対応メール")))
            {
                var cells = line.Split('|').Select(v => v.Trim()).Where(v => v.Length > 0).ToArray();
                if (cells.Length < 2 || cells.All(v => Regex.IsMatch(v, @"^:?-+:?$")) ||
                    cells[0] is "期限" or "日" or "日付" or "受信日" || cells[0].Contains("~~")) continue;
                Add(i + 1, section, line);
            }
        }
        Flush();
        return new SourceDocument(path, Hash(text), result, excludedSections);
    }

    public static string Redact(string text)
    {
        text = Regex.Replace(text, @"https?://[^\s)）>]+", "[URL省略]", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", "[メールアドレス省略]");
        text = Regex.Replace(text, @"(?<!\d)(?:0\d{1,4}[-－]\d{1,4}[-－]\d{3,4}|\d{12,19})(?!\d)", "[番号省略]");
        text = Regex.Replace(text, @"(?i)(パスワード|ログインID|予約番号|口座番号|普通預金|普通)[：:=＝\s]+[A-Za-z0-9＿_\-]{4,}", "$1 [省略]");
        return text;
    }
}

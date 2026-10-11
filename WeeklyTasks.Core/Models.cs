using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeeklyTasks.Core;

public sealed record SourceItem(string Id, int Line, string Heading, string Text);
public sealed record SourceDocument(string Path, string Hash, List<SourceItem> Items, int ExcludedSections);

public sealed class PlanItem
{
    public string Title { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? Deadline { get; set; }
    public string Kind { get; set; } = "task";
    public string Confidence { get; set; } = "suggested";
    public string Priority { get; set; } = "normal";
    public List<string> SourceIds { get; set; } = [];
}

public sealed class DayPlan
{
    public string Date { get; set; } = "";
    public List<PlanItem> Items { get; set; } = [];
}

public sealed class AiPlan
{
    public List<DayPlan> Days { get; set; } = [];
    public List<PlanItem> Review { get; set; } = [];
}

public sealed class SavedPlan
{
    public int Version { get; set; } = 1;
    public DateTimeOffset GeneratedAt { get; set; }
    public string SourcePath { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string Model { get; set; } = "";
    public List<SourceItem> Sources { get; set; } = [];
    public AiPlan Plan { get; set; } = new();
}

public sealed class WidgetSettings
{
    public string SourcePath { get; set; } = DefaultSourcePath;
    public bool AlwaysOnTop { get; set; } = true;
    public bool DesktopMode { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 390;
    public double Height { get; set; } = 600;
    public static string DefaultSourcePath
    {
        get
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (string drive in Directory.EnumerateDirectories(profile, "OneDrive*"))
            {
                foreach (string root in new[] { drive }.Concat(Directory.EnumerateDirectories(drive)))
                {
                    string home = Path.Combine(root, "2nd_brain", "ホーム.md");
                    if (File.Exists(home)) return home;
                }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "2nd_brain", "ホーム.md");
        }
    }
}

public static class Dates
{
    public static DateTimeOffset Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Tokyo Standard Time" : "Asia/Tokyo"));
    public static DateOnly Today => DateOnly.FromDateTime(Now.DateTime);
    public static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

public static class JsonData
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

public static class PlanValidation
{
    public static void Validate(AiPlan plan, SourceDocument source, DateOnly today)
    {
        var dates = Enumerable.Range(0, 7).Select(i => Dates.Iso(today.AddDays(i))).ToHashSet();
        if (plan.Days is null || plan.Days.Count != 7 || plan.Days.Any(d => d is null || !dates.Remove(d.Date)) || dates.Count != 0)
            throw new InvalidDataException("AIの応答が正しい7日分になっていません。前の一覧を保持しました。");
        if (plan.Review is null || plan.Review.Count > 8)
            throw new InvalidDataException("要確認リストの形式が正しくありません。");
        var ids = source.Items.Select(s => s.Id).ToHashSet();
        foreach (var day in plan.Days)
            if (day.Items is null || day.Items.Count > 25) throw new InvalidDataException("日付別リストの形式が正しくありません。");
        foreach (var item in plan.Days.SelectMany(d => d.Items).Concat(plan.Review))
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > 160 ||
                string.IsNullOrWhiteSpace(item.Reason) || item.Reason.Length > 350 ||
                item.SourceIds is null || item.SourceIds.Count == 0 || item.SourceIds.Any(s => !ids.Contains(s)) ||
                item.Kind is not ("task" or "appointment" or "confirm") ||
                item.Confidence is not ("confirmed" or "suggested" or "uncertain") ||
                item.Priority is not ("high" or "normal") || (item.Deadline?.Length ?? 0) > 100)
                throw new InvalidDataException("AIの応答に根拠のない項目や不正な内容が含まれています。前の一覧を保持しました。");
        }
        plan.Days = plan.Days.OrderBy(d => d.Date, StringComparer.Ordinal).ToList();
    }
}

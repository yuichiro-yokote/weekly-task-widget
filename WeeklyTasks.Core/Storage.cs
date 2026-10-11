using System.Text.Json;

namespace WeeklyTasks.Core;

public sealed class Storage
{
    public string DirectoryPath { get; }
    public string CachePath => Path.Combine(DirectoryPath, "week.json");
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public string WorkerPath => Path.Combine(DirectoryPath, "worker");
    public Storage(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yokote", "WeeklyTasks");
        Directory.CreateDirectory(DirectoryPath);
    }
    public WidgetSettings LoadSettings() => Read<WidgetSettings>(SettingsPath) ?? new();
    public SavedPlan? LoadPlan()
    {
        var saved = Read<SavedPlan>(CachePath);
        if (saved is null) return null;
        try
        {
            if (saved.Version != 1 || saved.Plan is null || saved.Sources is null || saved.Plan.Days is null ||
                saved.Sources.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id)) || saved.Plan.Days.Count != 7 ||
                saved.Plan.Days[0] is null || !DateOnly.TryParseExact(saved.Plan.Days[0].Date, "yyyy-MM-dd", out var first)) return null;
            PlanValidation.Validate(saved.Plan, new SourceDocument(saved.SourcePath, saved.SourceHash, saved.Sources, 0), first);
            return saved;
        }
        catch (InvalidDataException) { return null; }
    }
    public void SaveSettings(WidgetSettings settings) => Write(SettingsPath, settings);
    public void SavePlan(SavedPlan plan) => Write(CachePath, plan);
    private static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonData.Options); }
        catch (JsonException) { return default; }
        catch (IOException) { return default; }
    }
    private static void Write<T>(string path, T value)
    {
        string tmp = path + ".new";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonData.Options), new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, true);
    }
}

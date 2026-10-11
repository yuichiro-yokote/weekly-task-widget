namespace WeeklyTasks.Core;

public static class CalendarMonth
{
    // Six Sunday-first weeks keep the layout stable while moving between months.
    public static IReadOnlyList<DateOnly> GridDates(DateOnly month)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var start = first.AddDays(-(int)first.DayOfWeek);
        return Enumerable.Range(0, 42).Select(i => start.AddDays(i)).ToArray();
    }
}

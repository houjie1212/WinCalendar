using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ical.Net;
using Ical.Net.CalendarComponents;

namespace WinCalendar;

public sealed record AgendaEvent(string Id, string Title, string Location, string Description, DateTime Start, DateTime End, bool AllDay, string SourceId, string Color, string? HolidayName = null, DateTime? SourceStart = null, DateTime? SourceEnd = null, bool? IsOffDay = null)
{
    public bool HolidayOn(DateTime day) => HolidayName != null && SourceStart <= day.Date && SourceEnd > day.Date;
    public bool On(DateTime day) => Start < day.Date.AddDays(1) && (End > day.Date || End == Start && Start.Date == day.Date);
}
// 日期格只汇总明确标记的订阅，冲突不猜测优先级。
public sealed record HolidayDisplay(string[] Names, bool? IsOffDay, bool Conflict);
public static class IcsParser
{
    private static readonly Regex HolidayTitle = new(@"\A(?<name>\S(?:[^\r\n]*?\S)?)[ \t]+(?<kind>假期|补班)[ \t]+第(?<day>[1-9][0-9]*)天/共(?<total>[1-9][0-9]*)天\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    // 真正解析在子进程执行；父进程超时终止，防止恶意 RRULE 占满 UI 线程。
    public static List<AgendaEvent> Parse(string text, DateTime from, DateTime to, string source, string color, SubscriptionKind kind = SubscriptionKind.Ordinary)
    {
        if (text.Length > 5 * 1024 * 1024 || !text.TrimStart().StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase) || !text.Contains("END:VCALENDAR", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        if (to <= from || (to - from).TotalDays > 100) throw new ArgumentOutOfRangeException();
        var calendar = Ical.Net.Calendar.Load(text) ?? throw new InvalidDataException();
        if (calendar.Events.Count > 10000) throw new InvalidDataException();
        var events = new List<AgendaEvent>();
        // 查询边界预留两天覆盖全球时区差，最终再按本地时间过滤，避免月初漏掉 UTC 前一天的事件。
        foreach (var occurrence in calendar.GetOccurrences(from.AddDays(-2), to.AddDays(2)))
        {
            if (events.Count >= 10000) throw new InvalidDataException();
            if (occurrence.Source is not CalendarEvent ev || string.Equals(ev.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
            var p = occurrence.Period;
            bool allDay = !p.StartTime.HasTime;
            // AsSystemLocal 在 Ical.Net 4.x 会保留原墙上时间；显式经 UTC 转成本地时间。
            DateTime Local(Ical.Net.DataTypes.IDateTime value) => string.IsNullOrEmpty(value.TzId) ? DateTime.SpecifyKind(value.Value, DateTimeKind.Local) : DateTime.SpecifyKind(value.AsUtc, DateTimeKind.Utc).ToLocalTime();
            var start = allDay ? p.StartTime.Value.Date : Local(p.StartTime);
            var end = p.EndTime == null ? start : allDay ? p.EndTime.Value.Date : Local(p.EndTime);
            if (end < start) throw new InvalidDataException();
            if (allDay && end == start) end = start.AddDays(1);
            string? holidayName = null;
            bool? off = null;
            DateTime? sourceStart = null, sourceEnd = null;
            if (kind == SubscriptionKind.ChinaHolidays)
            {
                var match = HolidayTitle.Match(ev.Summary ?? "");
                if (match.Success && int.TryParse(match.Groups["day"].Value, out int day) && int.TryParse(match.Groups["total"].Value, out int total) && day <= total)
                {
                    holidayName = match.Groups["name"].Value;
                    off = match.Groups["kind"].Value == "假期";
                    // 保留源墙上日期，不让系统时区移动放假／补班标记。
                    sourceStart = p.StartTime.Value.Date;
                    var rawEnd = p.EndTime?.Value ?? p.StartTime.Value;
                    sourceEnd = rawEnd.Date;
                    if (rawEnd.TimeOfDay != TimeSpan.Zero || sourceEnd <= sourceStart) sourceEnd = rawEnd.Date.AddDays(1);
                }
            }
            var item = new AgendaEvent((ev.Uid ?? "") + "/" + start.ToString("O"), ev.Summary ?? "", ev.Location ?? "", ev.Description ?? "", start, end, allDay, source, color, holidayName, sourceStart, sourceEnd, off);
            if ((item.Start < to && (item.End > from || item.End == item.Start && item.Start >= from)) || (item.SourceStart < to && item.SourceEnd > from)) events.Add(item);
        }
        return events.DistinctBy(x => x.Id).OrderBy(x => x.Start).ToList();
    }
    public static int Worker(string[] args)
    {
        try
        {
            var items = Parse(File.ReadAllText(args[1]), DateTime.ParseExact(args[3], "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTime.ParseExact(args[4], "yyyy-MM-dd", CultureInfo.InvariantCulture), args[5], args[6], args.Length > 7 && args[7] == "ChinaHolidays" ? SubscriptionKind.ChinaHolidays : SubscriptionKind.Ordinary);
            Store.Write(args[2], JsonSerializer.Serialize(items, Store.Json));
            return 0;
        }
        catch { return 2; }
    }
    public static async Task<List<AgendaEvent>> Isolated(string input, DateTime from, DateTime to, Subscription source, CancellationToken ct = default)
    {
        var output = Store.PathFor("parse-" + Guid.NewGuid().ToString("N") + ".json");
        var executable = Environment.ProcessPath!;
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var value in new[] { "--parse-ics", input, output, from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"), source.Id, source.Color, source.Kind.ToString() }) info.ArgumentList.Add(value);
        using var process = Process.Start(info) ?? throw new IOException();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var job = ParseJob.Limit(process);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) throw new InvalidDataException();
            return JsonSerializer.Deserialize<List<AgendaEvent>>(await File.ReadAllTextAsync(output, ct), Store.Json) ?? throw new InvalidDataException();
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            if (File.Exists(output)) File.Delete(output);
        }
    }
}
public sealed class Subscriptions
{
    private readonly Settings settings;
    private readonly SemaphoreSlim gate = new(1);
    public List<AgendaEvent> Events { get; private set; } = new();
    public bool Busy { get; private set; }
    public Subscriptions(Settings settings) { this.settings = settings; }
    public IEnumerable<AgendaEvent> ForDay(DateTime date) => Events.Where(x => settings.Sources.Any(s => s.Enabled && s.Id == x.SourceId && (x.On(date) || s.Kind == SubscriptionKind.ChinaHolidays && x.HolidayOn(date)))).OrderByDescending(x => x.AllDay).ThenBy(x => x.Start);
    // 按配置顺序返回当天来源，每个订阅仅出现一次。
    public Subscription[] SourcesForDay(DateTime date)
    {
        var ids = ForDay(date).Select(e => e.SourceId).ToHashSet();
        return settings.Sources.Where(s => s.Enabled && ids.Contains(s.Id)).ToArray();
    }
    public HolidayDisplay HolidayForDay(DateTime date)
    {
        var items = settings.Sources.Where(s => s.Enabled && s.Kind == SubscriptionKind.ChinaHolidays)
            .SelectMany(s => Events.Where(e => e.SourceId == s.Id && e.HolidayOn(date))).ToArray();
        var names = items.Select(e => e.HolidayName!).Distinct().ToArray();
        var states = items.Where(e => e.IsOffDay.HasValue).Select(e => e.IsOffDay!.Value).Distinct().ToArray();
        return new(names, states.Length == 1 ? states[0] : null, states.Length > 1);
    }
    public async Task Refresh(DateTime from, DateTime to, bool network)
    {
        await gate.WaitAsync();
        Busy = true;
        try
        {
            var result = new List<AgendaEvent>();
            foreach (var source in settings.Sources.Where(x => x.Enabled).ToArray())
            {
                string cache = Store.PathFor(source.Id + ".ics"), pending = Store.PathFor(source.Id + ".pending");
                List<AgendaEvent>? parsed = null;
                if (network)
                {
                    try
                    {
                        var text = await Download.Text(Download.Validate(source.Url));
                        Store.Write(pending, text);
                        parsed = await IcsParser.Isolated(pending, from, to, source);
                        File.Move(pending, cache, true);
                        source.LastSuccess = DateTimeOffset.Now;
                        source.Failed = false;
                    }
                    catch { source.Failed = true; }
                    finally { if (File.Exists(pending)) File.Delete(pending); }
                }
                if (parsed == null && File.Exists(cache))
                    try { parsed = await IcsParser.Isolated(cache, from, to, source); }
                    catch { source.Failed = true; }
                result.AddRange(parsed ?? Events.Where(x => x.SourceId == source.Id));
            }
            Events = result;
            if (network) Store.Save(settings);
        }
        finally { Busy = false; gate.Release(); }
    }
}

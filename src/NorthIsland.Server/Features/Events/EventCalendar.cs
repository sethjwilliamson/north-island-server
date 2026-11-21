using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.GameData.Events;
using NorthIsland.Protocol.Messages.Player;

namespace NorthIsland.Server.Features.Events;

/// <summary>
/// The seasonal event calendar and the weekly activity hours, read from the calendar file. It tells which events are on, their experience and
/// drop bonuses, the date the date conditions of listed map scripts compare with, and whether an activity is open.
/// </summary>
public sealed class EventCalendar
{
    /// <summary>Activity name of the quiz in <see cref="ActivityHours.Name"/>.</summary>
    public const string ActivityQuiz = "quiz";

    /// <summary>Activity name of the compass hunt in <see cref="ActivityHours.Name"/>.</summary>
    public const string ActivityCompass = "compass";

    /// <summary>Activity name of the treasure hunt in <see cref="ActivityHours.Name"/>.</summary>
    public const string ActivityTreasure = "treasure";

    /// <summary>Activity name of the challenge in <see cref="ActivityHours.Name"/>.</summary>
    public const string ActivityChallenge = "challenge";

    /// <summary>How the calendar file is read: names without regard to case, comments skipped, trailing commas allowed, enums as text.</summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>When true every activity is open whatever the weekly hours say.</summary>
    private readonly bool _activitiesAlwaysOpen;

    /// <summary>The calendar event and the date window of each map event that follows a calendar event, by map id and event index.</summary>
    private readonly Dictionary<(int Map, int Event), (CalendarEvent Owner, ScriptWindow? Window)> _scripts = [];

    /// <summary>The source of the current local time. Replaceable by tests.</summary>
    public Func<DateTime> Clock { get; set; } = () => GameClock.Now;

    /// <summary>The calendar file that was loaded.</summary>
    public CalendarFile File { get; }

    /// <summary>Whether every activity is open whatever the weekly hours say.</summary>
    public bool ActivitiesAlwaysOpen => _activitiesAlwaysOpen;

    /// <summary>Loads the calendar file named in the server options and sets up the New Year event name.</summary>
    /// <param name="options">The server options, which name the calendar file and the data folder.</param>
    /// <param name="activityOptions">The activity options, which can leave every activity open.</param>
    /// <param name="eventOptions">The event options, which name the New Year event.</param>
    /// <param name="maps">The map data, for the scenes of the listed map scripts.</param>
    /// <param name="logger">The log.</param>
    public EventCalendar(
        IOptions<ServerOptions> options,
        IOptions<ActivityOptions> activityOptions,
        IOptions<EventOptions> eventOptions,
        MapData maps,
        ILogger<EventCalendar> logger)
        : this(Load(options.Value, logger), activityOptions.Value.ActivitiesAlwaysOpen, id => maps.Scene(id))
    {
        NewYearName = eventOptions.Value.NewYear.EventName;
    }

    /// <summary>The name of the calendar event that counts as the New Year event; empty for none.</summary>
    public string NewYearName { get; set; } = "New Year";

    /// <summary>Tells whether the New Year event is on now.</summary>
    /// <returns>True when a calendar event of that name is on.</returns>
    public bool NewYearOn() => NewYearName.Length > 0 && Active().Any(e => e.Name == NewYearName);

    /// <summary>Builds a calendar from a parsed file and works out the date window of every map script it lists.</summary>
    /// <param name="file">The calendar file.</param>
    /// <param name="activitiesAlwaysOpen">True to leave every activity open whatever the weekly hours say.</param>
    /// <param name="scenes">Finds the scene of a map id, null when the map is unknown.</param>
    public EventCalendar(
        CalendarFile file,
        bool activitiesAlwaysOpen,
        Func<int, EveScene?> scenes)
    {
        File = file;
        _activitiesAlwaysOpen = activitiesAlwaysOpen;
        foreach (var ev in file.Events)
            foreach (var script in ev.Scripts)
            {
                var window = ScriptWindow.Of(scenes(script.Map), script.Events);
                foreach (var index in script.Events)
                    _scripts.TryAdd((script.Map, index), (ev, window));
            }
    }

    /// <summary>
    /// Reads the calendar file named in the server options, from the data folder or else from the settings folder next to the server, absolute
    /// names as they are.
    /// A missing or unreadable file gives an empty calendar.
    /// </summary>
    /// <param name="options">The server options.</param>
    /// <param name="logger">The log.</param>
    /// <returns>The calendar file.</returns>
    public static CalendarFile Load(ServerOptions options, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(options.EventCalendarFile))
            return new CalendarFile();
        string[] candidates = Path.IsPathRooted(options.EventCalendarFile)
            ? [options.EventCalendarFile]
            : new[]
            {
                Path.Combine(ServerPaths.Resolve(options.DataDirectory), options.EventCalendarFile),
                Path.Combine(AppContext.BaseDirectory, SettingsFiles.Folder, options.EventCalendarFile)
            };
        foreach (var path in candidates.Where(System.IO.File.Exists))
        {
            try
            {
                var file = JsonSerializer.Deserialize<CalendarFile>(System.IO.File.ReadAllText(path), Json) ?? new CalendarFile();
                logger.LogInformation(
                    "Event calendar {Path}: {Events} event(s), {Activities} activity hour(s)",
                    path,
                    file.Events.Count,
                    file.Activities.Count);
                return file;
            }
            catch (Exception ex) when (ex is IOException or JsonException or FormatException)
            {
                logger.LogWarning("Event calendar {Path} not read ({Error}); no seasonal events", path, ex.Message);
                return new CalendarFile();
            }
        }
        logger.LogInformation("No event calendar ({File}); no seasonal events", options.EventCalendarFile);
        return new CalendarFile();
    }

    /// <summary>Reads a calendar file from JSON text.</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The calendar file.</returns>
    public static CalendarFile Parse(string json) => JsonSerializer.Deserialize<CalendarFile>(json, Json) ?? new CalendarFile();

    /// <summary>Lists the events that are on at a given time.</summary>
    /// <param name="now">The local time to test.</param>
    /// <returns>The events that are on.</returns>
    public IReadOnlyList<CalendarEvent> Active(DateTime now) => [.. File.Events.Where(e => WindowStart(e, now) is not null)];

    /// <summary>Lists the events that are on now.</summary>
    /// <returns>The events that are on.</returns>
    public IReadOnlyList<CalendarEvent> Active() => Active(Clock());

    /// <summary>Works out the battle experience factor of the events that are on.</summary>
    /// <returns>The highest factor above 0 among them; 1 when none is on.</returns>
    public double ExpMultiplier() => Active().Select(e => e.ExpMultiplier).Where(m => m > 0).DefaultIfEmpty(1).Max();

    /// <summary>Collects the drop table rows of the events that are on.</summary>
    /// <returns>The rows of all of them.</returns>
    public IReadOnlyList<CalendarDrop> Drops() => [.. Active().SelectMany(e => e.Drops)];

    /// <summary>Finds when the current run of an event began.</summary>
    /// <param name="ev">The event.</param>
    /// <param name="now">The local time to test.</param>
    /// <returns>The start of the window that holds the time (today for an event that is always on); null when the event is not on.</returns>
    public static DateTime? WindowStart(CalendarEvent ev, DateTime now)
    {
        switch (ev.Mode)
        {
            case CalendarMode.On: // always on: the window starts today
                return now.Date;
            case CalendarMode.Off: // always off: no window
                return null;
        }
        if (ev.Days.Count > 0)
            return ev.Days.Contains(now.DayOfWeek) ? now.Date : null;
        for (var year = now.Year; year >= now.Year - 1; year--)
        {
            if (Window(ev, year) is not var (start, end))
                continue;
            if (now >= start && now < end)
                return start;
        }
        return null;
    }

    /// <summary>
    /// Works out the window of an event for the year it begins in; a row for the year in <see cref="CalendarEvent.Years"/>
    /// replaces the yearly dates.
    /// </summary>
    /// <param name="ev">The event.</param>
    /// <param name="year">The year the window begins in.</param>
    /// <returns>The start and the end (the midnight after the last day); null when the dates are missing or unreadable.</returns>
    public static (DateTime Start, DateTime End)? Window(CalendarEvent ev, int year)
    {
        string? from = ev.Start, to = ev.End;
        if (ev.Years.TryGetValue(year.ToString(CultureInfo.InvariantCulture), out var dates) && dates.Count == 2)
            (from, to) = (dates[0], dates[1]);
        if (MonthDay(from, year) is not { } start || MonthDay(to, year) is not { } end)
            return null;
        if (end < start)
            end = MonthDay(to, year + 1)!.Value;
        return (start, end.AddDays(1));
    }

    /// <summary>Reads a day written as <c>MM-dd</c>; a day past the end of the month counts as its last day.</summary>
    /// <param name="text">The text to read.</param>
    /// <param name="year">The year to put the day in.</param>
    /// <returns>Local midnight of that day; null when the text is empty or unreadable.</returns>
    private static DateTime? MonthDay(string? text, int year)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var parts = text.Split('-');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var day) || month is < 1 or > 12 || day < 1)
            return null;
        return new DateTime(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)), 0, 0, 0, DateTimeKind.Local);
    }

    /// <summary>
    /// Gives the time a map event's date conditions compare with: the real time for an event no calendar event lists, otherwise a clock
    /// that runs from the first scripted date while the calendar event is on and stands just before it while it is off.
    /// </summary>
    /// <param name="mapId">The map id.</param>
    /// <param name="eventIndex">The index of the event on the map.</param>
    /// <returns>The time to compare with.</returns>
    public DateTime ScriptNow(int mapId, int eventIndex)
    {
        var now = Clock();
        if (!_scripts.TryGetValue((mapId, eventIndex), out var entry) || entry.Window is not { } w)
            return now;
        var hold = w.Last > w.First ? w.Last.AddSeconds(-1) : w.First;
        if (entry.Owner.Mode == CalendarMode.On)
            return hold;
        if (WindowStart(entry.Owner, now) is not { } start)
            return w.First.AddSeconds(-1);
        var virtualNow = w.First + (now - start);
        return virtualNow < hold ? virtualNow : hold;
    }

    /// <summary>Tells whether a map event's date conditions follow a calendar event.</summary>
    /// <param name="mapId">The map id.</param>
    /// <param name="eventIndex">The index of the event on the map.</param>
    /// <returns>True when a calendar event lists it.</returns>
    public bool Redates(int mapId, int eventIndex) => _scripts.ContainsKey((mapId, eventIndex));

    /// <summary>Tells whether an activity is open now by the weekly hours.</summary>
    /// <param name="activity">The activity name.</param>
    /// <returns>True when activities are always open, the activity has no hours listed, or now falls in one of its windows.</returns>
    public bool ActivityOpen(string activity) => OpeningWindow(activity) is not null;

    /// <summary>
    /// Names the opening window of an activity that is open now. A window of the weekly hours is named by its date and start time; when
    /// activities are always open, or the activity has no hours, the whole local day counts as one window.
    /// </summary>
    /// <param name="activity">The activity name, such as <see cref="ActivityQuiz"/>.</param>
    /// <returns>The window's name; null when the activity is closed now.</returns>
    public string? OpeningWindow(string activity)
    {
        var now = Clock();
        var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_activitiesAlwaysOpen)
            return day;
        var hours = File.Activities.Where(a => string.Equals(a.Name, activity, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hours.Count == 0)
            return day;
        var open = hours.Find(h => h.Day == now.DayOfWeek && TimeOf(h.Start) is { } from && TimeOf(h.End) is { } to
            && now.TimeOfDay >= from && now.TimeOfDay < to);
        return open is null ? null : $"{day} {open.Start}";
    }

    /// <summary>Reads a time of day written as <c>h:mm</c>.</summary>
    /// <param name="text">The text to read.</param>
    /// <returns>The time of day; null when the text is unreadable.</returns>
    private static TimeSpan? TimeOf(string text) =>
        TimeSpan.TryParseExact(text, @"h\:mm", CultureInfo.InvariantCulture, out var t) ? t : null;

    /// <summary>
    /// Sends a player who logs in the start notices of the events that are on, and the New Year notice and Lucky 7 count
    /// while that event is on.
    /// </summary>
    /// <param name="session">The player's session.</param>
    public void SendOnLogin(GameSession session)
    {
        foreach (var ev in Active().Where(e => e.StartNotice != 0))
            session.Send(PlayerMessages.EventNotice(ev.StartNotice));
        if (NewYearOn() && session.ActiveCharacter is { } character)
        {
            session.Send(PlayerMessages.NewYearNotice(true));
            session.Send(PlayerMessages.Lucky7Count(character.NewYearLucky7));
        }
    }

    /// <summary>
    /// Builds the notice packets for the change between two lists of events that are on: end notices of those gone,
    /// then start notices of the new ones.
    /// </summary>
    /// <param name="before">The events that were on.</param>
    /// <param name="after">The events that are on now.</param>
    /// <returns>The packets; events with notice 0 give none.</returns>
    public static List<byte[]> NoticesBetween(IReadOnlyList<CalendarEvent> before, IReadOnlyList<CalendarEvent> after)
    {
        HashSet<string> was = [.. before.Select(e => e.Name)], now = [.. after.Select(e => e.Name)];
        return
            [
                .. before.Where(e => !now.Contains(e.Name) && e.EndNotice != 0).Select(e => PlayerMessages.EventNotice(e.EndNotice)),
                .. after.Where(e => !was.Contains(e.Name) && e.StartNotice != 0).Select(e => PlayerMessages.EventNotice(e.StartNotice)),
            ];
    }
}

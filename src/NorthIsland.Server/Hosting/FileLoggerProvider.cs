using System.Globalization;
using Microsoft.Extensions.Logging;

namespace NorthIsland.Server.Hosting;

/// <summary>
/// Logger provider that appends to one log file per day, <c>server-yyyyMMdd.log</c>, continued in <c>server-yyyyMMdd.2.log</c> and so on when a
/// file reaches its size cap. Once the day's last part is full, the day's oldest part is deleted and the others move down one number (part 2
/// becomes the first part, and so on), and a new last part begins: the day keeps its latest lines, up to the parts per day times the size cap,
/// so that a flood of lines cannot blind the log for the rest of the day. Control characters in a message are escaped, so that text a client
/// sent cannot fake log lines or terminal codes.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>The default size cap of a log file, in megabytes.</summary>
    public const int DefaultFileMegabytes = 100;

    /// <summary>The default number of log files (parts) per day.</summary>
    public const int DefaultFilesPerDay = 10;

    /// <summary>The bytes in a megabyte.</summary>
    private const long BytesPerMegabyte = 1024 * 1024;

    /// <summary>The directory that holds the log files.</summary>
    private readonly string _directory;

    /// <summary>The size in bytes at which a file is closed and the next part begun.</summary>
    private readonly long _maxFileBytes;

    /// <summary>The most parts per day.</summary>
    private readonly int _filesPerDay;

    /// <summary>Serializes writing and the change of the day's file.</summary>
    private readonly Lock _lock = new();

    /// <summary>The writer of the current file; null before the first line and after disposal.</summary>
    private StreamWriter? _writer;

    /// <summary>The bytes in the current file: its length when it was opened, plus the bytes written since.</summary>
    private long _written;

    /// <summary>The day the current file belongs to.</summary>
    private DateOnly _day;

    /// <summary>The part of the day the current file is, from 1.</summary>
    private int _part;

    /// <summary>Creates the provider and the log directory.</summary>
    /// <param name="directory">The directory for the log files.</param>
    /// <param name="fileMegabytes">The size cap of a file in megabytes; 0 or less for the default.</param>
    /// <param name="filesPerDay">The most files per day; 0 or less for the default.</param>
    public FileLoggerProvider(
        string directory,
        int fileMegabytes = DefaultFileMegabytes,
        int filesPerDay = DefaultFilesPerDay)
    {
        _directory = directory;
        _maxFileBytes = (fileMegabytes > 0 ? fileMegabytes : DefaultFileMegabytes) * BytesPerMegabyte;
        _filesPerDay = filesPerDay > 0 ? filesPerDay : DefaultFilesPerDay;
        Directory.CreateDirectory(directory);
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>Writes a timestamped line, and the exception when there is one, to the day's file.</summary>
    /// <param name="category">The logger category.</param>
    /// <param name="level">The log level.</param>
    /// <param name="message">The formatted message; its control characters are escaped.</param>
    /// <param name="exception">The exception to write below the line, or null.</param>
    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var now = DateTime.Now;
        var line = string.Create(CultureInfo.InvariantCulture, $"{now:HH:mm:ss.fff} {LevelTag(level)}: {category}: {LogText.Printable(message)}");
        lock (_lock)
        {
            var writer = WriterFor(DateOnly.FromDateTime(now));
            WriteLine(writer, line);
            if (exception is not null)
                WriteLine(writer, exception.ToString());
        }
    }

    /// <summary>Writes a line to the current file and counts its bytes.</summary>
    /// <param name="writer">The writer of the current file.</param>
    /// <param name="line">The line, without its line break.</param>
    private void WriteLine(StreamWriter writer, string line)
    {
        writer.WriteLine(line);
        _written += writer.Encoding.GetByteCount(line) + writer.Encoding.GetByteCount(writer.NewLine);
    }

    /// <summary>
    /// Gives the writer for a day: opens the day's first part when the day has changed (or after disposal), the next part when the current one
    /// reached its size cap, and once the day's last part is full drops the day's oldest part (<see cref="DropOldestPart"/>) and begins a new last
    /// part, with a line that says so.
    /// </summary>
    /// <param name="day">The day of the line being written.</param>
    /// <returns>The writer.</returns>
    private StreamWriter WriterFor(DateOnly day)
    {
        if (day != _day || _writer is null)
        {
            _writer?.Dispose();
            _day = day;
            _part = 1;
            _writer = Open(day, _part, append: true);
        }
        while (_written >= _maxFileBytes && _part < _filesPerDay)
        {
            _writer.Dispose();
            _writer = Open(day, ++_part, append: true);
        }
        if (_written < _maxFileBytes)
            return _writer;

        _writer.Dispose();
        var dropped = DropOldestPart(day);
        _writer = Open(day, _part, append: false); // a moved part's name is free now; when the move failed, the full last part starts over
        WriteLine(_writer, string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss.fff} warn: {nameof(FileLoggerProvider)}: the day's "
            + $"{_filesPerDay} log file(s) were full; {(dropped ? "the oldest was deleted and the others renumbered" : "the last one starts over")}"));
        return _writer;
    }

    /// <summary>
    /// Deletes the day's first (oldest) part and moves each later part down one number, so that the name of the last part is free. With one part
    /// per day, only deletes it.
    /// </summary>
    /// <param name="day">The day.</param>
    /// <returns>True when the parts were moved; false when a file could not be deleted or moved (another program holds it, for example).</returns>
    private bool DropOldestPart(DateOnly day)
    {
        try
        {
            File.Delete(PathOf(day, 1));
            for (var part = 2; part <= _filesPerDay; part++)
            {
                if (File.Exists(PathOf(day, part)))
                    File.Move(PathOf(day, part), PathOf(day, part - 1));
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Opens a part of a day's log and counts the bytes it already holds.</summary>
    /// <param name="day">The day.</param>
    /// <param name="part">The part, from 1.</param>
    /// <param name="append">True to append to the file; false to empty it first.</param>
    /// <returns>The writer.</returns>
    private StreamWriter Open(DateOnly day, int part, bool append)
    {
        var stream = new FileStream(PathOf(day, part), append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        _written = stream.Length;
        return new StreamWriter(stream) { AutoFlush = true };
    }

    /// <summary>Gives the path of a part of a day's log.</summary>
    /// <param name="day">The day.</param>
    /// <param name="part">The part, from 1; the first part has no number in its name.</param>
    /// <returns>The path.</returns>
    private string PathOf(DateOnly day, int part) => Path.Combine(_directory, part == 1
        ? string.Create(CultureInfo.InvariantCulture, $"server-{day:yyyyMMdd}.log")
        : string.Create(CultureInfo.InvariantCulture, $"server-{day:yyyyMMdd}.{part}.log"));

    /// <summary>Gives the four-letter tag a level has in the log.</summary>
    /// <param name="level">The log level.</param>
    /// <returns>The tag.</returns>
    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce", // trace
        LogLevel.Debug => "dbug", // debug
        LogLevel.Information => "info", // information
        LogLevel.Warning => "warn", // warning
        LogLevel.Error => "fail", // error
        LogLevel.Critical => "crit", // critical
        _ => "none", // any other level
    };

    /// <summary>Logger of one category that hands its lines to the provider.</summary>
    /// <param name="provider">The provider that writes the lines.</param>
    /// <param name="category">The logger category.</param>
    private sealed class FileLogger(
        FileLoggerProvider provider,
        string category) : ILogger
    {
        /// <inheritdoc/>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc/>
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        /// <inheritdoc/>
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}

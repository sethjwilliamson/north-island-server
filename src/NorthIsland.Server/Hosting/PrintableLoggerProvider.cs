using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace NorthIsland.Server.Hosting;

/// <summary>
/// Logger provider that hands every message to another provider with its control characters escaped (<see cref="LogText.Printable"/>), so that
/// text a client sent cannot fake log lines or send terminal codes to the console. It carries the alias of the console provider, so that the
/// <c>Logging:Console</c> settings still apply to the console it wraps.
/// </summary>
/// <param name="inner">The provider that writes the escaped messages.</param>
[ProviderAlias("Console")]
public sealed class PrintableLoggerProvider(
    ILoggerProvider inner) : ILoggerProvider, ISupportExternalScope
{
    /// <summary>Replaces the console provider that <c>AddConsole</c> or <c>AddSimpleConsole</c> registered with one wrapped in this provider.</summary>
    /// <param name="logging">The logging builder, after the console was added.</param>
    public static void WrapConsole(ILoggingBuilder logging)
    {
        var services = logging.Services;
        var console = services.FirstOrDefault(d => d.ServiceType == typeof(ILoggerProvider) && d.ImplementationType == typeof(ConsoleLoggerProvider));
        if (console is null)
            return;
        services.Remove(console);
        services.AddSingleton<ILoggerProvider>(sp => new PrintableLoggerProvider(
            new ConsoleLoggerProvider(sp.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>(), sp.GetServices<ConsoleFormatter>())));
    }

    /// <summary>Creates the wrapped provider's logger of a category, wrapped so that its messages are escaped.</summary>
    /// <param name="categoryName">The logger category.</param>
    /// <returns>The logger.</returns>
    public ILogger CreateLogger(string categoryName) => new PrintableLogger(inner.CreateLogger(categoryName));

    /// <summary>Hands the scope provider on to the wrapped provider when it takes one.</summary>
    /// <param name="scopeProvider">The scope provider.</param>
    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        if (inner is ISupportExternalScope scoped)
            scoped.SetScopeProvider(scopeProvider);
    }

    /// <summary>Disposes the wrapped provider.</summary>
    public void Dispose() => inner.Dispose();

    /// <summary>Logger that escapes each message before the wrapped logger formats it.</summary>
    /// <param name="inner">The wrapped logger.</param>
    private sealed class PrintableLogger(
        ILogger inner) : ILogger
    {
        /// <inheritdoc/>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        /// <inheritdoc/>
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        /// <inheritdoc/>
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, (s, e) => LogText.Printable(formatter(s, e)));
    }
}

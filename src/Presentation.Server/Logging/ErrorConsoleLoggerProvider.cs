using Microsoft.Extensions.Logging;

namespace Presentation.Server.Logging;

/// <summary>
/// The desktop host has no console window, so the default ASP.NET logging is replaced by this
/// provider: it writes only Error/Critical entries to <see cref="Console.Error"/> (a no-op when
/// no console is attached) so a Kestrel bind failure is still visible when run from a terminal.
/// </summary>
public sealed class ErrorConsoleLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ErrorConsoleLogger(categoryName);

    public void Dispose() { }

    private sealed class ErrorConsoleLogger : ILogger
    {
        private readonly string _category;
        public ErrorConsoleLogger(string category) => _category = category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            try
            {
                var line = $"[LoqzenServer {logLevel}] {_category}: {formatter(state, exception)}";
                if (exception is not null) line += Environment.NewLine + exception;
                Console.Error.WriteLine(line);
            }
            catch { /* logging must never throw */ }
        }
    }
}

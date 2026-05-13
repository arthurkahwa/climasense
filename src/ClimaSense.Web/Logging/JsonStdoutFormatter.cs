using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace ClimaSense.Web.Logging;

/// <summary>
/// One JSON object per log line on stdout. Each object always carries
/// <c>ts</c>, <c>level</c>, <c>service</c>, <c>msg</c>, and (when in
/// scope) <c>request_id</c>. This is the .NET-side mirror of the
/// FastAPI tier's <c>python-json-logger</c> dictConfig — both honour
/// the same five required keys per slice 1's acceptance criteria.
/// </summary>
public sealed class JsonStdoutFormatter : ConsoleFormatter
{
    public const string FormatterName = "climasense-json";
    private const string ServiceName = "web";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        SkipValidation = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public JsonStdoutFormatter() : base(FormatterName) { }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null)
        {
            return;
        }

        var holder = new ScopeHolder();
        scopeProvider?.ForEachScope(static (scope, state) =>
        {
            // ASP.NET's BeginScope variants surface both nullable and
            // non-nullable KeyValuePair sequences; accept either.
            switch (scope)
            {
                case IEnumerable<KeyValuePair<string, object>> kvps:
                    foreach (var kvp in kvps)
                    {
                        if (string.Equals(kvp.Key, RequestIdMiddleware.LogScopeKey, StringComparison.Ordinal))
                        {
                            state.RequestId = kvp.Value?.ToString();
                        }
                    }
                    break;
                case IEnumerable<KeyValuePair<string, object?>> kvpsNullable:
                    foreach (var kvp in kvpsNullable)
                    {
                        if (string.Equals(kvp.Key, RequestIdMiddleware.LogScopeKey, StringComparison.Ordinal))
                        {
                            state.RequestId = kvp.Value?.ToString();
                        }
                    }
                    break;
            }
        }, holder);

        var bufferWriter = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(bufferWriter, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("ts", DateTime.UtcNow.ToString("O"));
            writer.WriteString("level", LevelToken(logEntry.LogLevel));
            writer.WriteString("service", ServiceName);
            writer.WriteString("logger", logEntry.Category);
            writer.WriteString("msg", message ?? string.Empty);

            if (logEntry.EventId.Id != 0 || !string.IsNullOrEmpty(logEntry.EventId.Name))
            {
                writer.WriteStartObject("event");
                writer.WriteNumber("id", logEntry.EventId.Id);
                if (!string.IsNullOrEmpty(logEntry.EventId.Name))
                {
                    writer.WriteString("name", logEntry.EventId.Name);
                }
                writer.WriteEndObject();
            }

            if (!string.IsNullOrEmpty(holder.RequestId))
            {
                writer.WriteString("request_id", holder.RequestId);
            }

            if (logEntry.Exception is not null)
            {
                writer.WriteString("exception", logEntry.Exception.ToString());
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        textWriter.Write(System.Text.Encoding.UTF8.GetString(bufferWriter.WrittenSpan));
        textWriter.Write('\n');
    }

    private static string LevelToken(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "error",
        LogLevel.Critical => "critical",
        LogLevel.None => "none",
        _ => "info",
    };

    private sealed class ScopeHolder
    {
        public string? RequestId;
    }
}

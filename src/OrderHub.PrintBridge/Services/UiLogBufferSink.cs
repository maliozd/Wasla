using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace OrderHub.PrintBridge.Services;

public sealed class UiLogBufferSink : ILogEventSink
{
    private static readonly MessageTemplateTextFormatter Formatter = new(
        "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
        formatProvider: null);

    private readonly UiLogBuffer _buffer;

    public UiLogBufferSink(UiLogBuffer buffer) => _buffer = buffer;

    public void Emit(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        Formatter.Format(logEvent, writer);
        _buffer.AppendFormatted(writer.ToString().TrimEnd());
    }
}

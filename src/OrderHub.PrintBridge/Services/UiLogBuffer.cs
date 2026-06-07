namespace OrderHub.PrintBridge.Services;

public sealed class UiLogBuffer
{
    public const int MaxLines = 500;

    private readonly object _sync = new();
    private readonly List<string> _lines = [];

    public event EventHandler? Changed;

    public void AppendFormatted(string formattedLine)
    {
        if (string.IsNullOrWhiteSpace(formattedLine))
            return;

        lock (_sync)
        {
            _lines.Add(formattedLine);
            while (_lines.Count > MaxLines)
                _lines.RemoveAt(0);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string GetText()
    {
        lock (_sync)
            return string.Join(Environment.NewLine, _lines);
    }

    public void Clear()
    {
        lock (_sync)
            _lines.Clear();

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

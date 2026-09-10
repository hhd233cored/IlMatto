using System.Text;

namespace IlMatto.Desktop.Infrastructure;

/// <summary>Preserves event order while combining adjacent plain text deltas.</summary>
internal sealed class ManagerEventBuffer
{
    private List<ManagerHostEvent> _events = new();
    private ManagerHostEvent? _pending;
    private readonly StringBuilder _text = new();
    public int Count => _events.Count + (_pending is null ? 0 : 1);

    public void Add(ManagerHostEvent message)
    {
        // Thinking/tool events have per-event formatting semantics and must
        // remain separate. Never merge across a state/completion boundary.
        if (message.Type == "manager_delta")
        {
            if (_pending is not null && (_pending.SessionId != message.SessionId ||
                _pending.Source != message.Source || _text.Length >= 8192)) FlushText();
            _pending ??= message;
            _text.Append(message.Text);
        }
        else
        {
            FlushText();
            _events.Add(message);
        }
    }

    private void FlushText()
    {
        if (_pending is null) return;
        _events.Add(_pending.WithText(_text.ToString()));
        _pending = null;
        _text.Clear();
    }

    public List<ManagerHostEvent> Drain()
    {
        FlushText();
        var result = _events;
        _events = new();
        if (_text.Capacity > 8192) _text.Capacity = 8192;
        return result;
    }
}

namespace MixLane.Core;

/// <summary>Snapshot-based undo. The project (minus audio data) is tiny, so a JSON copy per edit is cheap and bulletproof.</summary>
public sealed class UndoManager
{
    readonly Stack<(string label, string json)> _undo = new();
    readonly Stack<(string label, string json)> _redo = new();
    const int Max = 200;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Count > 0 ? _undo.Peek().label : null;
    public string? RedoLabel => _redo.Count > 0 ? _redo.Peek().label : null;
    public event Action? Changed;

    /// <summary>Record the state *before* an edit.</summary>
    public void Push(string label, string jsonBefore)
    {
        _undo.Push((label, jsonBefore));
        while (_undo.Count > Max) _undo.TryPop(out _);
        _redo.Clear();
        Changed?.Invoke();
    }

    public string? Undo(string jsonNow)
    {
        if (_undo.Count == 0) return null;
        var (label, json) = _undo.Pop();
        _redo.Push((label, jsonNow));
        Changed?.Invoke();
        return json;
    }

    public string? Redo(string jsonNow)
    {
        if (_redo.Count == 0) return null;
        var (label, json) = _redo.Pop();
        _undo.Push((label, jsonNow));
        Changed?.Invoke();
        return json;
    }

    public void Clear() { _undo.Clear(); _redo.Clear(); Changed?.Invoke(); }
}

using System.ComponentModel;

namespace IlMatto.Desktop.Models;

internal sealed class ActiveTaskRuntimes : IDisposable
{
    private readonly HashSet<TaskRuntimeInfo> _active = new();
    public int Count => _active.Count;

    public void Track(TaskRuntimeInfo runtime)
    {
        if (runtime.IsActive && _active.Add(runtime)) runtime.PropertyChanged += OnRuntimeChanged;
    }

    public void Remove(TaskRuntimeInfo runtime)
    {
        if (_active.Remove(runtime)) runtime.PropertyChanged -= OnRuntimeChanged;
    }

    private void OnRuntimeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is TaskRuntimeInfo runtime && !runtime.IsActive) Remove(runtime);
    }

    public void Refresh()
    {
        // A notification can synchronously finish a task; allow removal while
        // publishing, and allocate only for the few active tasks, never history.
        foreach (var runtime in _active.ToArray())
            if (runtime.IsActive) runtime.RefreshElapsed();
    }

    public void Dispose()
    {
        foreach (var runtime in _active) runtime.PropertyChanged -= OnRuntimeChanged;
        _active.Clear();
    }
}

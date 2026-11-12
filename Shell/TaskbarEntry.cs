namespace LunaDesktop.Shell;

internal readonly record struct TaskbarKey(string? Application, nint Window, uint Process);

internal sealed record TaskbarEntry(TaskbarKey Key, string Label, TaskWindow[] Windows)
{
    internal bool IsGroup => Windows.Length > 1;

    internal static TaskbarEntry[] Build(IReadOnlyList<TaskWindow> windows, TaskGrouping grouping)
    {
        return windows.GroupBy(window => GetKey(window, grouping))
            .Select(group =>
            {
                TaskWindow[] members = group.ToArray();
                TaskWindow first = members[0];
                string label = members.Length == 1 ? first.Title : first.Identity!.Label;
                return new TaskbarEntry(group.Key, label, members);
            })
            .ToArray();
    }

    private static TaskbarKey GetKey(TaskWindow task, TaskGrouping grouping) =>
        grouping == TaskGrouping.Always && task.Identity is { } identity
            ? new TaskbarKey(identity.Key, 0, 0)
            : new TaskbarKey(null, task.Handle, task.Process);
}

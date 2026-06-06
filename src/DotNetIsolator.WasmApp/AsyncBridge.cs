using System.Reflection;

namespace DotNetIsolator.WasmApp;

#pragma warning disable IL2075
// This bridge reflects only over BCL Task<T>/ValueTask<T> members. DotNetIsolator already relies on
// dynamic type serialization and is not trim-safe for arbitrary isolated application code.
public static class AsyncBridge
{
    public static void EnsureInstalled()
        => IsolatorSynchronizationContext.Install();

    public static object? Complete(object? value)
    {
        EnsureInstalled();
        if (value is null)
        {
            return null;
        }

        if (value is Task task)
        {
            return CompleteTask(task);
        }

        if (value is ValueTask valueTask)
        {
            return CompleteTask(valueTask.AsTask());
        }

        var valueType = value.GetType();
        if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = valueType.GetMethod(nameof(ValueTask<int>.AsTask), BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException($"Could not find ValueTask.AsTask on '{valueType.FullName}'.");

            return CompleteTask((Task)asTask.Invoke(value, null)!);
        }

        return value;
    }

    private static object? CompleteTask(Task task)
    {
        IsolatorSynchronizationContext.RunUntilCompleted(task);

        var taskType = FindGenericTaskType(task.GetType());
        if (taskType is null || IsVoidTaskResult(taskType.GetGenericArguments()[0]))
        {
            return null;
        }

        return taskType.GetProperty(nameof(Task<object>.Result))!.GetValue(task);
    }

    private static Type? FindGenericTaskType(Type? type)
    {
        while (type is not null)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                return type;
            }

            type = type.BaseType;
        }

        return null;
    }

    private static bool IsVoidTaskResult(Type type)
        => type.FullName == "System.Threading.Tasks.VoidTaskResult";
}
#pragma warning restore IL2075

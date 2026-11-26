using System.Reflection;
using System.Runtime.CompilerServices;
using SharpFAI.ModApi;

namespace SharpFAI.Editor.Mods;

internal sealed partial class ModScope
{
    internal int RegisterMenuItems(Type[] types, ISharpFaiMod? instance)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
            BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var items = from type in types
                    from method in type.GetMethods(flags)
                    where method.IsStatic || type.IsInstanceOfType(instance)
                    from attribute in method.GetCustomAttributes<MenuItemAttribute>()
                    orderby attribute.Order, attribute.Path
                    select (method, attribute);
        var customMenus = new HashSet<string>(StringComparer.Ordinal);
        int count = 0;
        foreach (var (method, attribute) in items)
        {
            string[] path = attribute.Path?.Split('/', StringSplitOptions.TrimEntries) ?? [];
            if (path.Length == 0 || path.Any(string.IsNullOrEmpty))
                throw new InvalidOperationException($"Invalid [MenuItem] path on '{method.DeclaringType?.FullName}.{method.Name}'.");

            string fullPath = string.Join('/', path);
            string command = $"{Id}/menu-command/{fullPath}";
            ((IEditorCommands)this).Register(new(command, path[^1], attribute.Shortcut, SnapshotChart: attribute.SnapshotChart),
                BindMenuMethod(method, method.IsStatic ? null : instance));
            if (path.Length == 1)
                RegisterMenuBarCommand(new($"{Id}/menu-button/{fullPath}", command, MenuBarSlot.Extensions, UiHost.Editor, attribute.Order));
            else
            {
                MenuTarget target;
                if (BuiltInMenu(path[0]) is { } slot)
                    target = MenuTarget.BuiltIn(slot);
                else
                {
                    string menuId = $"{Id}/menu-root/{path[0]}";
                    if (customMenus.Add(menuId))
                        RegisterMenuBarMenu(new(menuId, path[0], MenuBarSlot.Extensions, UiHost.Editor, attribute.Order));
                    target = MenuTarget.ModMenu(menuId);
                }
                RegisterItem(new($"{Id}/menu-item/{fullPath}", target, command, attribute.Order) { Submenu = path[1..^1] });
            }
            count++;
        }
        return count;
    }

    private static MenuSlot? BuiltInMenu(string name) => name.ToUpperInvariant() switch
    {
        "FILE" or "文件" => MenuSlot.File,
        "EDIT" or "编辑" => MenuSlot.Edit,
        "VIEW" or "视图" => MenuSlot.View,
        "CHART" or "谱面" => MenuSlot.Chart,
        "RENDER" or "渲染" => MenuSlot.Render,
        "TOOLS" or "工具" => MenuSlot.Tools,
        "HELP" or "帮助" => MenuSlot.Help,
        _ => null,
    };

    private static ModCommand BindMenuMethod(MethodInfo method, ISharpFaiMod? instance)
    {
        Type result = method.ReturnType;
        Type[] parameters = method.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        if (method.ContainsGenericParameters ||
            result != typeof(void) && result != typeof(Task) && result != typeof(ValueTask) ||
            result == typeof(void) && method.IsDefined(typeof(AsyncStateMachineAttribute)) ||
            parameters.Length > 2 ||
            parameters.Length == 2 && (parameters[0] != typeof(CommandContext) || parameters[1] != typeof(CancellationToken)) ||
            parameters.Length == 1 && parameters[0] != typeof(CommandContext) && parameters[0] != typeof(CancellationToken))
            throw new InvalidOperationException($"Unsupported [MenuItem] signature on '{method.DeclaringType?.FullName}.{method.Name}'.");

        return (context, token) =>
        {
            object?[]? arguments = parameters.Length switch
            {
                0 => null,
                1 when parameters[0] == typeof(CommandContext) => [context],
                1 => [token],
                _ => [context, token],
            };
            return method.Invoke(instance, arguments) switch
            {
                ValueTask pending => pending,
                Task pending => new(pending),
                _ when result == typeof(void) => ValueTask.CompletedTask,
                _ => throw new InvalidOperationException($"[MenuItem] method '{method.Name}' returned a null task."),
            };
        };
    }
}

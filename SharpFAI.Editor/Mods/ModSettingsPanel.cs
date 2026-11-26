using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpFAI.ModApi;

namespace SharpFAI.Editor.Mods;

internal sealed partial class ModSettingsPanel : IModSettingsStore
{
    private const int MaximumFileBytes = 1024 * 1024;
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                                  BindingFlags.DeclaredOnly;
    private readonly IModSettings _mod;
    private readonly IModContext _context;
    private readonly object _target;
    private readonly string _path;
    private readonly object _saveGate = new();
    private readonly Field[] _fields;
    private readonly bool _hasHiddenValues;
    private readonly Dictionary<string, MemberInfo> _members;
    private readonly JsonSerializerOptions _json = new()
    {
        IncludeFields = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private Dictionary<string, JsonElement> _stored = new(StringComparer.Ordinal);

    private sealed class Field(MemberInfo member, string label, Type type, bool readOnly)
    {
        public MemberInfo Member { get; } = member;
        public string Name => Member.Name;
        public string Label { get; } = label;
        public Type Type { get; } = type;
        public bool ReadOnly { get; } = readOnly || member.IsDefined(typeof(ModSettingReadOnlyAttribute));
        public bool Hidden { get; } = member.IsDefined(typeof(ModSettingHiddenAttribute));
        public bool Persist => !ReadOnly && !Member.IsDefined(typeof(ModSettingNoSaveAttribute));
        public bool Number { get; } = IsNumber(type);
        public ModSettingRangeAttribute? Range { get; } = member.GetCustomAttribute<ModSettingRangeAttribute>();
        public ModSettingHeaderAttribute? Header { get; } = member.GetCustomAttribute<ModSettingHeaderAttribute>();
        public ModSettingTooltipAttribute? Tooltip { get; } = member.GetCustomAttribute<ModSettingTooltipAttribute>();
        public ModSettingShowIfAttribute? Condition { get; } = member.GetCustomAttribute<ModSettingShowIfAttribute>();
        public ModSettingJsonAttribute? JsonEditor { get; } = member.GetCustomAttribute<ModSettingJsonAttribute>();
        public string[]? Options { get; } = type.IsEnum ? Enum.GetNames(type) : null;
        public int Order { get; } = member.GetCustomAttribute<ModSettingOrderAttribute>()?.Order ?? 0;
    }

    public ModSettingsPanel(IModSettings mod, string path, IModContext context)
    {
        _mod = mod;
        _path = path;
        _context = context;
        _target = mod.Settings ?? throw new InvalidOperationException("Mod Settings cannot be null.");
        if (_target.GetType().IsValueType)
            throw new InvalidOperationException("Mod Settings must be a reference type.");
        _members = EnumerateMembers(_target.GetType()).GroupBy(member => member.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        _fields = _members.Values.Select(Describe).OfType<Field>().OrderBy(field => field.Order).ToArray();
        _hasHiddenValues = _fields.Any(setting => setting.Hidden && setting.Persist);
        foreach (var field in _fields)
        {
            if (field.Range is { } range &&
                (!field.Number || !double.IsFinite(range.Minimum) || !double.IsFinite(range.Maximum) || range.Minimum > range.Maximum))
                throw new InvalidOperationException($"Invalid range for setting '{field.Name}'.");
            if (field.Condition is { } condition && (!_members.TryGetValue(condition.Member, out var controlling) ||
                MemberType(controlling) != typeof(bool)))
                throw new InvalidOperationException($"Setting '{field.Name}' requires a bool member named '{condition.Member}'.");
        }
    }

    public string FilePath => _path;
    internal bool HasVisibleFields => _fields.Any(setting => !setting.Hidden);

    internal void Load()
    {
        if (_fields.Length == 0 || !File.Exists(_path)) return;
        try
        {
            if (new FileInfo(_path).Length > MaximumFileBytes)
                throw new InvalidDataException("The settings file exceeds 1 MiB.");
            _stored = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(_path))
                ?? throw new JsonException("Expected a settings object.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            _context.Log($"Could not read '{_path}': {error.Message}");
            return;
        }

        foreach (var field in _fields)
        {
            if (!field.Persist || !_stored.TryGetValue(field.Name, out var value)) continue;
            try { Set(field.Member, Validate(field, value.Deserialize(field.Type, _json))); }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException
                                               or NotSupportedException or TargetInvocationException or OverflowException)
            {
                _context.Log($"Could not load setting '{field.Name}': {error.GetBaseException().Message}");
            }
        }
    }

    internal void Apply(IReadOnlyDictionary<string, object?> changes)
    {
        lock (_saveGate)
            if (!ApplyChanges(changes)) return;
        try { _mod.OnSettingsChanged(); }
        catch (Exception error) { _context.Log($"Settings callback failed: {error.GetBaseException().Message}"); }
    }

    private bool ApplyChanges(IReadOnlyDictionary<string, object?> changes)
    {
        var edits = _fields.Where(field => !field.ReadOnly && !field.Hidden && changes.ContainsKey(field.Name))
            .Select(field => (Field: field, Before: Get(field.Member), After: Validate(field, changes[field.Name])))
            .Where(edit => !Equals(edit.Before, edit.After)).ToArray();
        if (edits.Length == 0) return false;
        int index = 0;
        try
        {
            for (; index < edits.Length; index++)
                Set(edits[index].Field.Member, edits[index].After);
            if (edits.Any(edit => edit.Field.Persist)) SaveValues();
        }
        catch
        {
            // Restore the active settings if a setter or the disk write fails.
            for (int i = Math.Min(index, edits.Length - 1); i >= 0; i--)
            {
                try { Set(edits[i].Field.Member, edits[i].Before); }
                catch (Exception error) { _context.Log($"Could not restore '{edits[i].Field.Name}': {error.GetBaseException().Message}"); }
            }
            throw;
        }
        return true;
    }

    public void Save()
    {
        lock (_saveGate) SaveValues();
    }

    private void SaveValues()
    {
        var values = new Dictionary<string, JsonElement>(_stored, StringComparer.Ordinal);
        foreach (string name in _members.Keys) values.Remove(name);
        foreach (var field in _fields)
            if (field.Persist) values[field.Name] = JsonSerializer.SerializeToElement(Get(field.Member), field.Type, _json);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(values, _json);
        if (data.Length > MaximumFileBytes) throw new IOException("The settings file exceeds 1 MiB.");
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = OpenSettingsFile(temporary)) file.Write(data);
            File.Move(temporary, _path, overwrite: true);
            _stored = values;
        }
        finally { File.Delete(temporary); }
    }

    private FileStream OpenSettingsFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            return new FileStream(path, options);
        }
        return _hasHiddenValues ? OpenPrivateWindowsFile(path) : new FileStream(path, options);
    }

    [SupportedOSPlatform("windows")]
    private static FileStream OpenPrivateWindowsFile(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Cannot identify the owner of the settings file.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096,
            FileOptions.None, security);
    }

    private object? Validate(Field field, object? value)
    {
        if (value is null && field.Type.IsValueType && Nullable.GetUnderlyingType(field.Type) is null ||
            value is not null && !field.Type.IsInstanceOfType(value))
            throw new ArgumentException($"Invalid value for '{field.Name}'.");
        if (value is float f && !float.IsFinite(f) || value is double d && !double.IsFinite(d))
            throw new ArgumentException($"'{field.Name}' must be finite.");
        if (field.JsonEditor is not null && value is string { Length: > 0 } json)
        {
            using var parsed = JsonDocument.Parse(json);
        }
        if (value is null || field.Range is not { } range) return value;
        if (field.Type == typeof(float) || field.Type == typeof(double))
            return Convert.ChangeType(Math.Clamp(Convert.ToDouble(value, CultureInfo.InvariantCulture), range.Minimum,
                range.Maximum), field.Type, CultureInfo.InvariantCulture);

        decimal min = ToDecimalBound(range.Minimum), max = ToDecimalBound(range.Maximum);
        if (field.Type != typeof(decimal)) { min = decimal.Ceiling(min); max = decimal.Floor(max); }
        if (min > max) throw new ArgumentException($"Range for '{field.Name}' contains no integer.");
        return Convert.ChangeType(Math.Clamp(Convert.ToDecimal(value, CultureInfo.InvariantCulture), min, max),
            field.Type, CultureInfo.InvariantCulture);
    }

    private static decimal ToDecimalBound(double bound) => bound >= (double)decimal.MaxValue ? decimal.MaxValue :
        bound <= (double)decimal.MinValue ? decimal.MinValue : (decimal)bound;

    private object? Get(MemberInfo member) => member is FieldInfo field ? field.GetValue(_target) : ((PropertyInfo)member).GetValue(_target);
    private void Set(MemberInfo member, object? value)
    {
        if (member is FieldInfo field) field.SetValue(_target, value);
        else ((PropertyInfo)member).SetValue(_target, value);
    }

    private static Type MemberType(MemberInfo member) =>
        member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;

    private static Field? Describe(MemberInfo member)
    {
        var attributes = member.GetCustomAttributes<ModSettingAttribute>().ToArray();
        if (attributes.Length == 0 || member.IsDefined(typeof(ModSettingIgnoreAttribute))) return null;
        string label = attributes.Select(attribute => attribute.Label).FirstOrDefault(value => value is not null)
            ?? member.Name.TrimStart('_');
        bool readOnly = member is FieldInfo field ? field.IsInitOnly
            : ((PropertyInfo)member).SetMethod is not { } setter ||
              setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));
        return new(member, label, MemberType(member), readOnly);
    }

    private static IEnumerable<MemberInfo> EnumerateMembers(Type type)
    {
        if (type.BaseType is { } parent)
            foreach (var member in EnumerateMembers(parent)) yield return member;
        foreach (var member in type.GetMembers(InstanceMembers))
            if (member is FieldInfo { IsSpecialName: false } field && !field.IsDefined(typeof(CompilerGeneratedAttribute)) &&
                type.GetEvent(member.Name, InstanceMembers) is null ||
                member is PropertyInfo { GetMethod: not null } property && property.GetIndexParameters().Length == 0)
                yield return member;
    }

    private static bool IsNumber(Type type) => !type.IsEnum &&
        Type.GetTypeCode(type) is >= TypeCode.SByte and <= TypeCode.Decimal;
}

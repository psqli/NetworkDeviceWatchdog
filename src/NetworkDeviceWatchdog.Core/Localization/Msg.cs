namespace NetworkDeviceWatchdog.Localization;

/// <summary>
/// A message that is localized only when it is shown: a string key plus named
/// arguments. Arguments may be plain values, other <see cref="Msg"/> instances
/// or a <see cref="MsgList"/>; the <see cref="Localizer"/> formats them recursively.
/// </summary>
/// <remarks>
/// Named placeholders (<c>{host}</c>, <c>{port}</c>) instead of positional ones let
/// translators reorder words freely, which matters for languages such as Hindi,
/// Arabic or Chinese whose word order differs from English.
/// </remarks>
public sealed class Msg
{
    private static readonly IReadOnlyDictionary<string, object?> NoArgs =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    public Msg(string key, IReadOnlyDictionary<string, object?>? args = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key = key;
        Args = args ?? NoArgs;
    }

    /// <summary>Localization key, e.g. <c>failure.unreachable</c>.</summary>
    public string Key { get; }

    /// <summary>Named arguments substituted into the localized text.</summary>
    public IReadOnlyDictionary<string, object?> Args { get; }

    /// <summary>Creates a message with named arguments.</summary>
    public static Msg Of(string key, params (string Name, object? Value)[] args)
    {
        if (args.Length == 0)
            return new Msg(key);
        var dict = new Dictionary<string, object?>(args.Length, StringComparer.Ordinal);
        foreach (var (name, value) in args)
            dict[name] = value;
        return new Msg(key, dict);
    }

    /// <summary>Debug representation; not localized.</summary>
    public override string ToString() =>
        Args.Count == 0 ? Key : $"{Key}({string.Join(", ", Args.Select(a => $"{a.Key}={a.Value}"))})";
}

/// <summary>A list of values joined with the locale's list separator.</summary>
public sealed class MsgList(IReadOnlyList<object> items)
{
    public IReadOnlyList<object> Items { get; } = items;

    public override string ToString() => string.Join(", ", Items);
}

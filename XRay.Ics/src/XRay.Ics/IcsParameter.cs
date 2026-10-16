namespace XRay.Ics;

/// <summary>A property parameter: a name (upper-cased) and one or more values (quotes removed).</summary>
public sealed class IcsParameter
{
    public IcsParameter(string name, params string[] values)
    {
        Name = (name ?? throw new ArgumentNullException(nameof(name))).ToUpperInvariant();
        Values = new List<string>(values ?? []);
    }

    public string Name { get; }

    public List<string> Values { get; }

    /// <summary>The first value, or an empty string.</summary>
    public string Value => Values.Count > 0 ? Values[0] : string.Empty;

    public override string ToString() => Name + "=" + string.Join(",", Values);
}

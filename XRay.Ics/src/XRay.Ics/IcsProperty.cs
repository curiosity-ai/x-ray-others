using XRay.Ics.Internal;

namespace XRay.Ics;

/// <summary>
/// One content line of a calendar: <c>[group.]NAME;PARAM=value:value</c>. <see cref="Value"/> is the
/// wire form (TEXT escapes still in place, quoted-printable and charset already decoded); use
/// <see cref="TextValue"/> for the unescaped text.
/// </summary>
public sealed class IcsProperty
{
    public IcsProperty(string name, string value = "")
    {
        Name = (name ?? throw new ArgumentNullException(nameof(name))).ToUpperInvariant();
        Value = value ?? string.Empty;
    }

    /// <summary>The vCard-style group prefix (<c>item1</c> in <c>item1.ATTACH</c>), or null.</summary>
    public string? Group { get; set; }

    /// <summary>The property name, upper-cased. X- properties keep their name.</summary>
    public string Name { get; }

    public List<IcsParameter> Parameters { get; } = [];

    /// <summary>The value as written in the file (escapes not removed).</summary>
    public string Value { get; set; }

    /// <summary>The 1-based line number of the property in the input, or 0 when it was created in code.</summary>
    public int LineNumber { get; internal set; }

    /// <summary>The value with TEXT escapes removed (<c>\n</c>, <c>\,</c>, <c>\;</c>, <c>\\</c>).</summary>
    public string TextValue
    {
        get => IcsText.Unescape(Value);
        set => Value = IcsText.Escape(value ?? string.Empty);
    }

    /// <summary>The value split on unescaped commas and unescaped, as for CATEGORIES.</summary>
    public IReadOnlyList<string> TextValues => IcsText.SplitUnescaped(Value, ',');

    /// <summary>The first value of a parameter, or null.</summary>
    public string? GetParameter(string name)
    {
        foreach (var parameter in Parameters)
        {
            if (string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase) && parameter.Values.Count > 0)
            {
                return parameter.Values[0];
            }
        }

        return null;
    }

    public IReadOnlyList<string> GetParameterValues(string name)
    {
        var result = new List<string>();
        foreach (var parameter in Parameters)
        {
            if (string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                result.AddRange(parameter.Values);
            }
        }

        return result;
    }

    /// <summary>Replaces every parameter of that name with one value; null removes them.</summary>
    public void SetParameter(string name, string? value)
    {
        Parameters.RemoveAll(parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase));
        if (value is not null)
        {
            Parameters.Add(new IcsParameter(name, value));
        }
    }

    public IcsProperty Clone()
    {
        var copy = new IcsProperty(Name, Value) { Group = Group, LineNumber = LineNumber };
        foreach (var parameter in Parameters)
        {
            copy.Parameters.Add(new IcsParameter(parameter.Name, [.. parameter.Values]));
        }

        return copy;
    }

    public override string ToString() => (Group is null ? string.Empty : Group + ".") + Name + ":" + Value;
}

namespace XRay.Ics;

/// <summary>
/// The raw tree of a calendar: a component (<c>VCALENDAR</c>, <c>VEVENT</c>, <c>VALARM</c>, ...)
/// with its properties in file order and its child components. Nothing is dropped, so X-
/// properties and unknown components survive a load and a save.
/// </summary>
public sealed class IcsComponent
{
    public IcsComponent(string name)
    {
        Name = (name ?? throw new ArgumentNullException(nameof(name))).ToUpperInvariant();
    }

    public string Name { get; }

    public List<IcsProperty> Properties { get; } = [];

    public List<IcsComponent> Children { get; } = [];

    /// <summary>The first property of that name, or null.</summary>
    public IcsProperty? Get(string name)
    {
        foreach (var property in Properties)
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property;
            }
        }

        return null;
    }

    public IEnumerable<IcsProperty> GetAll(string name)
    {
        foreach (var property in Properties)
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                yield return property;
            }
        }
    }

    /// <summary>The wire value of the first property of that name, or null.</summary>
    public string? GetValue(string name) => Get(name)?.Value;

    /// <summary>Replaces every property of that name with one; a null value removes them.</summary>
    public IcsProperty? Set(string name, string? value)
    {
        Properties.RemoveAll(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));
        if (value is null)
        {
            return null;
        }

        var created = new IcsProperty(name, value);
        Properties.Add(created);
        return created;
    }

    public IEnumerable<IcsComponent> GetChildren(string name)
    {
        foreach (var child in Children)
        {
            if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                yield return child;
            }
        }
    }

    public IcsComponent Clone()
    {
        var copy = new IcsComponent(Name);
        foreach (var property in Properties)
        {
            copy.Properties.Add(property.Clone());
        }

        foreach (var child in Children)
        {
            copy.Children.Add(child.Clone());
        }

        return copy;
    }

    public override string ToString() => Name;
}

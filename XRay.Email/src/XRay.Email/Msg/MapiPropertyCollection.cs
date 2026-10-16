namespace XRay.Email.Msg;

public sealed class MapiPropertyCollection
{
    private readonly Dictionary<(ushort PropertyId, ushort PropertyType), MapiProperty> _properties = [];

    public MapiProperty Set(MapiProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        _properties[(property.PropertyId, property.PropertyType)] = property;
        return property;
    }

    public MapiProperty Add(ushort propertyId, ushort propertyType, object? value, uint flags = MapiMessage.DefaultPropertyFlags)
    {
        return Set(new MapiProperty(propertyId, propertyType, value, flags));
    }

    public MapiProperty? Get(ushort propertyId, ushort? propertyType = null)
    {
        if (propertyType is not null)
        {
            _properties.TryGetValue((propertyId, propertyType.Value), out var exact);
            return exact;
        }

        return _properties
            .Where(item => item.Key.PropertyId == propertyId)
            .OrderBy(item => item.Key.PropertyType)
            .Select(item => item.Value)
            .FirstOrDefault();
    }

    public void Remove(ushort propertyId, ushort? propertyType = null)
    {
        if (propertyType is not null)
        {
            _properties.Remove((propertyId, propertyType.Value));
            return;
        }

        foreach (var key in _properties.Keys.Where(key => key.PropertyId == propertyId).ToArray())
        {
            _properties.Remove(key);
        }
    }

    public IEnumerable<MapiProperty> IterProperties()
    {
        foreach (var property in _properties.OrderBy(item => item.Key.PropertyId).ThenBy(item => item.Key.PropertyType).Select(item => item.Value))
        {
            yield return property;
        }
    }
}

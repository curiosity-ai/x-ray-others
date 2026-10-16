namespace XRay.Email.Cfb;

public sealed class CfbStorage : CfbNode
{
    public CfbStorage(string name)
        : base(name)
    {
    }

    public IList<CfbNode> Children { get; } = [];

    public CfbStorage AddStorage(CfbStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        Children.Add(storage);
        return storage;
    }

    public CfbStream AddStream(CfbStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Children.Add(stream);
        return stream;
    }
}

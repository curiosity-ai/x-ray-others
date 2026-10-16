namespace XRay.Email.Cfb;

public sealed class CfbException : Exception
{
    public CfbException(string message)
        : base(message)
    {
    }
}

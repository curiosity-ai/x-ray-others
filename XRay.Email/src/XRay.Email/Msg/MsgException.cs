namespace XRay.Email.Msg;

public sealed class MsgException : Exception
{
    public MsgException(string message)
        : base(message)
    {
    }
}

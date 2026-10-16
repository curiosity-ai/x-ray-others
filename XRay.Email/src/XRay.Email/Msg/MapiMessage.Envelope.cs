using System.Globalization;

namespace XRay.Email.Msg;

/// <summary>PR_MESSAGE_FLAGS (MS-OXCMSG 2.2.1.6).</summary>
[Flags]
public enum MapiMessageFlags : uint
{
    None = 0,
    Read = 0x00000001,
    Unmodified = 0x00000002,
    Submitted = 0x00000004,

    /// <summary>MSGFLAG_UNSENT: the message has not been sent, which is what makes it a draft.</summary>
    Unsent = 0x00000008,
    HasAttachment = 0x00000010,
    FromMe = 0x00000020,
    Associated = 0x00000040,
    Resend = 0x00000080,
    ReadReceiptPending = 0x00000100,
    NonReadReceiptPending = 0x00000200,
}

/// <summary>PR_IMPORTANCE.</summary>
public enum MapiImportance
{
    Low = 0,
    Normal = 1,
    High = 2,
}

/// <summary>PR_SENSITIVITY.</summary>
public enum MapiSensitivity
{
    None = 0,
    Personal = 1,
    Private = 2,
    CompanyConfidential = 3,
}

public sealed partial class MapiMessage
{
    private const ushort ImportancePropertyId = 0x0017;
    private const ushort SensitivityPropertyId = 0x0036;
    private const ushort ClientSubmitTimePropertyId = 0x0039;
    private const ushort ProviderSubmitTimePropertyId = 0x0048;
    private const ushort CreationTimePropertyId = 0x3007;
    private const ushort LastModificationTimePropertyId = 0x3008;

    /// <summary>
    /// PR_MESSAGE_FLAGS, or null when the message has none (an EML never does). The flags are what
    /// <see cref="IsRead"/> and <see cref="IsDraft"/> read.
    /// </summary>
    public MapiMessageFlags? MessageFlags
    {
        get => GetPropertyInt32((ushort)CommonMessagePropertyId.MessageFlags) is { } value ? (MapiMessageFlags)(uint)value : null;
        set
        {
            if (value is null)
            {
                Properties.Remove((ushort)CommonMessagePropertyId.MessageFlags);
                return;
            }

            SetProperty((ushort)CommonMessagePropertyId.MessageFlags, (ushort)PropertyTypeCode.PtypInteger32, (int)(uint)value.Value);
        }
    }

    /// <summary>
    /// True when MSGFLAG_READ is set. A message loaded from EML carries no flags and reads as unread,
    /// unless its <c>Status</c> header says it was read (the mbox <c>R</c> flag).
    /// </summary>
    public bool IsRead
    {
        get
        {
            if (MessageFlags is { } flags)
            {
                return flags.HasFlag(MapiMessageFlags.Read);
            }

            return GetTransportHeader("Status") is { } status && status.Contains('R', StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// True when MSGFLAG_UNSENT is set (an Outlook draft). For a message without flags, true when the
    /// transport headers carry <c>X-Unsent: 1</c>, which is how Outlook saves a draft as EML.
    /// </summary>
    public bool IsDraft
    {
        get
        {
            if (MessageFlags is { } flags)
            {
                return flags.HasFlag(MapiMessageFlags.Unsent);
            }

            return GetTransportHeader("X-Unsent") is { } unsent && unsent.Trim() == "1";
        }
    }

    /// <summary>PR_IMPORTANCE; null when absent.</summary>
    public MapiImportance? Importance => GetPropertyInt32(ImportancePropertyId) is { } value && Enum.IsDefined(typeof(MapiImportance), value) ? (MapiImportance)value : null;

    /// <summary>PR_SENSITIVITY; null when absent.</summary>
    public MapiSensitivity? Sensitivity => GetPropertyInt32(SensitivityPropertyId) is { } value && Enum.IsDefined(typeof(MapiSensitivity), value) ? (MapiSensitivity)value : null;

    /// <summary>PR_CLIENT_SUBMIT_TIME (UTC): when the sender's client sent the message. This is what Outlook shows as Sent.</summary>
    public DateTime? ClientSubmitTime
    {
        get => GetPropertyDateTime(ClientSubmitTimePropertyId);
        set => SetDateTime(ClientSubmitTimePropertyId, value);
    }

    /// <summary>PR_PROVIDER_SUBMIT_TIME (UTC): when the message transport accepted it.</summary>
    public DateTime? SubmitTime
    {
        get => GetPropertyDateTime(ProviderSubmitTimePropertyId);
        set => SetDateTime(ProviderSubmitTimePropertyId, value);
    }

    /// <summary>PR_CREATION_TIME (UTC).</summary>
    public DateTime? CreationTime => GetPropertyDateTime(CreationTimePropertyId);

    /// <summary>PR_LAST_MODIFICATION_TIME (UTC).</summary>
    public DateTime? LastModificationTime => GetPropertyDateTime(LastModificationTimePropertyId);

    /// <summary>
    /// The best available "sent" instant (UTC): <see cref="ClientSubmitTime"/>, else <see cref="SubmitTime"/>,
    /// else <see cref="MessageDeliveryTime"/> (which is where an EML's <c>Date</c> header is kept).
    /// </summary>
    public DateTime? SentTime => ClientSubmitTime ?? SubmitTime ?? MessageDeliveryTime;

    /// <summary>True when the property exists, in any of its stored types.</summary>
    public bool HasProperty(ushort propertyId) => Properties.Get(propertyId) is not null;

    /// <summary>A string property, whether stored as Unicode or 8-bit text; null when absent.</summary>
    public string? GetPropertyString(ushort propertyId) => GetStringProperty(propertyId);

    /// <summary>An integer property of any integer width, as Int32; null when absent or not an integer.</summary>
    public int? GetPropertyInt32(ushort propertyId) => ToInt32(Properties.Get(propertyId)?.Value);

    /// <summary>A boolean property; null when absent.</summary>
    public bool? GetPropertyBoolean(ushort propertyId) => Properties.Get(propertyId)?.Value switch
    {
        bool value => value,
        int value => value != 0,
        short value => value != 0,
        _ => null,
    };

    /// <summary>A date and time property as UTC; null when absent.</summary>
    public DateTime? GetPropertyDateTime(ushort propertyId) => Properties.Get(propertyId)?.Value as DateTime?;

    /// <summary>A binary property; null when absent. The array is the stored one, not a copy.</summary>
    public byte[]? GetPropertyBytes(ushort propertyId) => Properties.Get(propertyId)?.Value as byte[];

    /// <summary>
    /// A property converted to <typeparamref name="T"/>: the stored value when it already is a
    /// <typeparamref name="T"/>, else a numeric, boolean or string conversion; default when absent or
    /// not convertible.
    /// </summary>
    public T? GetPropertyValue<T>(ushort propertyId, ushort? propertyType = null)
    {
        var value = Properties.Get(propertyId, propertyType)?.Value;
        if (value is null)
        {
            return default;
        }

        if (value is T exact)
        {
            return exact;
        }

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        try
        {
            if (target.IsEnum && ToInt32(value) is { } number)
            {
                return (T)Enum.ToObject(target, number);
            }

            if (value is IConvertible && (target.IsPrimitive || target == typeof(string) || target == typeof(decimal)))
            {
                return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            // fall through to the default
        }

        return default;
    }

    private void SetDateTime(ushort propertyId, DateTime? value)
    {
        if (value is null)
        {
            Properties.Remove(propertyId);
            return;
        }

        SetProperty(propertyId, (ushort)PropertyTypeCode.PtypTime, value.Value);
    }

    private static int? ToInt32(object? value) => value switch
    {
        int number => number,
        short number => number,
        ushort number => number,
        uint number when number <= int.MaxValue => (int)number,
        long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
        byte number => number,
        _ => null,
    };

    /// <summary>The unfolded value of the first transport header with this name, or null.</summary>
    private string? GetTransportHeader(string name)
    {
        if (TransportMessageHeaders is not { Length: > 0 } headers)
        {
            return null;
        }

        foreach (var line in headers.Split('\n'))
        {
            if (line.Length > name.Length && line[name.Length] == ':' && line.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(name.Length + 1)..].Trim();
            }
        }

        return null;
    }
}

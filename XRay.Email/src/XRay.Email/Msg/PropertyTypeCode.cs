namespace XRay.Email.Msg;

/// <summary>MAPI property type codes that appear in property tags and stream names in MSG files.</summary>
public enum PropertyTypeCode : ushort
{
    /// <summary>16-bit signed integer property value type.</summary>
    PtypInteger16 = (ushort)2,
    /// <summary>32-bit signed integer property value type.</summary>
    PtypInteger32 = (ushort)3,
    /// <summary>32-bit floating-point property value type.</summary>
    PtypFloating32 = (ushort)4,
    /// <summary>64-bit floating-point property value type.</summary>
    PtypFloating64 = (ushort)5,
    /// <summary>64-bit signed integer currency property value type with 10,000 units per currency unit.</summary>
    PtypCurrency = (ushort)6,
    /// <summary>Floating-time property value type encoded as an 8-byte double.</summary>
    PtypFloatingTime = (ushort)7,
    /// <summary>32-bit error code property value type.</summary>
    PtypErrorCode = (ushort)10,
    /// <summary>Boolean property value type.</summary>
    PtypBoolean = (ushort)11,
    /// <summary>Object property value type.</summary>
    PtypObject = (ushort)13,
    /// <summary>64-bit signed integer property value type.</summary>
    PtypInteger64 = (ushort)20,
    /// <summary>8-bit string property value type.</summary>
    PtypString8 = (ushort)30,
    /// <summary>Unicode string property value type.</summary>
    PtypString = (ushort)31,
    /// <summary>FILETIME-based date/time property value type.</summary>
    PtypTime = (ushort)64,
    /// <summary>GUID property value type.</summary>
    PtypGuid = (ushort)72,
    /// <summary>Binary property value type.</summary>
    PtypBinary = (ushort)258,
    /// <summary>Multiple-valued 16-bit integer property value type.</summary>
    PtypMultipleInteger16 = (ushort)4098,
    /// <summary>Multiple-valued 32-bit integer property value type.</summary>
    PtypMultipleInteger32 = (ushort)4099,
    /// <summary>Multiple-valued 32-bit floating-point property value type.</summary>
    PtypMultipleFloating32 = (ushort)4100,
    /// <summary>Multiple-valued 64-bit floating-point property value type.</summary>
    PtypMultipleFloating64 = (ushort)4101,
    /// <summary>Multiple-valued currency property value type.</summary>
    PtypMultipleCurrency = (ushort)4102,
    /// <summary>Multiple-valued floating-time property value type.</summary>
    PtypMultipleFloatingTime = (ushort)4103,
    /// <summary>Multiple-valued 64-bit integer property value type.</summary>
    PtypMultipleInteger64 = (ushort)4116,
    /// <summary>Multiple-valued 8-bit string property value type.</summary>
    PtypMultipleString8 = (ushort)4126,
    /// <summary>Multiple-valued Unicode string property value type.</summary>
    PtypMultipleString = (ushort)4127,
    /// <summary>Multiple-valued FILETIME property value type.</summary>
    PtypMultipleTime = (ushort)4160,
    /// <summary>Multiple-valued GUID property value type.</summary>
    PtypMultipleGuid = (ushort)4168,
    /// <summary>Multiple-valued binary property value type.</summary>
    PtypMultipleBinary = (ushort)4354
}

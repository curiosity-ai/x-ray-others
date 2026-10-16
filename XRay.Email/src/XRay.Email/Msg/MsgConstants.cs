namespace XRay.Email.Msg;

public static class MsgConstants
{
    /// <summary>Prefix for attachment object storage names; suffix is 8-digit hexadecimal index.</summary>
    public const string AttachmentStoragePrefix = "__attach_version1.0_#";

    /// <summary>Substorage name under attachment storage that contains an embedded message object.</summary>
    public const string EmbeddedMessageStorageName = "__substg1.0_3701000D";

    /// <summary>Maximum number of attachment object storages in one MSG file.</summary>
    public const uint MaxAttachmentStorages = 2048u;

    /// <summary>Maximum number of recipient object storages in one MSG file.</summary>
    public const uint MaxRecipientStorages = 2048u;

    /// <summary>Required storage name for named property mapping streams.</summary>
    public const string NamedPropertyMappingStorageName = "__nameid_version1.0";

    /// <summary>Property must not be deleted from MSG file.</summary>
    public const uint PROPATTR_MANDATORY = 1u;

    /// <summary>If clear, property must not be read.</summary>
    public const uint PROPATTR_READABLE = 2u;

    /// <summary>If clear, property must not be modified or deleted.</summary>
    public const uint PROPATTR_WRITABLE = 4u;

    /// <summary>Required name for the property stream in all MSG storages except Named Property Mapping storage.</summary>
    public const string PropertyStreamName = "__properties_version1.0";

    /// <summary>Prefix used to form stream names that store values of variable-length and multi-valued properties.</summary>
    public const string PropertyValueStreamPrefix = "__substg1.0_";

    /// <summary>Prefix for recipient object storage names; suffix is 8-digit hexadecimal index.</summary>
    public const string RecipientStoragePrefix = "__recip_version1.0_#";

}

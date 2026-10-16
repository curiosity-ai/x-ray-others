using XRay.Email.Cfb;

namespace XRay.Email.Msg;

internal sealed record StorageLayout(
    IReadOnlyList<DirectoryEntry> RecipientStorages,
    IReadOnlyList<DirectoryEntry> AttachmentStorages,
    DirectoryEntry? NamedPropertyMappingStorage,
    DirectoryEntry TopLevelPropertyStream);

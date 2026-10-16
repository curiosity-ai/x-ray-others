namespace XRay.Ics;

/// <summary>An attendee's ROLE. A missing parameter means <see cref="RequiredParticipant"/>.</summary>
public enum AttendeeRole
{
    RequiredParticipant,
    OptionalParticipant,
    NonParticipant,
    Chair,
}

/// <summary>PARTSTAT, the participation status of an attendee.</summary>
public enum ParticipationStatus
{
    NeedsAction,
    Accepted,
    Declined,
    Tentative,
    Delegated,
    Completed,
    InProcess,
}

/// <summary>CUTYPE, the kind of calendar user.</summary>
public enum CalendarUserType
{
    Individual,
    Group,
    Resource,
    Room,
    Unknown,
}

/// <summary>STATUS of an event, to-do or journal entry.</summary>
public enum CalendarStatus
{
    None,
    Tentative,
    Confirmed,
    Cancelled,
    NeedsAction,
    Completed,
    InProcess,
    Draft,
    Final,
}

/// <summary>CLASS, the access classification.</summary>
public enum CalendarClassification
{
    None,
    Public,
    Private,
    Confidential,
}

/// <summary>TRANSP, whether the event blocks time.</summary>
public enum CalendarTransparency
{
    Opaque,
    Transparent,
}

/// <summary>The iTIP METHOD (RFC 5546) of a calendar object.</summary>
public enum CalendarMethod
{
    None,
    Publish,
    Request,
    Reply,
    Add,
    Cancel,
    Refresh,
    Counter,
    DeclineCounter,
    Other,
}

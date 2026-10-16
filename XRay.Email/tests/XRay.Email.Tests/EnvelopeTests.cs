using System.Text;
using XRay.Email.Msg;

namespace XRay.Email.Tests;

/// <summary>Typed envelope properties: flags, submit times, importance, and the GetPropertyValue helpers.</summary>
public sealed class EnvelopeTests
{
    [Fact]
    public void Read_and_draft_come_from_the_message_flags()
    {
        var message = MapiMessage.Create("x");
        Assert.Null(message.MessageFlags);
        Assert.False(message.IsRead);
        Assert.False(message.IsDraft);

        message.MessageFlags = MapiMessageFlags.Read | MapiMessageFlags.HasAttachment;
        Assert.True(message.IsRead);
        Assert.False(message.IsDraft);

        message.MessageFlags = MapiMessageFlags.Unsent | MapiMessageFlags.Unmodified;
        Assert.False(message.IsRead);
        Assert.True(message.IsDraft);
        Assert.Equal(MapiMessageFlags.Unsent | MapiMessageFlags.Unmodified, message.MessageFlags);

        var loaded = TestMessages.ThroughMsg(message);
        Assert.True(loaded.IsDraft);
        Assert.Equal(message.MessageFlags, loaded.MessageFlags);

        message.MessageFlags = null;
        Assert.Null(message.MessageFlags);
    }

    [Fact]
    public void Eml_drafts_and_read_status_come_from_their_headers()
    {
        var draft = MapiMessage.LoadFromEml("X-Unsent: 1\r\nSubject: d\r\n\r\nbody"u8.ToArray(), TestContext.Current.CancellationToken);
        var read = MapiMessage.LoadFromEml("Status: RO\r\nSubject: r\r\n\r\nbody"u8.ToArray(), TestContext.Current.CancellationToken);
        var plain = MapiMessage.LoadFromEml("Subject: p\r\n\r\nbody"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.True(draft.IsDraft);
        Assert.False(draft.IsRead);
        Assert.True(read.IsRead);
        Assert.False(read.IsDraft);
        Assert.False(plain.IsRead);
        Assert.False(plain.IsDraft);
    }

    [Fact]
    public void Msg_fixtures_report_flags_consistently()
    {
        var message = MapiMessage.FromFile(TestDocuments.RequireFile("email/simple_msg.msg"), TestContext.Current.CancellationToken);

        Assert.NotNull(message.MessageFlags);
        Assert.Equal(message.MessageFlags!.Value.HasFlag(MapiMessageFlags.Read), message.IsRead);
        Assert.Equal(message.MessageFlags!.Value.HasFlag(MapiMessageFlags.Unsent), message.IsDraft);
    }

    [Fact]
    public void Submit_times_round_trip_and_sent_time_picks_the_best_one()
    {
        var message = MapiMessage.Create("x");
        message.MessageDeliveryTime = new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(message.MessageDeliveryTime, message.SentTime);

        message.SubmitTime = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(message.SubmitTime, message.SentTime);

        message.ClientSubmitTime = new DateTime(2024, 1, 1, 12, 30, 15, DateTimeKind.Utc);
        Assert.Equal(message.ClientSubmitTime, message.SentTime);

        var loaded = TestMessages.ThroughMsg(message);
        Assert.Equal(new DateTime(2024, 1, 1, 12, 30, 15), loaded.ClientSubmitTime?.ToUniversalTime().AddTicks(-(loaded.ClientSubmitTime?.Ticks ?? 0) % TimeSpan.TicksPerSecond));
        Assert.Equal(message.SubmitTime, loaded.SubmitTime);

        message.ClientSubmitTime = null;
        Assert.Null(message.ClientSubmitTime);
    }

    [Fact]
    public void Creation_and_modification_times_and_enums_are_typed()
    {
        var message = MapiMessage.Create("x");
        message.SetProperty(0x3007, (ushort)PropertyTypeCode.PtypTime, new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc));
        message.SetProperty(0x3008, (ushort)PropertyTypeCode.PtypTime, new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc));
        message.SetProperty(0x0017, (ushort)PropertyTypeCode.PtypInteger32, 2);
        message.SetProperty(0x0036, (ushort)PropertyTypeCode.PtypInteger32, 3);

        Assert.Equal(2020, message.CreationTime?.Year);
        Assert.Equal(2021, message.LastModificationTime?.Year);
        Assert.Equal(MapiImportance.High, message.Importance);
        Assert.Equal(MapiSensitivity.CompanyConfidential, message.Sensitivity);
        Assert.Null(MapiMessage.Create("y").Importance);
        message.SetProperty(0x0017, (ushort)PropertyTypeCode.PtypInteger32, 99);
        Assert.Null(message.Importance);
    }

    [Fact]
    public void Property_helpers_convert_between_stored_types()
    {
        var message = MapiMessage.Create("x");
        message.SetProperty(0x9001, (ushort)PropertyTypeCode.PtypInteger32, 42);
        message.SetProperty(0x9002, (ushort)PropertyTypeCode.PtypInteger64, 7_000_000_000L);
        message.SetProperty(0x9003, (ushort)PropertyTypeCode.PtypBoolean, true);
        message.SetProperty(0x9004, (ushort)PropertyTypeCode.PtypString, "text");
        message.SetProperty(0x9005, (ushort)PropertyTypeCode.PtypString8, "ansi");
        message.SetProperty(0x9006, (ushort)PropertyTypeCode.PtypBinary, new byte[] { 1, 2 });
        message.SetProperty(0x9007, (ushort)PropertyTypeCode.PtypTime, new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        message.SetProperty(0x9008, (ushort)PropertyTypeCode.PtypFloating64, 2.5);

        Assert.Equal(42, message.GetPropertyInt32(0x9001));
        Assert.Null(message.GetPropertyInt32(0x9002));
        Assert.Equal(7_000_000_000L, message.GetPropertyValue<long>(0x9002));
        Assert.Equal(42L, message.GetPropertyValue<long>(0x9001));
        Assert.Equal(42.0, message.GetPropertyValue<double>(0x9001));
        Assert.True(message.GetPropertyBoolean(0x9003));
        Assert.Equal("text", message.GetPropertyString(0x9004));
        Assert.Equal("ansi", message.GetPropertyString(0x9005));
        Assert.Equal("ansi", message.GetPropertyValue<string>(0x9005));
        Assert.Equal(new byte[] { 1, 2 }, message.GetPropertyBytes(0x9006));
        Assert.Equal(new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc), message.GetPropertyDateTime(0x9007));
        Assert.Equal(new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc), message.GetPropertyValue<DateTime>(0x9007));
        Assert.Equal(2.5, message.GetPropertyValue<double>(0x9008));
        Assert.True(message.HasProperty(0x9001));
        Assert.False(message.HasProperty(0x9FFF));
        Assert.Null(message.GetPropertyValue<string>(0x9FFF));
        Assert.Equal(0, message.GetPropertyValue<int>(0x9FFF));
        Assert.Null(message.GetPropertyValue<int?>(0x9FFF));
        Assert.Equal(0, message.GetPropertyValue<int>(0x9004));
        Assert.Equal("42", message.GetPropertyValue<string>(0x9001));
    }

    [Fact]
    public void Enum_properties_convert_from_integers()
    {
        var message = MapiMessage.Create("x");
        message.SetProperty(0x9001, (ushort)PropertyTypeCode.PtypInteger32, 2);

        Assert.Equal(MapiImportance.High, message.GetPropertyValue<MapiImportance>(0x9001));
    }
}

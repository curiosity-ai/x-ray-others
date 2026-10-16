using XRay.Email.Msg;
using Xunit;

namespace XRay.Email.Tests;

public sealed class MapiMessageTests
{
    [Fact]
    public void CanCreateSaveAndReloadMessageWithSenderRecipientAndAttachment()
    {
        var message = MapiMessage.Create("Hello", "Body");
        message.SenderName = "Alice";
        message.SenderEmailAddress = "alice@example.com";
        message.InternetMessageId = "<hello@example.com>";
        message.MessageDeliveryTime = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        message.AddRecipient("bob@example.com", "Bob");
        message.AddAttachment("note.txt", "abc"u8.ToArray(), "text/plain");

        var bytes = message.Save();
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.msg");
        File.WriteAllBytes(tempPath, bytes);

        try
        {
            var loaded = MapiMessage.FromFile(tempPath);
            Assert.Equal("Hello", loaded.Subject);
            Assert.Equal("Body", loaded.Body);
            Assert.Equal("Alice", loaded.SenderName);
            Assert.Equal("alice@example.com", loaded.SenderEmailAddress);
            Assert.Equal("<hello@example.com>", loaded.InternetMessageId);
            Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), loaded.MessageDeliveryTime);
            Assert.Single(loaded.Recipients);
            Assert.Equal("bob@example.com", loaded.Recipients[0].EmailAddress);
            Assert.Single(loaded.Attachments);
            Assert.Equal("note.txt", loaded.Attachments[0].Filename);
            Assert.Equal("text/plain", loaded.Attachments[0].MimeType);
            Assert.Equal("abc"u8.ToArray(), loaded.Attachments[0].Data);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void CanCreateSaveAndReloadMessageUsingStreams()
    {
        var message = MapiMessage.Create("Stream subject", "Stream body");
        message.SenderEmailAddress = "alice@example.com";
        using var attachmentStream = new MemoryStream("stream-data"u8.ToArray());
        message.AddAttachment("stream.txt", attachmentStream, "text/plain");

        using var messageStream = new MemoryStream();
        message.Save(messageStream);
        messageStream.Position = 0;

        var loaded = MapiMessage.FromStream(messageStream);

        Assert.Equal("Stream subject", loaded.Subject);
        Assert.Equal("Stream body", loaded.Body);
        var attachment = Assert.Single(loaded.Attachments);
        Assert.Equal("stream.txt", attachment.Filename);
        Assert.Equal("stream-data"u8.ToArray(), attachment.Data);
    }

    [Fact]
    public void CanRoundTripEmbeddedMessageAttachment()
    {
        var parent = MapiMessage.Create("Outer", "Parent body");
        var child = MapiMessage.Create("Inner", "Child body");
        child.SenderEmailAddress = "inner@example.com";
        parent.AddEmbeddedMessageAttachment(child, "inner.msg");

        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.msg");
        parent.Save(tempPath);

        try
        {
            var loaded = MapiMessage.FromFile(tempPath);
            var attachment = Assert.Single(loaded.Attachments);
            Assert.True(attachment.IsEmbeddedMessage);
            Assert.Equal("inner.msg", attachment.Filename);
            Assert.NotNull(attachment.EmbeddedMessage);
            Assert.Equal("Inner", attachment.EmbeddedMessage!.Subject);
            Assert.Equal("Child body", attachment.EmbeddedMessage.Body);
            Assert.Equal("inner@example.com", attachment.EmbeddedMessage.SenderEmailAddress);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}

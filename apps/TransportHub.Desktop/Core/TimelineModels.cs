using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace TransportHub.Desktop.Core
{
    public sealed class TimelineProtocolException : System.IO.IOException
    {
        public TimelineProtocolException(string message) : base(message) { }
        public TimelineProtocolException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Wire-protocol limits. Values are measured after UTF-8 encoding where noted.
    /// </summary>
    public static class TimelineProtocol
    {
        public const int SchemaVersion = 1;
        public const int DefaultRecentMessageCount = 200;
        public const int MaximumRecentMessageCount = 5000;
        public const int MaximumMessageFileBytes = 128 * 1024;
        public const int MaximumReceiptFileBytes = 16 * 1024;
        public const int MaximumDeviceFileBytes = 4 * 1024;
        public const int MaximumTextBytes = 32 * 1024;
        public const int MaximumUrlBytes = 8 * 1024;
        public const int MaximumSenderNameBytes = 256;
        public const int MaximumTargetDevices = 256;
        public const long MaximumAttachmentBytes = 4L * 1024L * 1024L * 1024L * 1024L;
    }

    public enum TimelineMessageKind
    {
        Text,
        Link,
        Attachment
    }

    /// <summary>
    /// Immutable metadata for an attachment already stored below the Syncthing root.
    /// RelativePath always uses forward slashes and is never an absolute path.
    /// </summary>
    public sealed class TimelineAttachment
    {
        internal TimelineAttachment(string relativePath, string mimeType, long sizeBytes, string sha256)
        {
            RelativePath = relativePath;
            MimeType = mimeType;
            SizeBytes = sizeBytes;
            Sha256 = sha256;
        }

        public string RelativePath { get; private set; }
        public string MimeType { get; private set; }
        public long SizeBytes { get; private set; }
        public string Sha256 { get; private set; }
        public bool IsDirectory
        {
            get { return String.Equals(MimeType, "inode/directory", StringComparison.OrdinalIgnoreCase); }
        }
    }

    /// <summary>
    /// An immutable timeline event. A message file is the source of truth and is
    /// never edited after creation.
    /// </summary>
    public sealed class TimelineMessage
    {
        private readonly ReadOnlyCollection<string> _targetDeviceIds;

        internal TimelineMessage(
            int schema,
            string id,
            TimelineMessageKind kind,
            string senderDeviceId,
            string senderName,
            DateTime createdUtc,
            string text,
            string linkUrl,
            TimelineAttachment attachment,
            IEnumerable<string> targetDeviceIds)
        {
            Schema = schema;
            Id = id;
            Kind = kind;
            SenderDeviceId = senderDeviceId;
            SenderName = senderName;
            CreatedUtc = DateTime.SpecifyKind(createdUtc, DateTimeKind.Utc);
            Text = text;
            LinkUrl = linkUrl;
            Attachment = attachment;
            _targetDeviceIds = new ReadOnlyCollection<string>(new List<string>(targetDeviceIds));
        }

        public int Schema { get; private set; }
        public string Id { get; private set; }
        public TimelineMessageKind Kind { get; private set; }
        public string SenderDeviceId { get; private set; }
        public string SenderName { get; private set; }
        public DateTime CreatedUtc { get; private set; }
        public string Text { get; private set; }
        public string LinkUrl { get; private set; }
        public TimelineAttachment Attachment { get; private set; }
        public IReadOnlyList<string> TargetDeviceIds { get { return _targetDeviceIds; } }
    }

    /// <summary>
    /// A delivery receipt means that TransportHub on ReceiverDeviceId parsed the
    /// message successfully. It deliberately does not mean that a person read it.
    /// </summary>
    public sealed class DeliveryReceipt
    {
        internal DeliveryReceipt(
            int schema,
            string messageId,
            string receiverDeviceId,
            string receiverName,
            DateTime receivedUtc)
        {
            Schema = schema;
            MessageId = messageId;
            ReceiverDeviceId = receiverDeviceId;
            ReceiverName = receiverName;
            ReceivedUtc = DateTime.SpecifyKind(receivedUtc, DateTimeKind.Utc);
        }

        public int Schema { get; private set; }
        public string MessageId { get; private set; }
        public string ReceiverDeviceId { get; private set; }
        public string ReceiverName { get; private set; }
        public DateTime ReceivedUtc { get; private set; }
    }

    public sealed class DeliverySummary
    {
        private readonly ReadOnlyCollection<string> _deliveredDeviceIds;

        internal DeliverySummary(int targetCount, IEnumerable<string> deliveredDeviceIds)
        {
            TargetCount = targetCount;
            _deliveredDeviceIds = new ReadOnlyCollection<string>(new List<string>(deliveredDeviceIds));
        }

        public int TargetCount { get; private set; }
        public int DeliveredCount { get { return _deliveredDeviceIds.Count; } }
        public int PendingCount { get { return Math.Max(0, TargetCount - DeliveredCount); } }
        public bool IsDeliveredToAll { get { return DeliveredCount >= TargetCount; } }
        public IReadOnlyList<string> DeliveredDeviceIds { get { return _deliveredDeviceIds; } }
    }

    /// <summary>
    /// Optional diagnostic returned by the overload of LoadRecentMessages that
    /// accepts a rejection collection. Paths are relative to the messages root.
    /// </summary>
    public sealed class TimelineReadRejection
    {
        internal TimelineReadRejection(string relativePath, string reason)
        {
            RelativePath = relativePath;
            Reason = reason;
        }

        public string RelativePath { get; private set; }
        public string Reason { get; private set; }
    }
}

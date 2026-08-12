using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace TransportHub.Desktop.Core
{
    /// <summary>
    /// Stores immutable timeline messages and delivery receipts inside a Syncthing
    /// root. This class has no UI or Syncthing process dependency.
    /// </summary>
    public sealed class TimelineStore
    {
        private const string MetadataDirectoryName = ".transporthub";
        private const string MessagesDirectoryName = "messages";
        private const string ReceiptsDirectoryName = "receipts";
        private const string DevicesDirectoryName = "devices";

        private static readonly Regex SyncthingDeviceIdPattern = new Regex(
            @"^[A-Z2-7]{7}(?:-[A-Z2-7]{7}){7}$",
            RegexOptions.CultureInvariant);

        private static readonly Regex UlidPattern = new Regex(
            @"^[0-9A-HJKMNP-TV-Z]{26}$",
            RegexOptions.CultureInvariant);

        private static readonly Regex GuidPattern = new Regex(
            @"^(?:[0-9A-F]{32}|[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12})$",
            RegexOptions.CultureInvariant);

        private static readonly Regex MimeTypePattern = new Regex(
            @"^[A-Za-z0-9][A-Za-z0-9!#$&^_.+\-]{0,126}/[A-Za-z0-9][A-Za-z0-9!#$&^_.+\-]{0,126}$",
            RegexOptions.CultureInvariant);

        private static readonly Regex Sha256Pattern = new Regex(
            @"^[0-9A-F]{64}$",
            RegexOptions.CultureInvariant);

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private readonly Func<DateTime> _utcNow;
        private readonly Func<string> _idFactory;

        public TimelineStore(string syncRoot, string localDeviceId, string localDeviceName)
            : this(syncRoot, localDeviceId, localDeviceName,
                delegate { return DateTime.UtcNow; },
                delegate { return Guid.NewGuid().ToString("N").ToUpperInvariant(); })
        {
        }

        internal TimelineStore(
            string syncRoot,
            string localDeviceId,
            string localDeviceName,
            Func<DateTime> utcNow,
            Func<string> idFactory)
        {
            if (utcNow == null)
            {
                throw new ArgumentNullException("utcNow");
            }
            if (idFactory == null)
            {
                throw new ArgumentNullException("idFactory");
            }

            SyncRoot = PathSafety.NormalizeRootPath(syncRoot);
            LocalDeviceId = NormalizeDeviceId(localDeviceId, "localDeviceId");
            LocalDeviceName = ValidateDisplayText(localDeviceName, TimelineProtocol.MaximumSenderNameBytes,
                "localDeviceName", false);
            _utcNow = utcNow;
            _idFactory = idFactory;

            Directory.CreateDirectory(SyncRoot);
            PathSafety.EnsureNoReparsePoints(SyncRoot, SyncRoot);

            MetadataRoot = Path.Combine(SyncRoot, MetadataDirectoryName);
            MessagesRoot = Path.Combine(MetadataRoot, MessagesDirectoryName);
            ReceiptsRoot = Path.Combine(MetadataRoot, ReceiptsDirectoryName);
            DevicesRoot = Path.Combine(MetadataRoot, DevicesDirectoryName);
            CreateProtocolDirectory(MetadataRoot);
            CreateProtocolDirectory(MessagesRoot);
            CreateProtocolDirectory(ReceiptsRoot);
            CreateProtocolDirectory(DevicesRoot);
            RegisterLocalDevice();
        }

        public string SyncRoot { get; private set; }
        public string MetadataRoot { get; private set; }
        public string MessagesRoot { get; private set; }
        public string ReceiptsRoot { get; private set; }
        public string DevicesRoot { get; private set; }
        public string LocalDeviceId { get; private set; }
        public string LocalDeviceName { get; private set; }

        /// <summary>
        /// Returns devices that have run TransportHub at least once in this shared
        /// folder. Each device owns one immutable registration file, allowing a
        /// leaf node in a hub topology to address receipts to other known leaves.
        /// </summary>
        public IReadOnlyList<string> LoadKnownDeviceIds()
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] files;
            try
            {
                PathSafety.EnsureNoReparsePoints(SyncRoot, DevicesRoot);
                files = Directory.GetFiles(DevicesRoot, "*.json", SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception)
            {
                if (IsExpectedReadFailure(exception))
                {
                    return result.AsReadOnly();
                }
                throw;
            }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                try
                {
                    if (PathSafety.IsSyncConflictPath(file) || IsReparsePoint(file))
                    {
                        continue;
                    }
                    string fileId = NormalizeDeviceId(Path.GetFileNameWithoutExtension(file), "deviceFileName");
                    DeviceWire wire = ReadJsonFile<DeviceWire>(file, TimelineProtocol.MaximumDeviceFileBytes);
                    if (wire == null || wire.schema != TimelineProtocol.SchemaVersion)
                    {
                        continue;
                    }
                    string wireId = NormalizeDeviceId(wire.deviceId, "deviceId");
                    ValidateDisplayText(wire.deviceName, TimelineProtocol.MaximumSenderNameBytes,
                        "deviceName", false);
                    if (String.Equals(fileId, wireId, StringComparison.OrdinalIgnoreCase) && seen.Add(wireId))
                    {
                        result.Add(wireId);
                    }
                }
                catch (Exception exception)
                {
                    if (!IsExpectedReadFailure(exception))
                    {
                        throw;
                    }
                }
            }
            result.Sort(StringComparer.Ordinal);
            return result.AsReadOnly();
        }

        private void RegisterLocalDevice()
        {
            string finalPath = Path.Combine(DevicesRoot, LocalDeviceId + ".json");
            if (File.Exists(finalPath))
            {
                return;
            }
            DeviceWire wire = new DeviceWire
            {
                schema = TimelineProtocol.SchemaVersion,
                deviceId = LocalDeviceId,
                deviceName = LocalDeviceName
            };
            try
            {
                WriteImmutableFile(finalPath, SerializeJson(wire, TimelineProtocol.MaximumDeviceFileBytes));
            }
            catch (IOException)
            {
                // A concurrent instance or sync operation may have won the create race.
                if (!File.Exists(finalPath))
                {
                    throw;
                }
            }
        }

        public TimelineMessage CreateText(string text, IEnumerable<string> targetDeviceIds)
        {
            string validatedText = ValidateDisplayText(text, TimelineProtocol.MaximumTextBytes, "text", false);
            return CreateMessage(TimelineMessageKind.Text, validatedText, null, null, targetDeviceIds);
        }

        public TimelineMessage CreateLink(string url, IEnumerable<string> targetDeviceIds)
        {
            return CreateLink(url, null, targetDeviceIds);
        }

        public TimelineMessage CreateLink(string url, string text, IEnumerable<string> targetDeviceIds)
        {
            string validatedUrl = ValidateHttpUrl(url);
            string validatedText = String.IsNullOrEmpty(text)
                ? null
                : ValidateDisplayText(text, TimelineProtocol.MaximumTextBytes, "text", true);
            return CreateMessage(TimelineMessageKind.Link, validatedText, validatedUrl, null, targetDeviceIds);
        }

        /// <summary>
        /// Creates an attachment event for a regular file or directory that already
        /// exists below SyncRoot. Directories must use MIME type inode/directory and
        /// an empty hash. Regular files retain exact size and SHA-256 validation.
        /// </summary>
        public TimelineMessage CreateAttachment(
            string relativePath,
            string mimeType,
            long sizeBytes,
            string sha256,
            IEnumerable<string> targetDeviceIds)
        {
            string normalizedRelativePath = PathSafety.NormalizeRelativePath(relativePath);
            if (sizeBytes < 0 || sizeBytes > TimelineProtocol.MaximumAttachmentBytes)
            {
                throw new ArgumentOutOfRangeException("sizeBytes", "The attachment size is outside the protocol limits.");
            }

            string normalizedMimeType = ValidateMimeType(mimeType);
            bool isDirectory = String.Equals(normalizedMimeType, "inode/directory", StringComparison.Ordinal);
            string normalizedSha256;
            if (isDirectory)
            {
                PathSafety.EnsureExistingDirectoryIsSafe(SyncRoot, normalizedRelativePath);
                normalizedSha256 = ValidateSha256(sha256, true);
            }
            else
            {
                string fullPath = PathSafety.EnsureExistingFileIsSafe(SyncRoot, normalizedRelativePath);
                long actualSize = new FileInfo(fullPath).Length;
                if (actualSize != sizeBytes)
                {
                    throw new ArgumentException("The attachment size does not match the local file.", "sizeBytes");
                }
                normalizedSha256 = ValidateSha256(sha256, false);
            }
            TimelineAttachment attachment = new TimelineAttachment(
                normalizedRelativePath, normalizedMimeType, sizeBytes, normalizedSha256);
            return CreateMessage(TimelineMessageKind.Attachment, null, null, attachment, targetDeviceIds);
        }

        public IReadOnlyList<TimelineMessage> LoadRecentMessages(int maxCount)
        {
            return LoadRecentMessages(maxCount, null);
        }

        public IReadOnlyList<TimelineMessage> LoadRecentMessages()
        {
            return LoadRecentMessages(TimelineProtocol.DefaultRecentMessageCount, null);
        }

        public IReadOnlyList<string> LoadAllAttachmentPaths()
        {
            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in EnumerateMessageFiles(null))
            {
                TimelineMessage message;
                string reason;
                if (TryLoadMessageFile(file, out message, out reason) && message.Attachment != null)
                {
                    paths.Add(message.Attachment.RelativePath);
                }
            }
            List<string> result = new List<string>(paths);
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result.AsReadOnly();
        }

        /// <summary>
        /// Loads the newest messages and returns them in chronological order. Invalid
        /// or conflict-copy files are skipped; optional diagnostics explain why.
        /// </summary>
        public IReadOnlyList<TimelineMessage> LoadRecentMessages(
            int maxCount,
            ICollection<TimelineReadRejection> rejections)
        {
            if (maxCount < 1 || maxCount > TimelineProtocol.MaximumRecentMessageCount)
            {
                throw new ArgumentOutOfRangeException("maxCount");
            }

            List<string> paths = EnumerateMessageFiles(rejections);
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            List<TimelineMessage> messages = new List<TimelineMessage>();
            HashSet<string> messageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in paths)
            {
                TimelineMessage message;
                string rejectionReason;
                if (!TryLoadMessageFile(path, out message, out rejectionReason))
                {
                    AddRejection(rejections, path, rejectionReason);
                    continue;
                }

                if (!messageIds.Add(message.Id))
                {
                    AddRejection(rejections, path, "A duplicate message ID was ignored.");
                    continue;
                }
                messages.Add(message);
            }

            messages.Sort(CompareMessagesAscending);
            if (messages.Count > maxCount)
            {
                messages.RemoveRange(0, messages.Count - maxCount);
            }
            return messages.AsReadOnly();
        }

        /// <summary>
        /// Safely parses one FileSystemWatcher candidate. The path must have the exact
        /// messages/&lt;sender&gt;/yyyy-MM/&lt;id&gt;.json layout below this store.
        /// </summary>
        public bool TryLoadMessageFile(
            string fullPath,
            out TimelineMessage message,
            out string rejectionReason)
        {
            message = null;
            rejectionReason = null;
            try
            {
                string normalizedPath = ValidateMessageFilePath(fullPath);
                MessageWire wire = ReadJsonFile<MessageWire>(normalizedPath, TimelineProtocol.MaximumMessageFileBytes);
                TimelineMessage parsed = FromWire(wire);
                ValidateMessagePathMatchesContent(normalizedPath, parsed);
                message = parsed;
                return true;
            }
            catch (Exception exception)
            {
                if (!IsExpectedReadFailure(exception))
                {
                    throw;
                }
                rejectionReason = exception.Message;
                return false;
            }
        }

        public bool TryGetMessage(string messageId, out TimelineMessage message)
        {
            string normalizedMessageId = NormalizeMessageId(messageId, "messageId");
            foreach (string path in EnumerateMessageFiles(null))
            {
                if (!String.Equals(Path.GetFileNameWithoutExtension(path), normalizedMessageId,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string reason;
                if (TryLoadMessageFile(path, out message, out reason) &&
                    String.Equals(message.Id, normalizedMessageId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            message = null;
            return false;
        }

        /// <summary>
        /// Creates this device's receipt idempotently. The referenced message must
        /// already exist and pass validation in this store.
        /// </summary>
        public DeliveryReceipt CreateDeliveryReceipt(string messageId)
        {
            TimelineMessage message;
            if (!TryGetMessage(messageId, out message))
            {
                throw new ArgumentException("The receipt refers to an unknown or invalid message.", "messageId");
            }

            string normalizedMessageId = message.Id;
            string receiptDirectory = Path.Combine(ReceiptsRoot, normalizedMessageId);
            CreateProtocolDirectory(receiptDirectory);
            string finalPath = Path.Combine(receiptDirectory, LocalDeviceId + ".json");

            if (File.Exists(finalPath))
            {
                return ReadAndValidateReceipt(finalPath, normalizedMessageId, LocalDeviceId);
            }

            DeliveryReceipt receipt = new DeliveryReceipt(
                TimelineProtocol.SchemaVersion,
                normalizedMessageId,
                LocalDeviceId,
                LocalDeviceName,
                GetUtcNow());
            ReceiptWire wire = ToWire(receipt);
            byte[] content = SerializeJson(wire, TimelineProtocol.MaximumReceiptFileBytes);

            try
            {
                WriteImmutableFile(finalPath, content);
            }
            catch (IOException)
            {
                // Another process/thread may have won the immutable CreateNew race.
                if (!File.Exists(finalPath))
                {
                    throw;
                }
            }

            return ReadAndValidateReceipt(finalPath, normalizedMessageId, LocalDeviceId);
        }

        public IReadOnlyList<DeliveryReceipt> LoadReceipts(string messageId)
        {
            string normalizedMessageId = NormalizeMessageId(messageId, "messageId");
            string directory = Path.Combine(ReceiptsRoot, normalizedMessageId);
            if (!Directory.Exists(directory))
            {
                return new List<DeliveryReceipt>().AsReadOnly();
            }

            PathSafety.EnsureNoReparsePoints(SyncRoot, directory);
            List<DeliveryReceipt> receipts = new List<DeliveryReceipt>();
            HashSet<string> devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                if (PathSafety.IsSyncConflictPath(file) || IsReparsePoint(file))
                {
                    continue;
                }

                string fileDeviceId;
                try
                {
                    fileDeviceId = NormalizeDeviceId(Path.GetFileNameWithoutExtension(file), "receiptFileName");
                    DeliveryReceipt receipt = ReadAndValidateReceipt(file, normalizedMessageId, fileDeviceId);
                    if (devices.Add(receipt.ReceiverDeviceId))
                    {
                        receipts.Add(receipt);
                    }
                }
                catch (Exception exception)
                {
                    if (!IsExpectedReadFailure(exception))
                    {
                        throw;
                    }
                }
            }

            receipts.Sort(delegate(DeliveryReceipt left, DeliveryReceipt right)
            {
                int timeComparison = DateTime.Compare(left.ReceivedUtc, right.ReceivedUtc);
                return timeComparison != 0
                    ? timeComparison
                    : StringComparer.Ordinal.Compare(left.ReceiverDeviceId, right.ReceiverDeviceId);
            });
            return receipts.AsReadOnly();
        }

        public DeliverySummary GetDeliverySummary(TimelineMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }

            HashSet<string> targetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string targetDeviceId in message.TargetDeviceIds)
            {
                if (!String.Equals(targetDeviceId, message.SenderDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    targetIds.Add(targetDeviceId);
                }
            }

            List<string> delivered = new List<string>();
            foreach (DeliveryReceipt receipt in LoadReceipts(message.Id))
            {
                if (targetIds.Contains(receipt.ReceiverDeviceId))
                {
                    delivered.Add(receipt.ReceiverDeviceId);
                }
            }
            delivered.Sort(StringComparer.Ordinal);
            return new DeliverySummary(targetIds.Count, delivered);
        }

        private TimelineMessage CreateMessage(
            TimelineMessageKind kind,
            string text,
            string linkUrl,
            TimelineAttachment attachment,
            IEnumerable<string> targetDeviceIds)
        {
            string messageId = NormalizeMessageId(_idFactory(), "generatedMessageId");
            DateTime createdUtc = GetUtcNow();
            List<string> targets = NormalizeTargetDevices(targetDeviceIds, LocalDeviceId);
            TimelineMessage message = new TimelineMessage(
                TimelineProtocol.SchemaVersion,
                messageId,
                kind,
                LocalDeviceId,
                LocalDeviceName,
                createdUtc,
                text,
                linkUrl,
                attachment,
                targets);

            string senderDirectory = Path.Combine(MessagesRoot, LocalDeviceId);
            string monthDirectory = Path.Combine(senderDirectory, createdUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture));
            CreateProtocolDirectory(senderDirectory);
            CreateProtocolDirectory(monthDirectory);
            string finalPath = Path.Combine(monthDirectory, message.Id + ".json");
            byte[] content = SerializeJson(ToWire(message), TimelineProtocol.MaximumMessageFileBytes);
            WriteImmutableFile(finalPath, content);
            return message;
        }

        private void CreateProtocolDirectory(string fullPath)
        {
            string normalized = Path.GetFullPath(fullPath);
            PathSafety.EnsureNoReparsePoints(SyncRoot, normalized);
            Directory.CreateDirectory(normalized);
            PathSafety.EnsureNoReparsePoints(SyncRoot, normalized);
        }

        private List<string> EnumerateMessageFiles(ICollection<TimelineReadRejection> rejections)
        {
            PathSafety.EnsureNoReparsePoints(SyncRoot, MessagesRoot);
            List<string> files = new List<string>();
            foreach (string senderDirectory in Directory.GetDirectories(MessagesRoot, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    string senderComponent = Path.GetFileName(senderDirectory);
                    NormalizeDeviceId(senderComponent, "senderDirectory");
                    if (PathSafety.IsSyncConflictPath(senderDirectory) || IsReparsePoint(senderDirectory))
                    {
                        throw new TimelineProtocolException("Unsafe sender directory.");
                    }

                    foreach (string monthDirectory in Directory.GetDirectories(senderDirectory, "*", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            string month = Path.GetFileName(monthDirectory);
                            DateTime ignoredMonth;
                            if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out ignoredMonth))
                            {
                                throw new TimelineProtocolException("Invalid message month directory.");
                            }
                            if (PathSafety.IsSyncConflictPath(monthDirectory) || IsReparsePoint(monthDirectory))
                            {
                                throw new TimelineProtocolException("Unsafe message month directory.");
                            }

                            string[] monthFiles = Directory.GetFiles(monthDirectory, "*.json", SearchOption.TopDirectoryOnly);
                            foreach (string monthFile in monthFiles)
                            {
                                files.Add(monthFile);
                            }
                        }
                        catch (Exception exception)
                        {
                            if (!IsExpectedReadFailure(exception))
                            {
                                throw;
                            }
                            AddRejection(rejections, monthDirectory, exception.Message);
                        }
                    }
                }
                catch (Exception exception)
                {
                    if (!IsExpectedReadFailure(exception))
                    {
                        throw;
                    }
                    AddRejection(rejections, senderDirectory, exception.Message);
                }
            }
            return files;
        }

        private string ValidateMessageFilePath(string fullPath)
        {
            if (String.IsNullOrWhiteSpace(fullPath))
            {
                throw new ArgumentException("A message file path is required.", "fullPath");
            }

            string normalized = Path.GetFullPath(fullPath);
            string relative = PathSafety.GetRelativePathUnderRoot(MessagesRoot, normalized);
            string[] segments = relative.Split('/');
            if (segments.Length != 3)
            {
                throw new TimelineProtocolException("The message file is not in the expected sender/month/file layout.");
            }

            NormalizeDeviceId(segments[0], "senderDirectory");
            DateTime ignoredMonth;
            if (!DateTime.TryParseExact(segments[1], "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out ignoredMonth))
            {
                throw new TimelineProtocolException("The message file has an invalid month directory.");
            }
            if (!String.Equals(Path.GetExtension(segments[2]), ".json", StringComparison.OrdinalIgnoreCase))
            {
                throw new TimelineProtocolException("Only JSON message files are accepted.");
            }
            NormalizeMessageId(Path.GetFileNameWithoutExtension(segments[2]), "messageFileName");
            if (PathSafety.IsSyncConflictPath(relative))
            {
                throw new TimelineProtocolException("Syncthing conflict-copy messages are not accepted.");
            }

            PathSafety.EnsureNoReparsePoints(SyncRoot, normalized);
            if (IsReparsePoint(normalized))
            {
                throw new TimelineProtocolException("Reparse-point message files are not accepted.");
            }
            return normalized;
        }

        private void ValidateMessagePathMatchesContent(string fullPath, TimelineMessage message)
        {
            string relative = PathSafety.GetRelativePathUnderRoot(MessagesRoot, fullPath);
            string[] segments = relative.Split('/');
            if (!String.Equals(segments[0], message.SenderDeviceId, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(segments[1], message.CreatedUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                !String.Equals(Path.GetFileNameWithoutExtension(segments[2]), message.Id, StringComparison.OrdinalIgnoreCase))
            {
                throw new TimelineProtocolException("The message content does not match its immutable storage path.");
            }
        }

        private TimelineMessage FromWire(MessageWire wire)
        {
            if (wire == null || wire.schema != TimelineProtocol.SchemaVersion)
            {
                throw new TimelineProtocolException("The message schema is missing or unsupported.");
            }

            string id = NormalizeMessageId(wire.id, "id");
            string senderDeviceId = NormalizeDeviceId(wire.senderDeviceId, "senderDeviceId");
            string senderName = ValidateDisplayText(wire.senderName, TimelineProtocol.MaximumSenderNameBytes,
                "senderName", false);
            DateTime createdUtc = ParseUtcTimestamp(wire.createdUtc, "createdUtc");
            List<string> targets = NormalizeTargetDevices(wire.targetDeviceIds, null);

            TimelineMessageKind kind;
            if (String.Equals(wire.kind, "text", StringComparison.Ordinal))
            {
                kind = TimelineMessageKind.Text;
            }
            else if (String.Equals(wire.kind, "link", StringComparison.Ordinal))
            {
                kind = TimelineMessageKind.Link;
            }
            else if (String.Equals(wire.kind, "attachment", StringComparison.Ordinal))
            {
                kind = TimelineMessageKind.Attachment;
            }
            else
            {
                throw new TimelineProtocolException("The message kind is missing or unsupported.");
            }

            string text = null;
            string linkUrl = null;
            TimelineAttachment attachment = null;
            switch (kind)
            {
                case TimelineMessageKind.Text:
                    text = ValidateDisplayText(wire.text, TimelineProtocol.MaximumTextBytes, "text", false);
                    EnsureAbsent(wire.url, wire.attachment, "A text message contains fields for another kind.");
                    break;
                case TimelineMessageKind.Link:
                    linkUrl = ValidateHttpUrl(wire.url);
                    text = String.IsNullOrEmpty(wire.text)
                        ? null
                        : ValidateDisplayText(wire.text, TimelineProtocol.MaximumTextBytes, "text", true);
                    if (wire.attachment != null)
                    {
                        throw new TimelineProtocolException("A link message cannot contain an attachment.");
                    }
                    break;
                case TimelineMessageKind.Attachment:
                    if (wire.attachment == null)
                    {
                        throw new TimelineProtocolException("Attachment metadata is missing.");
                    }
                    if (!String.IsNullOrEmpty(wire.text) || !String.IsNullOrEmpty(wire.url))
                    {
                        throw new TimelineProtocolException("An attachment message contains fields for another kind.");
                    }
                    string relativePath = PathSafety.NormalizeRelativePath(wire.attachment.relativePath);
                    long sizeBytes = wire.attachment.sizeBytes;
                    if (sizeBytes < 0 || sizeBytes > TimelineProtocol.MaximumAttachmentBytes)
                    {
                        throw new TimelineProtocolException("The attachment size is outside the protocol limits.");
                    }
                    string attachmentMimeType = ValidateMimeType(wire.attachment.mimeType);
                    bool attachmentIsDirectory = String.Equals(
                        attachmentMimeType, "inode/directory", StringComparison.Ordinal);
                    attachment = new TimelineAttachment(
                        relativePath,
                        attachmentMimeType,
                        sizeBytes,
                        ValidateSha256(wire.attachment.sha256, attachmentIsDirectory));
                    break;
            }

            return new TimelineMessage(wire.schema, id, kind, senderDeviceId, senderName,
                createdUtc, text, linkUrl, attachment, targets);
        }

        private DeliveryReceipt ReadAndValidateReceipt(string path, string expectedMessageId, string expectedDeviceId)
        {
            PathSafety.EnsureNoReparsePoints(SyncRoot, path);
            if (PathSafety.IsSyncConflictPath(path) || IsReparsePoint(path))
            {
                throw new TimelineProtocolException("Unsafe receipt path.");
            }

            ReceiptWire wire = ReadJsonFile<ReceiptWire>(path, TimelineProtocol.MaximumReceiptFileBytes);
            if (wire == null || wire.schema != TimelineProtocol.SchemaVersion)
            {
                throw new TimelineProtocolException("The receipt schema is missing or unsupported.");
            }

            string messageId = NormalizeMessageId(wire.messageId, "messageId");
            string deviceId = NormalizeDeviceId(wire.receiverDeviceId, "receiverDeviceId");
            if (!String.Equals(messageId, expectedMessageId, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(deviceId, expectedDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                throw new TimelineProtocolException("The receipt content does not match its immutable storage path.");
            }

            return new DeliveryReceipt(
                wire.schema,
                messageId,
                deviceId,
                ValidateDisplayText(wire.receiverName, TimelineProtocol.MaximumSenderNameBytes,
                    "receiverName", false),
                ParseUtcTimestamp(wire.receivedUtc, "receivedUtc"));
        }

        private static MessageWire ToWire(TimelineMessage message)
        {
            MessageWire wire = new MessageWire();
            wire.schema = message.Schema;
            wire.id = message.Id;
            wire.kind = KindToWire(message.Kind);
            wire.senderDeviceId = message.SenderDeviceId;
            wire.senderName = message.SenderName;
            wire.createdUtc = FormatUtc(message.CreatedUtc);
            wire.text = message.Text;
            wire.url = message.LinkUrl;
            wire.targetDeviceIds = CopyToArray(message.TargetDeviceIds);
            if (message.Attachment != null)
            {
                wire.attachment = new AttachmentWire
                {
                    relativePath = message.Attachment.RelativePath,
                    mimeType = message.Attachment.MimeType,
                    sizeBytes = message.Attachment.SizeBytes,
                    sha256 = message.Attachment.Sha256
                };
            }
            return wire;
        }

        private static ReceiptWire ToWire(DeliveryReceipt receipt)
        {
            return new ReceiptWire
            {
                schema = receipt.Schema,
                messageId = receipt.MessageId,
                receiverDeviceId = receipt.ReceiverDeviceId,
                receiverName = receipt.ReceiverName,
                receivedUtc = FormatUtc(receipt.ReceivedUtc)
            };
        }

        private static byte[] SerializeJson(object value, int maximumBytes)
        {
            JavaScriptSerializer serializer = CreateSerializer(maximumBytes);
            string json = serializer.Serialize(value);
            byte[] content = StrictUtf8.GetBytes(json);
            if (content.Length == 0 || content.Length > maximumBytes)
            {
                throw new TimelineProtocolException("The serialized JSON exceeds the protocol size limit.");
            }
            return content;
        }

        private static T ReadJsonFile<T>(string path, int maximumBytes)
        {
            byte[] content;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > maximumBytes || stream.Length > Int32.MaxValue)
                {
                    throw new TimelineProtocolException("The JSON file is empty or exceeds the protocol size limit.");
                }
                content = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < content.Length)
                {
                    int read = stream.Read(content, offset, content.Length - offset);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("The JSON file changed or ended while it was read.");
                    }
                    offset += read;
                }
            }

            string json;
            try
            {
                json = StrictUtf8.GetString(content);
            }
            catch (DecoderFallbackException exception)
            {
                throw new TimelineProtocolException("The JSON file is not valid UTF-8.", exception);
            }
            if (json.Length > 0 && json[0] == '\uFEFF')
            {
                json = json.Substring(1);
            }

            try
            {
                return CreateSerializer(maximumBytes).Deserialize<T>(json);
            }
            catch (Exception exception)
            {
                if (exception is InvalidOperationException || exception is ArgumentException)
                {
                    throw new TimelineProtocolException("The JSON file is malformed.", exception);
                }
                throw;
            }
        }

        private static JavaScriptSerializer CreateSerializer(int maximumBytes)
        {
            return new JavaScriptSerializer
            {
                MaxJsonLength = maximumBytes,
                RecursionLimit = 16
            };
        }

        private static void WriteImmutableFile(string finalPath, byte[] content)
        {
            if (File.Exists(finalPath))
            {
                byte[] existing = ReadRawFile(finalPath, content.Length);
                if (BytesEqual(existing, content))
                {
                    return;
                }
                throw new IOException("An immutable protocol file already exists with different content: " + finalPath);
            }

            string directory = Path.GetDirectoryName(finalPath);
            string tempPath = Path.Combine(directory,
                ".transporthub-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(content, 0, content.Length);
                    stream.Flush(true);
                }
                File.Move(tempPath, finalPath);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (IOException)
                {
                    // A stale temp file is never interpreted as a message or receipt.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        private static byte[] ReadRawFile(string path, int expectedMaximumBytes)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > expectedMaximumBytes || stream.Length > Int32.MaxValue)
                {
                    throw new IOException("The existing immutable file has unexpected content.");
                }
                byte[] result = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < result.Length)
                {
                    int read = stream.Read(result, offset, result.Length - offset);
                    if (read == 0)
                    {
                        throw new EndOfStreamException();
                    }
                    offset += read;
                }
                return result;
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (Object.ReferenceEquals(left, right))
            {
                return true;
            }
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }
            for (int index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }
            return true;
        }

        private static string ValidateDisplayText(string value, int maximumUtf8Bytes,
            string parameterName, bool allowWhiteSpace)
        {
            if (value == null || (!allowWhiteSpace && String.IsNullOrWhiteSpace(value)))
            {
                throw new ArgumentException("A non-empty value is required.", parameterName);
            }

            int byteCount;
            try
            {
                byteCount = StrictUtf8.GetByteCount(value);
            }
            catch (EncoderFallbackException exception)
            {
                throw new ArgumentException("The value contains invalid Unicode.", parameterName, exception);
            }
            if (byteCount == 0 || byteCount > maximumUtf8Bytes)
            {
                throw new ArgumentException("The UTF-8 value exceeds the protocol size limit.", parameterName);
            }
            if (value.IndexOf('\0') >= 0)
            {
                throw new ArgumentException("NUL characters are not allowed.", parameterName);
            }
            foreach (char character in value)
            {
                if (Char.IsControl(character) && character != '\r' && character != '\n' && character != '\t')
                {
                    throw new ArgumentException("Unsupported control characters are not allowed.", parameterName);
                }
            }
            return value;
        }

        private static string ValidateHttpUrl(string url)
        {
            string value = ValidateDisplayText(url, TimelineProtocol.MaximumUrlBytes, "url", false).Trim();
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                !(String.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                  String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
                String.IsNullOrEmpty(uri.Host) || !String.IsNullOrEmpty(uri.UserInfo))
            {
                throw new ArgumentException("Only absolute HTTP/HTTPS URLs without embedded credentials are allowed.", "url");
            }
            return value;
        }

        private static string ValidateMimeType(string mimeType)
        {
            string value = String.IsNullOrWhiteSpace(mimeType)
                ? "application/octet-stream"
                : mimeType.Trim();
            if (!MimeTypePattern.IsMatch(value))
            {
                throw new ArgumentException("The MIME type is invalid.", "mimeType");
            }
            return value.ToLowerInvariant();
        }

        private static string ValidateSha256(string sha256, bool allowEmpty)
        {
            if (String.IsNullOrWhiteSpace(sha256))
            {
                if (allowEmpty)
                {
                    return String.Empty;
                }
                throw new ArgumentException("A SHA-256 hash is required.", "sha256");
            }
            string value = sha256.Trim().ToUpperInvariant();
            if (!Sha256Pattern.IsMatch(value))
            {
                throw new ArgumentException("The SHA-256 hash must contain exactly 64 hexadecimal characters.", "sha256");
            }
            return value;
        }

        private static string NormalizeDeviceId(string deviceId, string parameterName)
        {
            if (String.IsNullOrWhiteSpace(deviceId))
            {
                throw new ArgumentException("A full Syncthing device ID is required.", parameterName);
            }
            string value = deviceId.Trim().ToUpperInvariant();
            if (!SyncthingDeviceIdPattern.IsMatch(value))
            {
                throw new ArgumentException("The value is not a full Syncthing device ID.", parameterName);
            }
            return value;
        }

        private static string NormalizeMessageId(string messageId, string parameterName)
        {
            if (String.IsNullOrWhiteSpace(messageId))
            {
                throw new ArgumentException("A message ID is required.", parameterName);
            }
            string value = messageId.Trim().ToUpperInvariant();
            if (PathSafety.IsSyncConflictPath(value) ||
                !(UlidPattern.IsMatch(value) || GuidPattern.IsMatch(value)))
            {
                throw new ArgumentException("The message ID is not a supported ULID or UUID.", parameterName);
            }
            return value;
        }

        private static List<string> NormalizeTargetDevices(IEnumerable<string> devices, string senderDeviceId)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (devices != null)
            {
                foreach (string device in devices)
                {
                    string normalized = NormalizeDeviceId(device, "targetDeviceIds");
                    if (!String.IsNullOrEmpty(senderDeviceId) &&
                        String.Equals(normalized, senderDeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!seen.Add(normalized))
                    {
                        throw new ArgumentException("Target device IDs must be unique.", "targetDeviceIds");
                    }
                    result.Add(normalized);
                    if (result.Count > TimelineProtocol.MaximumTargetDevices)
                    {
                        throw new ArgumentException("There are too many target devices.", "targetDeviceIds");
                    }
                }
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private DateTime GetUtcNow()
        {
            DateTime value = _utcNow();
            if (value.Kind == DateTimeKind.Local)
            {
                value = value.ToUniversalTime();
            }
            else if (value.Kind == DateTimeKind.Unspecified)
            {
                value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
            }
            return value;
        }

        private static DateTime ParseUtcTimestamp(string value, string fieldName)
        {
            DateTime parsed;
            if (String.IsNullOrEmpty(value) || !value.EndsWith("Z", StringComparison.Ordinal) ||
                !DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
            {
                throw new TimelineProtocolException("The " + fieldName + " timestamp is not canonical UTC.");
            }
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        private static string FormatUtc(DateTime value)
        {
            return value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        private static void EnsureAbsent(string otherText, object otherObject, string message)
        {
            if (!String.IsNullOrEmpty(otherText) || otherObject != null)
            {
                throw new TimelineProtocolException(message);
            }
        }

        private static bool IsExpectedReadFailure(Exception exception)
        {
            return exception is TimelineProtocolException ||
                exception is ArgumentException ||
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is NotSupportedException;
        }

        private static bool IsReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        private void AddRejection(ICollection<TimelineReadRejection> rejections, string path, string reason)
        {
            if (rejections == null)
            {
                return;
            }
            string relative;
            try
            {
                relative = PathSafety.GetRelativePathUnderRoot(MessagesRoot, path);
            }
            catch
            {
                relative = Path.GetFileName(path);
            }
            rejections.Add(new TimelineReadRejection(relative, reason));
        }

        private static int CompareMessagesAscending(TimelineMessage left, TimelineMessage right)
        {
            int timeComparison = DateTime.Compare(left.CreatedUtc, right.CreatedUtc);
            return timeComparison != 0
                ? timeComparison
                : StringComparer.Ordinal.Compare(left.Id, right.Id);
        }

        private static string KindToWire(TimelineMessageKind kind)
        {
            switch (kind)
            {
                case TimelineMessageKind.Text: return "text";
                case TimelineMessageKind.Link: return "link";
                case TimelineMessageKind.Attachment: return "attachment";
                default: throw new ArgumentOutOfRangeException("kind");
            }
        }

        private static string[] CopyToArray(IReadOnlyList<string> source)
        {
            string[] result = new string[source.Count];
            for (int index = 0; index < source.Count; index++)
            {
                result[index] = source[index];
            }
            return result;
        }

        private sealed class MessageWire
        {
            public int schema { get; set; }
            public string id { get; set; }
            public string kind { get; set; }
            public string senderDeviceId { get; set; }
            public string senderName { get; set; }
            public string createdUtc { get; set; }
            public string text { get; set; }
            public string url { get; set; }
            public AttachmentWire attachment { get; set; }
            public string[] targetDeviceIds { get; set; }
        }

        private sealed class AttachmentWire
        {
            public string relativePath { get; set; }
            public string mimeType { get; set; }
            public long sizeBytes { get; set; }
            public string sha256 { get; set; }
        }

        private sealed class ReceiptWire
        {
            public int schema { get; set; }
            public string messageId { get; set; }
            public string receiverDeviceId { get; set; }
            public string receiverName { get; set; }
            public string receivedUtc { get; set; }
        }

        private sealed class DeviceWire
        {
            public int schema { get; set; }
            public string deviceId { get; set; }
            public string deviceName { get; set; }
        }
    }
}

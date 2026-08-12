using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using TransportHub.Desktop.Core;
using TransportHub.Desktop.Models;
using TransportHub.Desktop.Services;

namespace TransportHub.Desktop.Testing
{
    internal static class SelfTest
    {
        private const string DeviceA = "AAAAAAA-AAAAAAA-AAAAAAA-AAAAAAA-AAAAAAA-AAAAAAA-AAAAAAA-AAAAAAA";
        private const string DeviceB = "BBBBBBB-BBBBBBB-BBBBBBB-BBBBBBB-BBBBBBB-BBBBBBB-BBBBBBB-BBBBBBB";

        internal static int Run()
        {
            var lines = new List<string>();
            var root = Path.Combine(Path.GetTempPath(), "TransportHub-self-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                RunTimelineScenario(root, lines);
                RunTransferScenario(root, lines);
                RunPathSafetyScenario(root, lines);
                lines.Add("PASS: all TransportHub self-tests completed");
                WriteResult(lines, true);
                return 0;
            }
            catch (Exception exception)
            {
                lines.Add("FAIL: " + exception);
                WriteResult(lines, false);
                return 1;
            }
            finally
            {
                TryDeleteOwnedTemporaryDirectory(root);
            }
        }

        private static void RunTimelineScenario(string root, ICollection<string> lines)
        {
            var now = new DateTime(2026, 8, 13, 1, 2, 3, DateTimeKind.Utc);
            var ids = new Queue<string>(new[]
            {
                "00000000000000000000000000000001",
                "00000000000000000000000000000002",
                "00000000000000000000000000000003",
                "00000000000000000000000000000004"
            });
            var sender = new TimelineStore(root, DeviceA, "测试电脑 A", () => now, () => ids.Dequeue());

            var text = sender.CreateText("第一条文字", new[] { DeviceB, DeviceA });
            Assert(text.Kind == TimelineMessageKind.Text, "Text kind was not preserved.");
            Assert(text.TargetDeviceIds.Count == 1 && text.TargetDeviceIds[0] == DeviceB,
                "The sender device was not removed from targets.");
            AssertThrows<ArgumentException>(() => sender.CreateText("重复目标", new[] { DeviceB, DeviceB }),
                "Duplicate target devices must be rejected.");

            var link = sender.CreateLink("https://example.com/a?b=1", "示例链接", new[] { DeviceB });
            Assert(link.Kind == TimelineMessageKind.Link && link.LinkUrl.StartsWith("https://", StringComparison.Ordinal),
                "HTTP link was not stored.");

            var deviceFolder = Path.Combine(root, "DEVICE-A");
            Directory.CreateDirectory(deviceFolder);
            var attachmentPath = Path.Combine(deviceFolder, "hello.txt");
            var attachmentBytes = Encoding.UTF8.GetBytes("TransportHub attachment");
            File.WriteAllBytes(attachmentPath, attachmentBytes);
            var relative = PathSafety.GetRelativePathUnderRoot(root, attachmentPath);
            var attachment = sender.CreateAttachment(
                relative,
                "text/plain",
                attachmentBytes.LongLength,
                Sha256(attachmentBytes),
                new[] { DeviceB });
            Assert(attachment.Attachment != null && attachment.Attachment.RelativePath == "DEVICE-A/hello.txt",
                "Attachment path was not normalized.");

            var loaded = sender.LoadRecentMessages(20);
            Assert(loaded.Count == 3, "Expected three immutable timeline messages.");
            Assert(loaded.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3,
                "Message IDs were not unique after loading.");

            var receiver = new TimelineStore(root, DeviceB, "测试电脑 B");
            var knownDevices = receiver.LoadKnownDeviceIds();
            Assert(knownDevices.Contains(DeviceA) && knownDevices.Contains(DeviceB),
                "Synchronized device registry did not discover both devices.");
            var receipt = receiver.CreateDeliveryReceipt(text.Id);
            var receiptAgain = receiver.CreateDeliveryReceipt(text.Id);
            Assert(receipt.ReceiverDeviceId == DeviceB && receiptAgain.ReceivedUtc == receipt.ReceivedUtc,
                "Delivery receipt creation was not idempotent.");
            var summary = sender.GetDeliverySummary(text);
            Assert(summary.IsDeliveredToAll && summary.DeliveredCount == 1 && summary.TargetCount == 1,
                "Delivery summary did not count the target receipt.");

            AssertThrows<ArgumentException>(() => sender.CreateLink("file:///C:/Windows/win.ini", null, new[] { DeviceB }),
                "Non-HTTP links must be rejected.");
            AssertThrows<ArgumentException>(() => sender.CreateAttachment("..\\outside.txt", "text/plain", 0, new string('0', 64), new[] { DeviceB }),
                "Attachment traversal must be rejected.");

            var malformedDirectory = Path.Combine(sender.MessagesRoot, DeviceB, "2026-08");
            Directory.CreateDirectory(malformedDirectory);
            File.WriteAllText(Path.Combine(malformedDirectory, "00000000000000000000000000000009.json"), "{not-json", Encoding.UTF8);
            var rejections = new List<TimelineReadRejection>();
            var validAfterMalformed = sender.LoadRecentMessages(20, rejections);
            Assert(validAfterMalformed.Count == 3 && rejections.Count >= 1,
                "Malformed synchronized metadata was not isolated from valid messages.");
            lines.Add("PASS: timeline, attachment, receipt, and malformed-file isolation");
        }

        private static void RunPathSafetyScenario(string root, ICollection<string> lines)
        {
            Assert(PathSafety.NormalizeRelativePath("PC-A\\图像.png") == "PC-A/图像.png",
                "Relative path separator normalization failed.");
            AssertThrows<ArgumentException>(() => PathSafety.NormalizeRelativePath("C:\\secret.txt"),
                "Rooted paths must be rejected.");
            AssertThrows<ArgumentException>(() => PathSafety.NormalizeRelativePath("PC-A\\..\\secret.txt"),
                "Traversal segments must be rejected.");
            AssertThrows<ArgumentException>(() => PathSafety.NormalizeRelativePath("PC-A\\name:stream"),
                "Alternate data streams must be rejected.");
            AssertThrows<ArgumentException>(() => PathSafety.NormalizeRelativePath("PC-A\\x.sync-conflict-1.txt"),
                "Syncthing conflict copies must be rejected as protocol attachments.");
            AssertThrows<ArgumentException>(() => PathSafety.ResolveUnderRoot(root, ".transporthub/messages.json"),
                "Protocol metadata must not be exposed as an attachment.");
            lines.Add("PASS: rooted, traversal, ADS, conflict, and metadata path rejection");
        }

        private static void RunTransferScenario(string root, ICollection<string> lines)
        {
            var transferRoot = Path.Combine(root, "transfer-root");
            var machineFolder = Path.Combine(transferRoot, "SELF-TEST-PC");
            var staging = Path.Combine(root, "transfer-staging");
            var sourceRoot = Path.Combine(root, "sources");
            Directory.CreateDirectory(sourceRoot);
            var context = SyncthingContext.CreateForTesting(transferRoot, machineFolder, staging, DeviceA);
            var service = new TransferService(context);

            var sourceFile = Path.Combine(sourceRoot, "report.txt");
            var bytes = Encoding.UTF8.GetBytes("transfer integration test");
            File.WriteAllBytes(sourceFile, bytes);
            var first = service.SendPathAsync(sourceFile, CancellationToken.None).GetAwaiter().GetResult();
            Assert(File.Exists(sourceFile), "Sending a file removed the source.");
            Assert(File.Exists(first.AbsolutePath) && first.Size == bytes.LongLength && first.Sha256 == Sha256(bytes).ToLowerInvariant(),
                "File transfer result or hash was invalid.");
            var second = service.SendPathAsync(sourceFile, CancellationToken.None).GetAwaiter().GetResult();
            Assert(!String.Equals(first.AbsolutePath, second.AbsolutePath, StringComparison.OrdinalIgnoreCase) &&
                   Path.GetFileName(second.AbsolutePath).Contains("(2)"),
                "File collision naming did not preserve both copies.");

            var sourceFolder = Path.Combine(sourceRoot, "资料目录");
            Directory.CreateDirectory(Path.Combine(sourceFolder, "nested"));
            Directory.CreateDirectory(Path.Combine(sourceFolder, "空目录"));
            File.WriteAllText(Path.Combine(sourceFolder, "nested", "item.txt"), "nested file", Encoding.UTF8);
            var folder = service.SendPathAsync(sourceFolder, CancellationToken.None).GetAwaiter().GetResult();
            Assert(Directory.Exists(folder.AbsolutePath) && File.Exists(Path.Combine(folder.AbsolutePath, "nested", "item.txt")),
                "Directory transfer did not preserve its hierarchy.");
            Assert(Directory.Exists(Path.Combine(folder.AbsolutePath, "空目录")),
                "Directory transfer dropped an empty directory.");
            Assert(folder.MimeType == "inode/directory" && String.IsNullOrEmpty(folder.Sha256),
                "Directory transfer metadata was invalid.");

            var image = service.SendBytesAsync(new byte[] { 1, 2, 3, 4 }, "clipboard.bin", "application/octet-stream", CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(File.Exists(image.AbsolutePath) && new FileInfo(image.AbsolutePath).Length == 4,
                "In-memory payload transfer failed.");
            Assert(!Directory.EnumerateFileSystemEntries(staging).Any(),
                "Successful transfers left staging artifacts behind.");
            lines.Add("PASS: file, collision, directory, byte payload, hash, and staging transfer flow");
        }

        private static string Sha256(byte[] bytes)
        {
            using (var algorithm = SHA256.Create())
            {
                return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", String.Empty);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void AssertThrows<T>(Action action, string message) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }
            throw new InvalidOperationException(message);
        }

        private static void WriteResult(IEnumerable<string> lines, bool success)
        {
            var content = String.Join(Environment.NewLine, lines) + Environment.NewLine;
            Debug.WriteLine(content);
            var requested = Environment.GetEnvironmentVariable("TRANSPORTHUB_SELF_TEST_LOG");
            var path = String.IsNullOrWhiteSpace(requested)
                ? Path.Combine(Path.GetTempPath(), "TransportHub-self-test.log")
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(requested));
            try
            {
                File.WriteAllText(path, content, new UTF8Encoding(false));
            }
            catch (Exception)
            {
                if (success)
                {
                    throw;
                }
            }
        }

        private static void TryDeleteOwnedTemporaryDirectory(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(full).StartsWith("TransportHub-self-test-", StringComparison.Ordinal) &&
                    Directory.Exists(full))
                {
                    Directory.Delete(full, true);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}

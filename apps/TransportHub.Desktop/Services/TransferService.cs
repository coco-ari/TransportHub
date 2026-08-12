using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TransportHub.Desktop.Models;

namespace TransportHub.Desktop.Services
{
    internal sealed class TransferProgressInfo
    {
        internal string DisplayName { get; set; }
        internal long BytesCopied { get; set; }
        internal long TotalBytes { get; set; }
        internal int Percent { get { return TotalBytes <= 0 ? 0 : (int)Math.Min(100, BytesCopied * 100L / TotalBytes); } }
    }

    internal sealed class TransferResult
    {
        internal string AbsolutePath { get; set; }
        internal string RelativePath { get; set; }
        internal string DisplayName { get; set; }
        internal string AttachmentType { get; set; }
        internal string MimeType { get; set; }
        internal long Size { get; set; }
        internal string Sha256 { get; set; }
    }

    internal sealed class TransferService
    {
        private const int BufferSize = 1024 * 1024;
        private readonly SyncthingContext _context;
        private readonly SemaphoreSlim _singleTransfer = new SemaphoreSlim(1, 1);

        internal TransferService(SyncthingContext context)
        {
            _context = context ?? throw new ArgumentNullException("context");
        }

        internal event EventHandler<TransferProgressInfo> ProgressChanged;

        internal async Task<TransferResult> SendPathAsync(string sourcePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("源路径不能为空。", "sourcePath");
            }
            sourcePath = Path.GetFullPath(sourcePath);
            var isFile = File.Exists(sourcePath);
            var isDirectory = Directory.Exists(sourcePath);
            if (!isFile && !isDirectory)
            {
                throw new FileNotFoundException("源文件或文件夹不存在。", sourcePath);
            }

            await _singleTransfer.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return isFile
                    ? await CopyFileAsync(sourcePath, cancellationToken).ConfigureAwait(false)
                    : await CopyDirectoryAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _singleTransfer.Release();
            }
        }

        internal async Task<TransferResult> SendBytesAsync(byte[] bytes, string suggestedName, string mimeType, CancellationToken cancellationToken)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new ArgumentException("图片内容为空。", "bytes");
            }
            if (bytes.LongLength > 100L * 1024L * 1024L)
            {
                throw new InvalidOperationException("粘贴的图片超过 100 MB 限制。");
            }

            await _singleTransfer.WaitAsync(cancellationToken).ConfigureAwait(false);
            string stagingFile = null;
            try
            {
                var name = SanitizeName(suggestedName);
                if (string.IsNullOrWhiteSpace(Path.GetExtension(name)))
                {
                    name += ".png";
                }
                var destination = GetUniqueDestination(_context.MachineFolder, name, false);
                stagingFile = Path.Combine(_context.StagingPath, Guid.NewGuid().ToString("N") + ".part");
                Directory.CreateDirectory(_context.StagingPath);

                using (var stream = new FileStream(stagingFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                    stream.Flush(true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(stagingFile, destination);
                stagingFile = null;
                RaiseProgress(new TransferProgressInfo { DisplayName = name, BytesCopied = bytes.LongLength, TotalBytes = bytes.LongLength });
                return CreateFileResult(destination, bytes.LongLength, ComputeSha256(bytes), mimeType);
            }
            finally
            {
                if (stagingFile != null)
                {
                    TryDeleteFile(stagingFile);
                }
                _singleTransfer.Release();
            }
        }

        private async Task<TransferResult> CopyFileAsync(string sourcePath, CancellationToken cancellationToken)
        {
            RejectReparsePoint(sourcePath);
            var sourceInfo = new FileInfo(sourcePath);
            if (sourceInfo.Length > TransportHub.Desktop.Core.TimelineProtocol.MaximumAttachmentBytes)
            {
                throw new InvalidOperationException("文件超过 TransportHub 支持的大小上限。");
            }
            var destination = GetUniqueDestination(_context.MachineFolder, SanitizeName(sourceInfo.Name), false);
            var stagingFile = Path.Combine(_context.StagingPath, Guid.NewGuid().ToString("N") + ".part");
            Directory.CreateDirectory(_context.StagingPath);
            string hash;
            try
            {
                hash = await CopyFileContentsAsync(sourcePath, stagingFile, sourceInfo.Length, sourceInfo.Name, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var stagedLength = new FileInfo(stagingFile).Length;
                if (stagedLength != sourceInfo.Length)
                {
                    throw new IOException("源文件在发送过程中发生变化，请重试。");
                }
                File.Move(stagingFile, destination);
            }
            catch
            {
                TryDeleteFile(stagingFile);
                throw;
            }
            return CreateFileResult(destination, sourceInfo.Length, hash, GuessMimeType(sourceInfo.Extension));
        }

        private async Task<TransferResult> CopyDirectoryAsync(string sourcePath, CancellationToken cancellationToken)
        {
            RejectReparsePoint(sourcePath);
            if (IsSameOrParent(sourcePath, _context.MachineFolder) || IsSameOrParent(sourcePath, _context.StagingPath))
            {
                throw new InvalidOperationException("不能投递包含 TransportHub 自身同步目录或暂存目录的上级文件夹。");
            }

            var sourceDirectory = new DirectoryInfo(sourcePath);
            var files = EnumerateSafeFiles(sourceDirectory).ToList();
            var total = files.Sum(file => file.Length);
            if (total > TransportHub.Desktop.Core.TimelineProtocol.MaximumAttachmentBytes)
            {
                throw new InvalidOperationException("文件夹总大小超过 TransportHub 支持的上限。");
            }
            var destination = GetUniqueDestination(_context.MachineFolder, SanitizeName(sourceDirectory.Name), true);
            var stagingDirectory = Path.Combine(_context.StagingPath, Guid.NewGuid().ToString("N") + ".folder");
            var copied = 0L;
            try
            {
                Directory.CreateDirectory(stagingDirectory);
                foreach (var directory in EnumerateSafeDirectories(sourceDirectory))
                {
                    var relativeDirectory = GetRelativePath(sourceDirectory.FullName, directory.FullName);
                    Directory.CreateDirectory(Path.Combine(stagingDirectory, relativeDirectory));
                }
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = GetRelativePath(sourceDirectory.FullName, file.FullName);
                    var target = Path.Combine(stagingDirectory, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    await CopyFileContentsAsync(
                        file.FullName,
                        target,
                        file.Length,
                        sourceDirectory.Name,
                        cancellationToken,
                        value =>
                        {
                            RaiseProgress(new TransferProgressInfo
                            {
                                DisplayName = sourceDirectory.Name,
                                BytesCopied = copied + value,
                                TotalBytes = total
                            });
                        }).ConfigureAwait(false);
                    copied += file.Length;
                }
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(stagingDirectory, destination);
            }
            catch
            {
                TryDeleteDirectory(stagingDirectory);
                throw;
            }

            RaiseProgress(new TransferProgressInfo { DisplayName = sourceDirectory.Name, BytesCopied = total, TotalBytes = total });
            return new TransferResult
            {
                AbsolutePath = destination,
                RelativePath = GetRelativePath(_context.RootPath, destination),
                DisplayName = Path.GetFileName(destination),
                AttachmentType = "folder",
                MimeType = "inode/directory",
                Size = total,
                Sha256 = string.Empty
            };
        }

        private async Task<string> CopyFileContentsAsync(
            string source,
            string destination,
            long total,
            string displayName,
            CancellationToken cancellationToken,
            Action<long> progressOverride = null)
        {
            var copied = 0L;
            using (var sha256 = SHA256.Create())
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[BufferSize];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }
                    await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                    sha256.TransformBlock(buffer, 0, read, null, 0);
                    copied += read;
                    if (progressOverride != null)
                    {
                        progressOverride(copied);
                    }
                    else
                    {
                        RaiseProgress(new TransferProgressInfo { DisplayName = displayName, BytesCopied = copied, TotalBytes = total });
                    }
                }
                sha256.TransformFinalBlock(new byte[0], 0, 0);
                output.Flush(true);
                return BitConverter.ToString(sha256.Hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private IEnumerable<FileInfo> EnumerateSafeFiles(DirectoryInfo root)
        {
            var pending = new Stack<DirectoryInfo>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                RejectReparsePoint(directory.FullName);
                foreach (var file in directory.GetFiles())
                {
                    RejectReparsePoint(file.FullName);
                    yield return file;
                }
                foreach (var child in directory.GetDirectories())
                {
                    RejectReparsePoint(child.FullName);
                    pending.Push(child);
                }
            }
        }

        private IEnumerable<DirectoryInfo> EnumerateSafeDirectories(DirectoryInfo root)
        {
            var pending = new Stack<DirectoryInfo>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                RejectReparsePoint(directory.FullName);
                foreach (var child in directory.GetDirectories())
                {
                    RejectReparsePoint(child.FullName);
                    yield return child;
                    pending.Push(child);
                }
            }
        }

        private TransferResult CreateFileResult(string path, long size, string hash, string mimeType)
        {
            return new TransferResult
            {
                AbsolutePath = path,
                RelativePath = GetRelativePath(_context.RootPath, path),
                DisplayName = Path.GetFileName(path),
                AttachmentType = IsImageExtension(Path.GetExtension(path)) ? "image" : "file",
                MimeType = string.IsNullOrWhiteSpace(mimeType) ? GuessMimeType(Path.GetExtension(path)) : mimeType,
                Size = size,
                Sha256 = hash
            };
        }

        private static string GetUniqueDestination(string directory, string requestedName, bool directoryTarget)
        {
            Directory.CreateDirectory(directory);
            requestedName = SanitizeName(requestedName);
            var extension = directoryTarget ? string.Empty : Path.GetExtension(requestedName);
            var stem = directoryTarget ? requestedName : Path.GetFileNameWithoutExtension(requestedName);
            for (var index = 1; index < 10000; index++)
            {
                var name = index == 1 ? stem + extension : stem + " (" + index + ")" + extension;
                var candidate = Path.Combine(directory, name);
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
            throw new IOException("无法为投递项目生成不重复的文件名。");
        }

        private static string SanitizeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string((name ?? string.Empty).Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = "投递项目-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            }
            var stem = Path.GetFileNameWithoutExtension(sanitized).ToUpperInvariant();
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            };
            if (reserved.Contains(stem))
            {
                sanitized = "_" + sanitized;
            }
            return sanitized;
        }

        private static void RejectReparsePoint(string path)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("不支持投递符号链接、目录联接或其他重解析点：" + Path.GetFileName(path));
            }
        }

        private static bool IsSameOrParent(string parent, string child)
        {
            var normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var normalizedChild = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return normalizedChild.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
        }

        internal static string GetRelativePath(string root, string path)
        {
            var rootUri = new Uri(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var pathUri = new Uri(Path.GetFullPath(path));
            var relative = Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                throw new InvalidOperationException("目标路径不在 TransportHub 同步目录内。");
            }
            return relative;
        }

        private static bool IsImageExtension(string extension)
        {
            return new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff" }
                .Contains((extension ?? string.Empty).ToLowerInvariant());
        }

        private static string GuessMimeType(string extension)
        {
            switch ((extension ?? string.Empty).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".bmp": return "image/bmp";
                case ".pdf": return "application/pdf";
                case ".zip": return "application/zip";
                case ".txt": return "text/plain";
                case ".mp4": return "video/mp4";
                default: return "application/octet-stream";
            }
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using (var sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup of a unique staging file only.
            }
        }

        private void TryDeleteDirectory(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var staging = Path.GetFullPath(_context.StagingPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full.StartsWith(staging, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
                {
                    Directory.Delete(full, true);
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup of a validated unique staging directory only.
            }
        }

        private void RaiseProgress(TransferProgressInfo progress)
        {
            var handler = ProgressChanged;
            if (handler != null)
            {
                handler(this, progress);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TransportHub.Desktop.Interop
{
    public enum ClipboardPayloadKind
    {
        FileDrop,
        Image,
        Url,
        Text
    }

    public enum ClipboardReadStatus
    {
        Success,
        NotStaThread,
        NotForeground,
        Empty,
        Unsupported,
        ClipboardUnavailable,
        InvalidData,
        TooLarge
    }

    /// <summary>
    /// An immutable snapshot of supported clipboard data. No member retains an
    /// IDataObject, Image, Stream, HBITMAP, or other clipboard-owned resource.
    /// </summary>
    public sealed class ClipboardPayload
    {
        private static readonly ReadOnlyCollection<string> NoFiles =
            new ReadOnlyCollection<string>(new string[0]);

        private readonly byte[] imageBytes;

        private ClipboardPayload(
            ClipboardPayloadKind kind,
            IList<string> filePaths,
            byte[] imageBytes,
            string imageMediaType,
            string suggestedFileExtension,
            Uri url,
            string text)
        {
            Kind = kind;
            FilePaths = filePaths == null
                ? NoFiles
                : new ReadOnlyCollection<string>(filePaths.ToArray());
            this.imageBytes = imageBytes == null ? null : (byte[])imageBytes.Clone();
            ImageMediaType = imageMediaType;
            SuggestedFileExtension = suggestedFileExtension;
            Url = url;
            Text = text;
        }

        public ClipboardPayloadKind Kind { get; private set; }

        public IReadOnlyList<string> FilePaths { get; private set; }

        public int ImageByteCount
        {
            get { return imageBytes == null ? 0 : imageBytes.Length; }
        }

        public string ImageMediaType { get; private set; }

        public string SuggestedFileExtension { get; private set; }

        public Uri Url { get; private set; }

        /// <summary>
        /// Contains the original clipboard text. For a Url payload this is the
        /// trimmed URL spelling that the user copied.
        /// </summary>
        public string Text { get; private set; }

        public byte[] GetImageBytes()
        {
            return imageBytes == null ? null : (byte[])imageBytes.Clone();
        }

        internal static ClipboardPayload FromFiles(IList<string> filePaths)
        {
            return new ClipboardPayload(
                ClipboardPayloadKind.FileDrop,
                filePaths,
                null,
                null,
                null,
                null,
                null);
        }

        internal static ClipboardPayload FromImage(byte[] bytes)
        {
            return new ClipboardPayload(
                ClipboardPayloadKind.Image,
                null,
                bytes,
                "image/png",
                ".png",
                null,
                null);
        }

        internal static ClipboardPayload FromUrl(Uri url, string originalText)
        {
            return new ClipboardPayload(
                ClipboardPayloadKind.Url,
                null,
                null,
                null,
                null,
                url,
                originalText);
        }

        internal static ClipboardPayload FromText(string text)
        {
            return new ClipboardPayload(
                ClipboardPayloadKind.Text,
                null,
                null,
                null,
                null,
                null,
                text);
        }
    }

    public sealed class ClipboardReadResult
    {
        private ClipboardReadResult(ClipboardReadStatus status, ClipboardPayload payload, string message)
        {
            Status = status;
            Payload = payload;
            Message = message;
        }

        public ClipboardReadStatus Status { get; private set; }

        public ClipboardPayload Payload { get; private set; }

        public string Message { get; private set; }

        public bool IsSuccess
        {
            get { return Status == ClipboardReadStatus.Success && Payload != null; }
        }

        internal static ClipboardReadResult Success(ClipboardPayload payload)
        {
            return new ClipboardReadResult(ClipboardReadStatus.Success, payload, null);
        }

        internal static ClipboardReadResult Failure(ClipboardReadStatus status, string message)
        {
            return new ClipboardReadResult(status, null, message);
        }
    }

    /// <summary>
    /// Reads one clipboard snapshot only when explicitly called by this process's
    /// foreground window. This class does not monitor or retain the clipboard.
    /// </summary>
    public static class ClipboardPayloadReader
    {
        private const int CfDibV5 = 17;
        private const int MaxFileDropCount = 4096;
        private const int MaxPngByteCount = 50 * 1024 * 1024;
        private const int MaxDibByteCount = 128 * 1024 * 1024;
        private const int MaxTextCharacterCount = 1024 * 1024;
        private const long MaxImagePixelCount = 40L * 1000L * 1000L;

        private static readonly byte[] PngSignature =
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A
        };

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

        /// <summary>
        /// Reads a supported payload if the caller is on an STA thread and a
        /// window owned by this process is currently in the foreground.
        /// </summary>
        /// <param name="requestingWindowHandle">
        /// A valid HWND owned by the current process. It is used as an explicit
        /// proof that a foreground UI action initiated this read.
        /// </param>
        public static ClipboardReadResult ReadForForegroundWindow(IntPtr requestingWindowHandle)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                return ClipboardReadResult.Failure(
                    ClipboardReadStatus.NotStaThread,
                    "剪贴板只能从 STA 界面线程读取。");
            }

            if (!IsCurrentProcessForeground(requestingWindowHandle))
            {
                return ClipboardReadResult.Failure(
                    ClipboardReadStatus.NotForeground,
                    "只有当前台 TransportHub 窗口中发生主动粘贴时才能读取剪贴板。");
            }

            try
            {
                var dataObject = Clipboard.GetDataObject();
                if (dataObject == null)
                {
                    return ClipboardReadResult.Failure(ClipboardReadStatus.Empty, "剪贴板为空。");
                }

                var fileResult = ReadFileDrop(dataObject);
                if (fileResult != null)
                {
                    return fileResult;
                }

                var pngResult = ReadRegisteredPng(dataObject);
                if (pngResult != null)
                {
                    return pngResult;
                }

                var bitmapResult = ReadBitmapOrDib(dataObject);
                if (bitmapResult != null)
                {
                    return bitmapResult;
                }

                var textResult = ReadTextOrUrl(dataObject);
                if (textResult != null)
                {
                    return textResult;
                }

                var formats = dataObject.GetFormats(false);
                return ClipboardReadResult.Failure(
                    formats == null || formats.Length == 0
                        ? ClipboardReadStatus.Empty
                        : ClipboardReadStatus.Unsupported,
                    formats == null || formats.Length == 0
                        ? "剪贴板为空。"
                        : "剪贴板中没有受支持的文件、图片、网址或文字。");
            }
            catch (ExternalException exception)
            {
                return ClipboardReadResult.Failure(
                    ClipboardReadStatus.ClipboardUnavailable,
                    "剪贴板正被其他程序占用：" + exception.Message);
            }
            catch (ThreadStateException exception)
            {
                return ClipboardReadResult.Failure(
                    ClipboardReadStatus.NotStaThread,
                    "剪贴板线程状态无效：" + exception.Message);
            }
        }

        public static bool TryReadForForegroundWindow(
            IntPtr requestingWindowHandle,
            out ClipboardPayload payload,
            out ClipboardReadStatus status,
            out string message)
        {
            var result = ReadForForegroundWindow(requestingWindowHandle);
            payload = result.Payload;
            status = result.Status;
            message = result.Message;
            return result.IsSuccess;
        }

        private static bool IsCurrentProcessForeground(IntPtr requestingWindowHandle)
        {
            if (requestingWindowHandle == IntPtr.Zero)
            {
                return false;
            }

            uint requestingProcessId;
            if (GetWindowThreadProcessId(requestingWindowHandle, out requestingProcessId) == 0 ||
                requestingProcessId != (uint)System.Diagnostics.Process.GetCurrentProcess().Id)
            {
                return false;
            }

            var foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
            {
                return false;
            }

            uint foregroundProcessId;
            return GetWindowThreadProcessId(foregroundWindow, out foregroundProcessId) != 0 &&
                   foregroundProcessId == requestingProcessId;
        }

        private static ClipboardReadResult ReadFileDrop(IDataObject dataObject)
        {
            if (!dataObject.GetDataPresent(DataFormats.FileDrop, false))
            {
                return null;
            }

            var value = dataObject.GetData(DataFormats.FileDrop, false) as string[];
            if (value == null || value.Length == 0)
            {
                return ClipboardReadResult.Failure(
                    ClipboardReadStatus.InvalidData,
                    "剪贴板声明了文件列表，但没有可读取的路径。");
            }

            if (value.Length > MaxFileDropCount)
            {
                return ClipboardReadResult.Failure(
                    ClipboardReadStatus.TooLarge,
                    "一次粘贴的文件数量过多。");
            }

            var paths = new List<string>(value.Length);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in value)
            {
                if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || !Path.IsPathRooted(path))
                {
                    continue;
                }

                try
                {
                    var fullPath = Path.GetFullPath(path);
                    if (seen.Add(fullPath))
                    {
                        paths.Add(fullPath);
                    }
                }
                catch (Exception exception) when (
                    exception is ArgumentException ||
                    exception is NotSupportedException ||
                    exception is PathTooLongException)
                {
                    // Ignore malformed paths while retaining other valid entries.
                }
            }

            if (paths.Count == 0)
            {
                return ClipboardReadResult.Failure(
                    ClipboardReadStatus.InvalidData,
                    "剪贴板文件列表不包含有效的绝对路径。");
            }

            return ClipboardReadResult.Success(ClipboardPayload.FromFiles(paths));
        }

        private static ClipboardReadResult ReadRegisteredPng(IDataObject dataObject)
        {
            var format = FindActualFormat(dataObject, "PNG", "image/png");
            if (format == null)
            {
                return null;
            }

            try
            {
                var value = dataObject.GetData(format, false);
                byte[] bytes;
                var image = value as Image;
                if (image != null)
                {
                    bytes = EncodeAsPng(image);
                }
                else
                {
                    bytes = CopyBinaryValue(value, MaxPngByteCount);
                    ValidatePng(bytes);
                }

                return ClipboardReadResult.Success(ClipboardPayload.FromImage(bytes));
            }
            catch (PayloadTooLargeException exception)
            {
                return ClipboardReadResult.Failure(ClipboardReadStatus.TooLarge, exception.Message);
            }
            catch (Exception exception) when (IsInvalidImageException(exception))
            {
                return ClipboardReadResult.Failure(
                    ClipboardReadStatus.InvalidData,
                    "剪贴板 PNG 数据无效：" + exception.Message);
            }
        }

        private static ClipboardReadResult ReadBitmapOrDib(IDataObject dataObject)
        {
            var dibV5Name = DataFormats.GetFormat(CfDibV5).Name;
            var actualBitmap = FindActualFormat(dataObject, DataFormats.Bitmap);
            var actualDibV5 = FindActualFormat(dataObject, dibV5Name);
            var actualDib = FindActualFormat(dataObject, DataFormats.Dib);

            if (actualBitmap == null && actualDibV5 == null && actualDib == null &&
                !dataObject.GetDataPresent(DataFormats.Bitmap, true))
            {
                return null;
            }

            try
            {
                byte[] bytes;
                if (TryEncodeImageValue(
                    actualBitmap == null ? null : dataObject.GetData(actualBitmap, false),
                    out bytes))
                {
                    return ClipboardReadResult.Success(ClipboardPayload.FromImage(bytes));
                }

                if (TryEncodeDibValue(
                    actualDibV5 == null ? null : dataObject.GetData(actualDibV5, false),
                    out bytes))
                {
                    return ClipboardReadResult.Success(ClipboardPayload.FromImage(bytes));
                }

                if (TryEncodeDibValue(
                    actualDib == null ? null : dataObject.GetData(actualDib, false),
                    out bytes))
                {
                    return ClipboardReadResult.Success(ClipboardPayload.FromImage(bytes));
                }

                if (TryEncodeImageValue(dataObject.GetData(DataFormats.Bitmap, true), out bytes))
                {
                    return ClipboardReadResult.Success(ClipboardPayload.FromImage(bytes));
                }

                return ClipboardReadResult.Failure(
                    ClipboardReadStatus.InvalidData,
                    "剪贴板包含位图格式，但无法取得有效像素数据。");
            }
            catch (PayloadTooLargeException exception)
            {
                return ClipboardReadResult.Failure(ClipboardReadStatus.TooLarge, exception.Message);
            }
            catch (Exception exception) when (IsInvalidImageException(exception))
            {
                return ClipboardReadResult.Failure(
                    ClipboardReadStatus.InvalidData,
                    "剪贴板位图数据无效：" + exception.Message);
            }
        }

        private static ClipboardReadResult ReadTextOrUrl(IDataObject dataObject)
        {
            var format = FindActualFormat(
                dataObject,
                DataFormats.UnicodeText,
                DataFormats.Text,
                DataFormats.StringFormat);

            if (format == null)
            {
                return null;
            }

            var text = dataObject.GetData(format, false) as string;
            if (text == null)
            {
                return ClipboardReadResult.Failure(
                    ClipboardReadStatus.InvalidData,
                    "剪贴板声明了文字格式，但无法读取文字。");
            }

            if (text.Length > MaxTextCharacterCount)
            {
                return ClipboardReadResult.Failure(
                    ClipboardReadStatus.TooLarge,
                    "剪贴板文字超过允许的长度。");
            }

            var candidate = text.Trim();
            Uri uri;
            if (candidate.Length > 0 &&
                Uri.TryCreate(candidate, UriKind.Absolute, out uri) &&
                (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                return ClipboardReadResult.Success(ClipboardPayload.FromUrl(uri, candidate));
            }

            return ClipboardReadResult.Success(ClipboardPayload.FromText(text));
        }

        private static string FindActualFormat(IDataObject dataObject, params string[] candidates)
        {
            var formats = dataObject.GetFormats(false) ?? new string[0];
            foreach (var candidate in candidates)
            {
                var match = formats.FirstOrDefault(
                    format => string.Equals(format, candidate, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private static byte[] CopyBinaryValue(object value, int maximumBytes)
        {
            var bytes = value as byte[];
            if (bytes != null)
            {
                if (bytes.Length > maximumBytes)
                {
                    throw new PayloadTooLargeException("剪贴板图片超过允许的大小。");
                }

                return (byte[])bytes.Clone();
            }

            var stream = value as Stream;
            if (stream == null)
            {
                throw new InvalidDataException("图片格式没有提供字节流。");
            }

            long originalPosition = 0;
            var restorePosition = stream.CanSeek;
            if (restorePosition)
            {
                originalPosition = stream.Position;
                if (stream.Length > maximumBytes)
                {
                    throw new PayloadTooLargeException("剪贴板图片超过允许的大小。");
                }

                stream.Position = 0;
            }

            try
            {
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + read > maximumBytes)
                        {
                            throw new PayloadTooLargeException("剪贴板图片超过允许的大小。");
                        }

                        output.Write(buffer, 0, read);
                    }

                    return output.ToArray();
                }
            }
            finally
            {
                if (restorePosition)
                {
                    try
                    {
                        stream.Position = originalPosition;
                    }
                    catch (IOException)
                    {
                        // The clipboard owns the stream; failure to restore is non-fatal.
                    }
                }
            }
        }

        private static bool TryEncodeImageValue(object value, out byte[] bytes)
        {
            var image = value as Image;
            if (image == null)
            {
                bytes = null;
                return false;
            }

            bytes = EncodeAsPng(image);
            return true;
        }

        private static bool TryEncodeDibValue(object value, out byte[] bytes)
        {
            if (TryEncodeImageValue(value, out bytes))
            {
                return true;
            }

            if (value == null)
            {
                bytes = null;
                return false;
            }

            var dibBytes = CopyBinaryValue(value, MaxDibByteCount);
            var bitmapFile = AddBitmapFileHeader(dibBytes);
            using (var input = new MemoryStream(bitmapFile, false))
            using (var image = Image.FromStream(input, true, true))
            {
                bytes = EncodeAsPng(image);
                return true;
            }
        }

        private static byte[] EncodeAsPng(Image image)
        {
            ValidatePixelDimensions(image.Width, image.Height);
            using (var output = new MemoryStream())
            {
                image.Save(output, ImageFormat.Png);
                if (output.Length > MaxPngByteCount)
                {
                    throw new PayloadTooLargeException("编码后的剪贴板图片超过允许的大小。");
                }

                return output.ToArray();
            }
        }

        private static void ValidatePng(byte[] bytes)
        {
            if (bytes == null || bytes.Length < PngSignature.Length)
            {
                throw new InvalidDataException("PNG 数据过短。");
            }

            for (var index = 0; index < PngSignature.Length; index++)
            {
                if (bytes[index] != PngSignature[index])
                {
                    throw new InvalidDataException("PNG 文件头无效。");
                }
            }

            using (var input = new MemoryStream(bytes, false))
            using (var image = Image.FromStream(input, true, true))
            {
                ValidatePixelDimensions(image.Width, image.Height);
            }
        }

        private static void ValidatePixelDimensions(int width, int height)
        {
            if (width <= 0 || height <= 0 || (long)width * height > MaxImagePixelCount)
            {
                throw new PayloadTooLargeException("剪贴板图片的像素尺寸超过允许范围。");
            }
        }

        private static byte[] AddBitmapFileHeader(byte[] dib)
        {
            if (dib == null || dib.Length < 12)
            {
                throw new InvalidDataException("DIB 数据过短。");
            }

            var headerSize = ReadUInt32(dib, 0);
            long pixelOffset;
            if (headerSize == 12)
            {
                var bitCount = ReadUInt16(dib, 10);
                var colorCount = bitCount <= 8 ? 1L << bitCount : 0;
                pixelOffset = headerSize + colorCount * 3L;
            }
            else if (headerSize >= 40 && headerSize <= dib.Length)
            {
                var bitCount = ReadUInt16(dib, 14);
                var compression = ReadUInt32(dib, 16);
                var colorsUsed = ReadUInt32(dib, 32);
                long maskBytes = 0;
                if (headerSize == 40 && compression == 3)
                {
                    maskBytes = 12;
                }
                else if (headerSize == 40 && compression == 6)
                {
                    maskBytes = 16;
                }

                var colorCount = colorsUsed != 0
                    ? colorsUsed
                    : (bitCount <= 8 ? 1L << bitCount : 0);
                pixelOffset = headerSize + maskBytes + colorCount * 4L;
            }
            else
            {
                throw new InvalidDataException("DIB 信息头不受支持。");
            }

            if (pixelOffset < headerSize || pixelOffset > dib.Length)
            {
                throw new InvalidDataException("DIB 像素偏移无效。");
            }

            var totalLength = checked(dib.Length + 14);
            var bitmap = new byte[totalLength];
            bitmap[0] = 0x42;
            bitmap[1] = 0x4D;
            WriteUInt32(bitmap, 2, (uint)totalLength);
            WriteUInt32(bitmap, 10, checked((uint)(pixelOffset + 14)));
            Buffer.BlockCopy(dib, 0, bitmap, 14, dib.Length);
            return bitmap;
        }

        private static ushort ReadUInt16(byte[] bytes, int offset)
        {
            EnsureRange(bytes, offset, 2);
            return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            EnsureRange(bytes, offset, 4);
            return (uint)(bytes[offset] |
                          (bytes[offset + 1] << 8) |
                          (bytes[offset + 2] << 16) |
                          (bytes[offset + 3] << 24));
        }

        private static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            EnsureRange(bytes, offset, 4);
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        private static void EnsureRange(byte[] bytes, int offset, int count)
        {
            if (bytes == null || offset < 0 || count < 0 || offset > bytes.Length - count)
            {
                throw new InvalidDataException("图片数据结构不完整。");
            }
        }

        private static bool IsInvalidImageException(Exception exception)
        {
            return exception is ArgumentException ||
                   exception is InvalidDataException ||
                   exception is ExternalException ||
                   exception is IOException ||
                   exception is NotSupportedException ||
                   exception is OverflowException ||
                   exception is OutOfMemoryException;
        }

        private sealed class PayloadTooLargeException : Exception
        {
            internal PayloadTooLargeException(string message)
                : base(message)
            {
            }
        }
    }
}

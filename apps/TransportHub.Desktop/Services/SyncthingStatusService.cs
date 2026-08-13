using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using TransportHub.Desktop.Models;

namespace TransportHub.Desktop.Services
{
    internal sealed class SyncthingStatus
    {
        internal bool Running { get; set; }
        internal int OnlineDevices { get; set; }
        internal int TotalDevices { get; set; }
        internal bool FolderIdle { get; set; }
        internal long DownloadBytesPerSecond { get; set; }
        internal long UploadBytesPerSecond { get; set; }
        internal IReadOnlyList<IncomingTransferInfo> IncomingTransfers { get; set; }
        internal string Detail { get; set; }
    }

    internal sealed class IncomingTransferInfo
    {
        internal string RelativePath { get; set; }
        internal string SenderName { get; set; }
        internal string FileName { get; set; }
        internal long BytesTotal { get; set; }
        internal long BytesDone { get; set; }

        internal int Percent
        {
            get
            {
                if (BytesTotal <= 0L)
                {
                    return 0;
                }
                var percent = Math.Round(BytesDone * 100d / BytesTotal,
                    MidpointRounding.AwayFromZero);
                return (int)Math.Max(0d, Math.Min(100d, percent));
            }
        }
    }

    internal sealed class SyncthingStatusService : IDisposable
    {
        private readonly SyncthingContext _context;
        private readonly HttpClient _client;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
        private Timer _timer;
        private int _refreshing;
        private bool _disposed;
        private bool _hasTrafficSample;
        private long _previousReceivedBytes;
        private long _previousSentBytes;
        private DateTime _previousTrafficSampleUtc;

        internal SyncthingStatusService(SyncthingContext context)
        {
            _context = context ?? throw new ArgumentNullException("context");
            var handler = new WebRequestHandler();
            if (string.Equals(context.GuiUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && context.GuiUri.IsLoopback)
            {
                handler.ServerCertificateValidationCallback = (sender, certificate, chain, errors) =>
                    errors == SslPolicyErrors.None || context.GuiUri.IsLoopback;
            }
            _client = new HttpClient(handler) { BaseAddress = context.GuiUri, Timeout = TimeSpan.FromSeconds(3) };
            if (!string.IsNullOrWhiteSpace(context.ApiKey))
            {
                _client.DefaultRequestHeaders.Add("X-API-Key", context.ApiKey);
            }
            Current = new SyncthingStatus
            {
                Running = false,
                OnlineDevices = 0,
                TotalDevices = context.TargetDevices.Count,
                FolderIdle = false,
                IncomingTransfers = new List<IncomingTransferInfo>(),
                Detail = "正在连接 Syncthing"
            };
        }

        internal event EventHandler StatusChanged;

        internal SyncthingStatus Current { get; private set; }

        internal void Start()
        {
            ThrowIfDisposed();
            if (_timer == null)
            {
                _timer = new Timer(async state => await RefreshAsync().ConfigureAwait(false), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
            }
        }

        internal async Task RefreshAsync()
        {
            if (_disposed || Interlocked.Exchange(ref _refreshing, 1) != 0)
            {
                return;
            }

            try
            {
                _context.RefreshTargetDevices();
                var statusResponse = await _client.GetStringAsync("rest/system/status").ConfigureAwait(false);
                var connectionsResponse = await _client.GetStringAsync("rest/system/connections").ConfigureAwait(false);
                var incomingTransfers = await GetIncomingTransfersAsync().ConfigureAwait(false);
                string folderResponse = null;
                try
                {
                    folderResponse = await _client.GetStringAsync("rest/db/status?folder=" + Uri.EscapeDataString(_context.FolderId)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The service is still healthy even if this optional folder status call fails.
                }

                var statusObject = AsDictionary(_serializer.DeserializeObject(statusResponse));
                var connectionsObject = AsDictionary(_serializer.DeserializeObject(connectionsResponse));
                var folderObject = string.IsNullOrWhiteSpace(folderResponse) ? null : AsDictionary(_serializer.DeserializeObject(folderResponse));

                var onlineIds = GetOnlineTargetIds(connectionsObject);
                var online = onlineIds.Count;
                long downloadBytesPerSecond;
                long uploadBytesPerSecond;
                SampleTransferRates(connectionsObject, onlineIds, DateTime.UtcNow,
                    out downloadBytesPerSecond, out uploadBytesPerSecond);
                var state = GetString(folderObject, "state");
                var idle = string.Equals(state, "idle", StringComparison.OrdinalIgnoreCase);
                var remoteComplete = idle && await AreRemoteFoldersCompleteAsync(onlineIds).ConfigureAwait(false);
                var total = _context.TargetDevices.Count;
                var detail = total == 0
                    ? "点击这里连接电脑"
                    : online == 0
                        ? "设备均离线，内容将在上线后同步"
                        : remoteComplete
                            ? online + " 台在线 · 目录最新"
                            : "同步中 · " + FormatTransferRates(downloadBytesPerSecond, uploadBytesPerSecond);

                var identityMatches = string.IsNullOrWhiteSpace(GetString(statusObject, "myID")) ||
                    string.Equals(GetString(statusObject, "myID"), _context.LocalDeviceId, StringComparison.OrdinalIgnoreCase);
                if (!identityMatches)
                {
                    detail = "Syncthing 身份与当前配置不一致";
                }

                SetCurrent(new SyncthingStatus
                {
                    Running = identityMatches,
                    OnlineDevices = online,
                    TotalDevices = total,
                    FolderIdle = identityMatches && idle,
                    DownloadBytesPerSecond = downloadBytesPerSecond,
                    UploadBytesPerSecond = uploadBytesPerSecond,
                    IncomingTransfers = incomingTransfers,
                    Detail = detail
                });
            }
            catch (Exception)
            {
                SetCurrent(new SyncthingStatus
                {
                    Running = false,
                    OnlineDevices = 0,
                    TotalDevices = _context.TargetDevices.Count,
                    FolderIdle = false,
                    IncomingTransfers = new List<IncomingTransferInfo>(),
                    Detail = "Syncthing 未运行 · 内容会先保存在本机"
                });
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        }

        internal void OpenWebGui()
        {
            Process.Start(new ProcessStartInfo(_context.GuiUri.AbsoluteUri) { UseShellExecute = true });
        }

        private List<string> GetOnlineTargetIds(IDictionary<string, object> root)
        {
            object rawConnections;
            var connections = root != null && root.TryGetValue("connections", out rawConnections)
                ? AsDictionary(rawConnections)
                : null;
            if (connections == null)
            {
                return new List<string>();
            }

            var targetIds = new HashSet<string>(_context.TargetDevices.Select(device => device.Id), StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var pair in connections)
            {
                if (!targetIds.Contains(pair.Key))
                {
                    continue;
                }
                var value = AsDictionary(pair.Value);
                object connected;
                if (value != null && value.TryGetValue("connected", out connected) && connected is bool && (bool)connected)
                {
                    result.Add(pair.Key);
                }
            }
            return result;
        }

        private void SampleTransferRates(
            IDictionary<string, object> root,
            IEnumerable<string> onlineDeviceIds,
            DateTime sampledUtc,
            out long downloadBytesPerSecond,
            out long uploadBytesPerSecond)
        {
            downloadBytesPerSecond = 0;
            uploadBytesPerSecond = 0;
            object rawConnections;
            var connections = root != null && root.TryGetValue("connections", out rawConnections)
                ? AsDictionary(rawConnections)
                : null;
            if (connections == null)
            {
                _hasTrafficSample = false;
                return;
            }

            var onlineIds = new HashSet<string>(onlineDeviceIds ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            long receivedBytes = 0;
            long sentBytes = 0;
            foreach (var pair in connections)
            {
                if (!onlineIds.Contains(pair.Key))
                {
                    continue;
                }
                var connection = AsDictionary(pair.Value);
                receivedBytes = SaturatingAdd(receivedBytes, GetLong(connection, "inBytesTotal"));
                sentBytes = SaturatingAdd(sentBytes, GetLong(connection, "outBytesTotal"));
            }

            if (_hasTrafficSample)
            {
                var elapsed = (sampledUtc - _previousTrafficSampleUtc).TotalSeconds;
                if (elapsed > 0.25d && receivedBytes >= _previousReceivedBytes && sentBytes >= _previousSentBytes)
                {
                    downloadBytesPerSecond = RatePerSecond(receivedBytes - _previousReceivedBytes, elapsed);
                    uploadBytesPerSecond = RatePerSecond(sentBytes - _previousSentBytes, elapsed);
                }
            }
            _previousReceivedBytes = receivedBytes;
            _previousSentBytes = sentBytes;
            _previousTrafficSampleUtc = sampledUtc;
            _hasTrafficSample = onlineIds.Count > 0;
        }

        internal static string FormatTransferRates(long downloadBytesPerSecond, long uploadBytesPerSecond)
        {
            downloadBytesPerSecond = Math.Max(0L, downloadBytesPerSecond);
            uploadBytesPerSecond = Math.Max(0L, uploadBytesPerSecond);
            if (downloadBytesPerSecond == 0L && uploadBytesPerSecond == 0L)
            {
                return "测速中";
            }
            if (downloadBytesPerSecond == 0L)
            {
                return "↑ " + FormatRate(uploadBytesPerSecond);
            }
            if (uploadBytesPerSecond == 0L)
            {
                return "↓ " + FormatRate(downloadBytesPerSecond);
            }
            return "↓ " + FormatRate(downloadBytesPerSecond) + " · ↑ " + FormatRate(uploadBytesPerSecond);
        }

        private static string FormatRate(long bytesPerSecond)
        {
            const double kibibyte = 1024d;
            const double mebibyte = 1024d * 1024d;
            const double gibibyte = 1024d * 1024d * 1024d;
            if (bytesPerSecond >= gibibyte)
            {
                return (bytesPerSecond / gibibyte).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB/s";
            }
            if (bytesPerSecond >= mebibyte)
            {
                return (bytesPerSecond / mebibyte).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB/s";
            }
            return Math.Max(1d, bytesPerSecond / kibibyte).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " KB/s";
        }

        private static long GetLong(IDictionary<string, object> dictionary, string key)
        {
            object value;
            long parsed;
            return dictionary != null && dictionary.TryGetValue(key, out value) && value != null &&
                Int64.TryParse(Convert.ToString(value), out parsed) && parsed > 0L
                ? parsed
                : 0L;
        }

        private static long SaturatingAdd(long left, long right)
        {
            return right > 0L && left > Int64.MaxValue - right ? Int64.MaxValue : left + Math.Max(0L, right);
        }

        private static long RatePerSecond(long bytes, double seconds)
        {
            var rate = bytes / seconds;
            return rate >= Int64.MaxValue ? Int64.MaxValue : Math.Max(0L, (long)Math.Round(rate));
        }

        private async Task<bool> AreRemoteFoldersCompleteAsync(IEnumerable<string> deviceIds)
        {
            foreach (var deviceId in deviceIds)
            {
                try
                {
                    var response = await _client.GetStringAsync(
                        "rest/db/completion?folder=" + Uri.EscapeDataString(_context.FolderId) +
                        "&device=" + Uri.EscapeDataString(deviceId)).ConfigureAwait(false);
                    var completion = AsDictionary(_serializer.DeserializeObject(response));
                    object rawValue;
                    double value;
                    if (completion == null || !completion.TryGetValue("completion", out rawValue) ||
                        !Double.TryParse(Convert.ToString(rawValue),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out value) || value < 99.999d)
                    {
                        return false;
                    }
                }
                catch (Exception)
                {
                    return false;
                }
            }
            return true;
        }

        private async Task<IReadOnlyList<IncomingTransferInfo>> GetIncomingTransfersAsync()
        {
            try
            {
                var response = await _client.GetStringAsync(
                    "rest/events?events=DownloadProgress&limit=1&timeout=0").ConfigureAwait(false);
                return ParseIncomingTransfers(response, _context.FolderId);
            }
            catch (Exception)
            {
                return new List<IncomingTransferInfo>();
            }
        }

        internal static IReadOnlyList<IncomingTransferInfo> ParseIncomingTransfers(string response, string folderId)
        {
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
                var events = serializer.DeserializeObject(response ?? String.Empty) as IEnumerable;
                if (events == null)
                {
                    return new List<IncomingTransferInfo>();
                }
                var latest = events.Cast<object>().LastOrDefault() as IDictionary<string, object>;
                object rawData;
                var data = latest != null && latest.TryGetValue("data", out rawData)
                    ? AsDictionary(rawData)
                    : null;
                object rawFolder;
                var folder = data != null && !String.IsNullOrWhiteSpace(folderId) &&
                    data.TryGetValue(folderId, out rawFolder)
                    ? AsDictionary(rawFolder)
                    : null;
                if (folder == null)
                {
                    return new List<IncomingTransferInfo>();
                }

                var result = new List<IncomingTransferInfo>();
                foreach (var pair in folder.OrderBy(item => item.Key, StringComparer.CurrentCultureIgnoreCase))
                {
                    string relativePath;
                    if (!TryNormalizeIncomingPath(pair.Key, out relativePath))
                    {
                        continue;
                    }
                    var progress = AsDictionary(pair.Value);
                    var bytesTotal = GetLong(progress, "bytesTotal");
                    var bytesDone = Math.Min(bytesTotal, GetLong(progress, "bytesDone"));
                    if (bytesTotal <= 0L)
                    {
                        continue;
                    }
                    var segments = relativePath.Split('/');
                    result.Add(new IncomingTransferInfo
                    {
                        RelativePath = relativePath,
                        SenderName = segments.Length > 1 ? SafeDisplayText(segments[0], "其他电脑") : "其他电脑",
                        FileName = SafeDisplayText(segments[segments.Length - 1], "正在接收的文件"),
                        BytesTotal = bytesTotal,
                        BytesDone = bytesDone
                    });
                    if (result.Count >= 8)
                    {
                        break;
                    }
                }
                return result;
            }
            catch (Exception)
            {
                return new List<IncomingTransferInfo>();
            }
        }

        private static bool TryNormalizeIncomingPath(string value, out string normalized)
        {
            normalized = String.Empty;
            var path = (value ?? String.Empty).Trim().Replace('\\', '/');
            if (path.Length == 0 || path.Length > 4096 || path.StartsWith("/", StringComparison.Ordinal) ||
                path.StartsWith(".transporthub/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(".stversions/", StringComparison.OrdinalIgnoreCase) ||
                path.IndexOf('\0') >= 0)
            {
                return false;
            }
            var segments = path.Split('/');
            if (segments.Any(segment => String.IsNullOrWhiteSpace(segment) || segment == "." || segment == ".."))
            {
                return false;
            }
            normalized = String.Join("/", segments);
            return true;
        }

        private static string SafeDisplayText(string value, string fallback)
        {
            var characters = (value ?? String.Empty).Where(character =>
                !Char.IsControl(character) && character != '\u202A' && character != '\u202B' &&
                character != '\u202D' && character != '\u202E' && character != '\u2066' &&
                character != '\u2067' && character != '\u2068' && character != '\u2069').ToArray();
            var result = new String(characters).Trim();
            if (String.IsNullOrWhiteSpace(result))
            {
                return fallback;
            }
            return result.Length <= 96 ? result : result.Substring(0, 93) + "...";
        }

        private static IDictionary<string, object> AsDictionary(object value)
        {
            return value as IDictionary<string, object>;
        }

        private static string GetString(IDictionary<string, object> dictionary, string key)
        {
            object value;
            return dictionary != null && dictionary.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : string.Empty;
        }

        private void SetCurrent(SyncthingStatus status)
        {
            Current = status;
            var handler = StatusChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }
            _client.Dispose();
        }
    }
}

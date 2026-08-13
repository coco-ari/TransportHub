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
        internal string Detail { get; set; }
    }

    internal sealed class SyncthingStatusService : IDisposable
    {
        private readonly SyncthingContext _context;
        private readonly HttpClient _client;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
        private Timer _timer;
        private int _refreshing;
        private bool _disposed;

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
                            : online + " 台在线 · 正在同步";

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

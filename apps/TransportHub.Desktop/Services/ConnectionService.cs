using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using TransportHub.Desktop.Models;

namespace TransportHub.Desktop.Services
{
    internal sealed class PendingDeviceInfo
    {
        internal string Id { get; set; }
        internal string Name { get; set; }
        internal string Address { get; set; }
    }

    internal sealed class ConnectionCodeInfo
    {
        internal string DeviceId { get; set; }
        internal string DeviceName { get; set; }
    }

    internal sealed class ConnectionService : IDisposable
    {
        private static readonly Regex DeviceIdPattern = new Regex(
            "^[A-Z2-7]{7}(?:-[A-Z2-7]{7}){7}$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private readonly SyncthingContext _context;
        private readonly HttpClient _client;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        private readonly SemaphoreSlim _configurationLock = new SemaphoreSlim(1, 1);
        private readonly object _pendingLock = new object();
        private List<PendingDeviceInfo> _pendingDevices = new List<PendingDeviceInfo>();
        private Timer _timer;
        private int _refreshing;
        private bool _disposed;

        internal ConnectionService(SyncthingContext context)
        {
            _context = context ?? throw new ArgumentNullException("context");
            var handler = new WebRequestHandler();
            if (String.Equals(context.GuiUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                context.GuiUri.IsLoopback)
            {
                handler.ServerCertificateValidationCallback = (sender, certificate, chain, errors) =>
                    errors == SslPolicyErrors.None || context.GuiUri.IsLoopback;
            }
            _client = new HttpClient(handler) { BaseAddress = context.GuiUri, Timeout = TimeSpan.FromSeconds(8) };
            if (!String.IsNullOrWhiteSpace(context.ApiKey))
            {
                _client.DefaultRequestHeaders.Add("X-API-Key", context.ApiKey);
            }
        }

        internal event EventHandler PendingDevicesChanged;

        internal string LocalConnectionCode
        {
            get { return BuildConnectionCode(_context.LocalDeviceId, _context.LocalDeviceName); }
        }

        internal IReadOnlyList<PendingDeviceInfo> PendingDevices
        {
            get
            {
                lock (_pendingLock)
                {
                    return _pendingDevices.Select(ClonePendingDevice).ToList();
                }
            }
        }

        internal void Start()
        {
            ThrowIfDisposed();
            if (_timer == null)
            {
                _timer = new Timer(async state => await RefreshPendingAsync().ConfigureAwait(false),
                    null, TimeSpan.Zero, TimeSpan.FromSeconds(4));
            }
        }

        internal async Task RefreshPendingAsync()
        {
            if (_disposed || Interlocked.Exchange(ref _refreshing, 1) != 0)
            {
                return;
            }
            try
            {
                var json = await _client.GetStringAsync("rest/cluster/pending/devices").ConfigureAwait(false);
                var root = _serializer.DeserializeObject(json) as IDictionary<string, object>;
                var devices = new List<PendingDeviceInfo>();
                if (root != null)
                {
                    foreach (var pair in root)
                    {
                        var id = (pair.Key ?? String.Empty).Trim().ToUpperInvariant();
                        if (!DeviceIdPattern.IsMatch(id) ||
                            String.Equals(id, _context.LocalDeviceId, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        var value = pair.Value as IDictionary<string, object>;
                        devices.Add(new PendingDeviceInfo
                        {
                            Id = id,
                            Name = SafeDeviceName(GetString(value, "name"), id),
                            Address = GetString(value, "address")
                        });
                    }
                }
                devices = devices.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                bool changed;
                lock (_pendingLock)
                {
                    changed = PendingSignature(_pendingDevices) != PendingSignature(devices);
                    _pendingDevices = devices;
                }
                if (changed)
                {
                    PendingDevicesChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception)
            {
                // Connection requests are optional status information. A temporary API
                // outage must not interrupt local messaging or file drops.
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        }

        internal async Task AddAndShareAsync(string connectionCode, string fallbackName = null)
        {
            var parsed = ParseConnectionCode(connectionCode);
            if (String.Equals(parsed.DeviceId, _context.LocalDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("这是本机的连接码，请输入另一台电脑的连接码。");
            }
            var name = SafeDeviceName(String.IsNullOrWhiteSpace(parsed.DeviceName) ? fallbackName : parsed.DeviceName,
                parsed.DeviceId);
            await AddAndShareCoreAsync(parsed.DeviceId, name).ConfigureAwait(false);
        }

        internal async Task AcceptPendingAsync(PendingDeviceInfo pending)
        {
            if (pending == null)
            {
                throw new ArgumentNullException("pending");
            }
            var id = NormalizeDeviceId(pending.Id);
            await AddAndShareCoreAsync(id, SafeDeviceName(pending.Name, id)).ConfigureAwait(false);
            try
            {
                var response = await _client.DeleteAsync(
                    "rest/cluster/pending/devices?device=" + Uri.EscapeDataString(id)).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    response.EnsureSuccessStatusCode();
                }
            }
            finally
            {
                await RefreshPendingAsync().ConfigureAwait(false);
            }
        }

        internal static string BuildConnectionCode(string deviceId, string deviceName)
        {
            var id = NormalizeDeviceId(deviceId);
            var nameBytes = Encoding.UTF8.GetBytes(SafeDeviceName(deviceName, id));
            var encodedName = Convert.ToBase64String(nameBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return "TH1:" + id + ":" + encodedName;
        }

        internal static ConnectionCodeInfo ParseConnectionCode(string value)
        {
            var input = (value ?? String.Empty).Trim();
            if (String.IsNullOrWhiteSpace(input))
            {
                throw new ArgumentException("请粘贴另一台电脑的连接码。", "value");
            }
            if (!input.StartsWith("TH1:", StringComparison.OrdinalIgnoreCase))
            {
                return new ConnectionCodeInfo { DeviceId = NormalizeDeviceId(input), DeviceName = String.Empty };
            }
            var parts = input.Split(new[] { ':' }, 3);
            if (parts.Length != 3)
            {
                throw new ArgumentException("连接码格式不正确，请重新复制。", "value");
            }
            var id = NormalizeDeviceId(parts[1]);
            try
            {
                var encoded = parts[2].Replace('-', '+').Replace('_', '/');
                encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
                var name = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                return new ConnectionCodeInfo { DeviceId = id, DeviceName = SafeDeviceName(name, id) };
            }
            catch (FormatException exception)
            {
                throw new ArgumentException("连接码格式不正确，请重新复制。", "value", exception);
            }
        }

        private async Task AddAndShareCoreAsync(string deviceId, string name)
        {
            ThrowIfDisposed();
            await _configurationLock.WaitAsync().ConfigureAwait(false);
            var deviceWasAdded = false;
            try
            {
                if (!await DeviceExistsAsync(deviceId).ConfigureAwait(false))
                {
                    var template = await GetJsonAsync("rest/config/defaults/device").ConfigureAwait(false);
                    template["deviceID"] = deviceId;
                    template["name"] = name;
                    await SendJsonAsync(HttpMethod.Post, "rest/config/devices", template).ConfigureAwait(false);
                    deviceWasAdded = true;
                }

                var folder = await GetJsonAsync(
                    "rest/config/folders/" + Uri.EscapeDataString(_context.FolderId)).ConfigureAwait(false);
                var devices = ToObjectList(folder.ContainsKey("devices") ? folder["devices"] : null);
                var alreadyShared = devices.Any(item =>
                    String.Equals(GetString(item as IDictionary<string, object>, "deviceID"), deviceId,
                        StringComparison.OrdinalIgnoreCase));
                if (!alreadyShared)
                {
                    devices.Add(new Dictionary<string, object> { { "deviceID", deviceId } });
                    var patch = new Dictionary<string, object> { { "devices", devices.ToArray() } };
                    await SendJsonAsync(new HttpMethod("PATCH"),
                        "rest/config/folders/" + Uri.EscapeDataString(_context.FolderId), patch).ConfigureAwait(false);
                }
                _context.RefreshTargetDevices();
            }
            catch
            {
                if (deviceWasAdded)
                {
                    try
                    {
                        await _client.DeleteAsync("rest/config/devices/" + Uri.EscapeDataString(deviceId))
                            .ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                }
                throw;
            }
            finally
            {
                _configurationLock.Release();
            }
        }

        private async Task<bool> DeviceExistsAsync(string deviceId)
        {
            using (var response = await _client.GetAsync(
                "rest/config/devices/" + Uri.EscapeDataString(deviceId)).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return false;
                }
                response.EnsureSuccessStatusCode();
                return true;
            }
        }

        private async Task<IDictionary<string, object>> GetJsonAsync(string path)
        {
            var json = await _client.GetStringAsync(path).ConfigureAwait(false);
            var value = _serializer.DeserializeObject(json) as IDictionary<string, object>;
            if (value == null)
            {
                throw new InvalidOperationException("Syncthing 返回了无法识别的配置数据。");
            }
            return value;
        }

        private async Task SendJsonAsync(HttpMethod method, string path, object value)
        {
            var json = _serializer.Serialize(value);
            using (var request = new HttpRequestMessage(method, path))
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using (var response = await _client.SendAsync(request).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                }
            }
        }

        private static List<object> ToObjectList(object value)
        {
            var enumerable = value as IEnumerable;
            if (enumerable == null || value is string)
            {
                return new List<object>();
            }
            return enumerable.Cast<object>().ToList();
        }

        private static string NormalizeDeviceId(string value)
        {
            var id = Regex.Replace((value ?? String.Empty).Trim().ToUpperInvariant(), "\\s+", String.Empty);
            if (!DeviceIdPattern.IsMatch(id))
            {
                throw new ArgumentException("连接码无效，请在另一台电脑中重新复制完整连接码。", "value");
            }
            return id;
        }

        private static string SafeDeviceName(string value, string deviceId)
        {
            var name = Regex.Replace((value ?? String.Empty).Trim(), "[\\x00-\\x1F\\x7F]", String.Empty);
            if (String.IsNullOrWhiteSpace(name))
            {
                name = "电脑-" + (deviceId ?? String.Empty).Substring(0, Math.Min(7, (deviceId ?? String.Empty).Length));
            }
            return name.Length <= 64 ? name : name.Substring(0, 64);
        }

        private static string GetString(IDictionary<string, object> dictionary, string key)
        {
            object value;
            return dictionary != null && dictionary.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value)
                : String.Empty;
        }

        private static PendingDeviceInfo ClonePendingDevice(PendingDeviceInfo value)
        {
            return new PendingDeviceInfo { Id = value.Id, Name = value.Name, Address = value.Address };
        }

        private static string PendingSignature(IEnumerable<PendingDeviceInfo> devices)
        {
            return String.Join("|", (devices ?? Enumerable.Empty<PendingDeviceInfo>())
                .Select(item => item.Id + ":" + item.Name + ":" + item.Address));
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

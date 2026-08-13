using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using TransportHub.Desktop.Core;
using TransportHub.Desktop.Forms;
using TransportHub.Desktop.Interop;
using TransportHub.Desktop.Models;
using TransportHub.Desktop.Services;
using NativeWindowHelper = TransportHub.Desktop.Interop.NativeWindow;

namespace TransportHub.Desktop.Application
{
    internal sealed class TransportHubApplicationContext : System.Windows.Forms.ApplicationContext
    {
        private const string SettingsKeyPath = @"Software\TransportHub\Desktop";
        private const int WindowLayoutVersion = 3;
        private readonly SyncthingContext _syncthing;
        private readonly TimelineStore _timelineStore;
        private readonly TransferService _transferService;
        private readonly SyncthingStatusService _statusService;
        private readonly ConnectionService _connectionService;
        private readonly MainForm _mainForm;
        private readonly EdgeButtonForm _edgeButton;
        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _trayMenu;
        private bool _collapsed;
        private bool _exiting;
        private bool _shownTrayHint;
        private bool _resourcesDisposed;

        internal TransportHubApplicationContext()
        {
            _syncthing = SyncthingContext.Load();
            _timelineStore = new TimelineStore(
                _syncthing.RootPath,
                _syncthing.LocalDeviceId,
                _syncthing.LocalDeviceName);
            _transferService = new TransferService(_syncthing);
            _statusService = new SyncthingStatusService(_syncthing);
            _connectionService = new ConnectionService(_syncthing);
            _mainForm = new MainForm(_syncthing, _timelineStore, _transferService, _statusService, _connectionService);
            _edgeButton = new EdgeButtonForm();

            _trayMenu = BuildTrayMenu();
            _notifyIcon = new NotifyIcon
            {
                Icon = NativeWindowHelper.CreateApplicationIcon(64),
                Text = "TransportHub · 文字与文件同步",
                Visible = true,
                ContextMenuStrip = _trayMenu
            };

            _mainForm.CollapseRequested += delegate { CollapseToEdge(); };
            _mainForm.HideRequested += delegate { HideToTray(); };
            _mainForm.StateChanged += delegate { UpdateEdgeState(); };
            _mainForm.FormClosed += delegate
            {
                if (!_exiting)
                {
                    ExitApplication(false);
                }
            };
            _edgeButton.ExpandRequested += delegate { ShowMainFromEdge(); };
            _edgeButton.PathsDropped += delegate(object sender, PathsDroppedEventArgs args)
            {
                _mainForm.QueuePaths(args.Paths);
            };
            _notifyIcon.DoubleClick += delegate { ShowMain(); };
            _notifyIcon.BalloonTipClicked += delegate { ShowMain(); };
            _statusService.StatusChanged += HandleStatusChanged;
            SystemEvents.DisplaySettingsChanged += HandleDisplaySettingsChanged;

            var settings = LoadWindowSettings();
            _mainForm.Bounds = settings.Bounds;
            _collapsed = settings.Collapsed;
            if (_collapsed)
            {
                _edgeButton.ShowAt(_mainForm.Bounds);
            }
            else
            {
                _mainForm.Show();
                _mainForm.Activate();
                _mainForm.MarkRead();
            }

            _statusService.Start();
            _connectionService.Start();
            UpdateEdgeState();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_resourcesDisposed)
            {
                _resourcesDisposed = true;
                SystemEvents.DisplaySettingsChanged -= HandleDisplaySettingsChanged;
                _statusService.StatusChanged -= HandleStatusChanged;
                _notifyIcon.Visible = false;
                _notifyIcon.ContextMenuStrip = null;
                _notifyIcon.Dispose();
                if (!_trayMenu.IsDisposed)
                {
                    if (_trayMenu.Visible)
                    {
                        _trayMenu.Close(ToolStripDropDownCloseReason.CloseCalled);
                    }
                    _trayMenu.Dispose();
                }
                _edgeButton.Dispose();
                _mainForm.Dispose();
                _connectionService.Dispose();
                _statusService.Dispose();
            }
            base.Dispose(disposing);
        }

        private ContextMenuStrip BuildTrayMenu()
        {
            var menu = new ContextMenuStrip
            {
                ShowImageMargin = false,
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point)
            };
            menu.Items.Add("打开 TransportHub", null, delegate { ShowMain(); });
            menu.Items.Add("折叠到屏幕边缘", null, delegate { CollapseToEdge(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("发送文件…", null, delegate
            {
                ShowMain();
                _mainForm.PromptForFiles();
            });
            menu.Items.Add("发送文件夹…", null, delegate
            {
                ShowMain();
                _mainForm.PromptForFolder();
            });
            menu.Items.Add("打开同步目录", null, delegate { OpenSyncRoot(); });
            menu.Items.Add("连接电脑…", null, delegate
            {
                ShowMain();
                _mainForm.ShowConnectionSetup();
            });
            menu.Items.Add("Syncthing 状态", null, delegate { _statusService.OpenWebGui(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出 TransportHub", null, delegate { ExitApplication(); });
            return menu;
        }

        private void CollapseToEdge()
        {
            if (_exiting)
            {
                return;
            }
            var bounds = _mainForm.Visible ? _mainForm.Bounds : RestoreVisibleBounds(_mainForm.Bounds, _mainForm.DesiredExpandedSize);
            _mainForm.Hide();
            _collapsed = true;
            SaveWindowSettings(bounds, true);
            _edgeButton.ShowAt(bounds);
            UpdateEdgeState();
        }

        private void ShowMainFromEdge()
        {
            var bounds = _edgeButton.GetExpandedBounds(_mainForm.DesiredExpandedSize);
            _edgeButton.Hide();
            _mainForm.Bounds = bounds;
            _collapsed = false;
            _mainForm.Show();
            _mainForm.WindowState = FormWindowState.Normal;
            _mainForm.Activate();
            _mainForm.BringToFront();
            _mainForm.MarkRead();
            SaveWindowSettings(bounds, false);
        }

        private void ShowMain()
        {
            if (_edgeButton.Visible)
            {
                ShowMainFromEdge();
                return;
            }
            if (!_mainForm.Visible)
            {
                _mainForm.Bounds = RestoreVisibleBounds(_mainForm.Bounds, _mainForm.DesiredExpandedSize);
                _mainForm.Show();
            }
            _collapsed = false;
            _mainForm.WindowState = FormWindowState.Normal;
            _mainForm.Activate();
            _mainForm.BringToFront();
            _mainForm.MarkRead();
            SaveWindowSettings(_mainForm.Bounds, false);
        }

        internal void ActivateFromExternalLaunch()
        {
            if (_exiting || _mainForm.IsDisposed || _mainForm.Disposing || !_mainForm.IsHandleCreated)
            {
                return;
            }
            try
            {
                _mainForm.BeginInvoke((Action)ShowMain);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void HideToTray()
        {
            if (_exiting)
            {
                return;
            }
            if (_mainForm.Visible)
            {
                SaveWindowSettings(_mainForm.Bounds, false);
            }
            _mainForm.Hide();
            _edgeButton.Hide();
            _collapsed = false;
            if (!_shownTrayHint)
            {
                _shownTrayHint = true;
                _notifyIcon.BalloonTipTitle = "TransportHub 仍在运行";
                _notifyIcon.BalloonTipText = "双击托盘图标即可重新打开。";
                _notifyIcon.ShowBalloonTip(2500);
            }
        }

        private void UpdateEdgeState()
        {
            var status = _statusService.Current;
            _edgeButton.SetState(status.Running, _mainForm.UnreadCount, _mainForm.TransferProgress);
            var state = !status.Running
                ? "未连接"
                : status.TotalDevices == 0
                    ? "本机"
                    : status.OnlineDevices + "/" + status.TotalDevices + " 在线";
            _notifyIcon.Text = TruncateNotifyText("TransportHub · " + state + " · " + status.Detail);
        }

        private void HandleStatusChanged(object sender, EventArgs e)
        {
            if (_mainForm.IsDisposed || _mainForm.Disposing || !_mainForm.IsHandleCreated)
            {
                return;
            }
            try
            {
                _mainForm.BeginInvoke((Action)UpdateEdgeState);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void HandleDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (_mainForm.IsDisposed || _mainForm.Disposing || !_mainForm.IsHandleCreated)
            {
                return;
            }
            try
            {
                _mainForm.BeginInvoke((Action)delegate
                {
                    if (_edgeButton.Visible)
                    {
                        _edgeButton.RepositionToVisibleWorkArea();
                    }
                    else if (_mainForm.Visible)
                    {
                        _mainForm.Bounds = RestoreVisibleBounds(_mainForm.Bounds, _mainForm.DesiredExpandedSize);
                    }
                });
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void OpenSyncRoot()
        {
            Directory.CreateDirectory(_syncthing.RootPath);
            Process.Start(new ProcessStartInfo(_syncthing.RootPath) { UseShellExecute = true });
        }

        private async void ExitApplication(bool closeMainForm = true)
        {
            if (_exiting)
            {
                return;
            }
            _exiting = true;
            var bounds = _mainForm.Bounds;
            SaveWindowSettings(bounds, _collapsed);
            _notifyIcon.Visible = false;
            _edgeButton.Hide();
            await _mainForm.ShutdownAsync(TimeSpan.FromSeconds(30));
            if (closeMainForm && !_mainForm.IsDisposed)
            {
                _mainForm.RequestCloseForExit();
            }
            ExitThread();
        }

        private WindowSettings LoadWindowSettings()
        {
            var fallback = CreateDefaultBounds(_mainForm.DesiredExpandedSize);
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, false))
                {
                    if (key == null)
                    {
                        return new WindowSettings { Bounds = fallback, Collapsed = false };
                    }
                    var x = ReadInt(key, "X", fallback.X);
                    var y = ReadInt(key, "Y", fallback.Y);
                    var width = ReadInt(key, "Width", fallback.Width);
                    var height = ReadInt(key, "Height", fallback.Height);
                    var collapsed = ReadInt(key, "Collapsed", 0) == 1;
                    var requested = new Rectangle(x, y, width, height);
                    if (ReadInt(key, "LayoutVersion", 0) != WindowLayoutVersion)
                    {
                        var desired = _mainForm.DesiredExpandedSize;
                        requested = new Rectangle(
                            requested.Right - desired.Width,
                            requested.Top + (requested.Height - desired.Height) / 2,
                            desired.Width,
                            desired.Height);
                    }
                    return new WindowSettings
                    {
                        Bounds = RestoreVisibleBounds(requested, _mainForm.DesiredExpandedSize),
                        Collapsed = collapsed
                    };
                }
            }
            catch (Exception)
            {
                return new WindowSettings { Bounds = fallback, Collapsed = false };
            }
        }

        private void SaveWindowSettings(Rectangle bounds, bool collapsed)
        {
            try
            {
                bounds = RestoreVisibleBounds(bounds, _mainForm.DesiredExpandedSize);
                using (var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath))
                {
                    if (key == null)
                    {
                        return;
                    }
                    key.SetValue("X", bounds.X, RegistryValueKind.DWord);
                    key.SetValue("Y", bounds.Y, RegistryValueKind.DWord);
                    key.SetValue("Width", bounds.Width, RegistryValueKind.DWord);
                    key.SetValue("Height", bounds.Height, RegistryValueKind.DWord);
                    key.SetValue("Collapsed", collapsed ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue("LayoutVersion", WindowLayoutVersion, RegistryValueKind.DWord);
                }
            }
            catch (Exception)
            {
                // Window placement is a convenience; failure must not stop transfers.
            }
        }

        private static Rectangle CreateDefaultBounds(Size desired)
        {
            var work = Screen.PrimaryScreen.WorkingArea;
            var width = Math.Min(desired.Width, work.Width - 16);
            var height = Math.Min(desired.Height, work.Height - 16);
            return new Rectangle(work.Right - width - 16, work.Top + Math.Max(8, (work.Height - height) / 2), width, height);
        }

        private static Rectangle RestoreVisibleBounds(Rectangle requested, Size desired)
        {
            var minimumWidth = Math.Max(320, (int)Math.Round(desired.Width * 0.84));
            var maximumWidth = Math.Max(minimumWidth, (int)Math.Round(desired.Width * 1.52));
            var minimumHeight = Math.Max(440, (int)Math.Round(desired.Height * 0.75));
            var maximumHeight = Math.Max(minimumHeight, (int)Math.Round(desired.Height * 1.50));
            var validWidth = requested.Width >= minimumWidth && requested.Width <= maximumWidth ? requested.Width : desired.Width;
            var validHeight = requested.Height >= minimumHeight && requested.Height <= maximumHeight ? requested.Height : desired.Height;
            var center = new Point(requested.Left + requested.Width / 2, requested.Top + requested.Height / 2);
            var screen = Screen.AllScreens.FirstOrDefault(item => item.WorkingArea.Contains(center))
                ?? Screen.AllScreens.FirstOrDefault(item => item.WorkingArea.IntersectsWith(requested))
                ?? Screen.PrimaryScreen;
            var work = screen.WorkingArea;
            var width = Math.Min(validWidth, Math.Max(minimumWidth, work.Width - 16));
            var height = Math.Min(validHeight, Math.Max(minimumHeight, work.Height - 16));
            var x = Math.Max(work.Left + 8, Math.Min(work.Right - width - 8, requested.X));
            var y = Math.Max(work.Top + 8, Math.Min(work.Bottom - height - 8, requested.Y));
            return new Rectangle(x, y, width, height);
        }

        private static int ReadInt(RegistryKey key, string name, int fallback)
        {
            var value = key.GetValue(name);
            return value is int ? (int)value : fallback;
        }

        private static string TruncateNotifyText(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return "TransportHub";
            }
            return value.Length <= 63 ? value : value.Substring(0, 60) + "...";
        }

        private sealed class WindowSettings
        {
            internal Rectangle Bounds { get; set; }
            internal bool Collapsed { get; set; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TransportHub.Desktop.Core;
using TransportHub.Desktop.Interop;
using TransportHub.Desktop.Models;
using TransportHub.Desktop.Services;
using TransportHub.Desktop.UI;
using NativeWindowHelper = TransportHub.Desktop.Interop.NativeWindow;

namespace TransportHub.Desktop.Forms
{
    internal sealed class MainForm : Form
    {
        private readonly SyncthingContext _context;
        private readonly TimelineStore _timelineStore;
        private readonly TransferService _transferService;
        private readonly SyncthingStatusService _statusService;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly HashSet<string> _knownMessageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly FlowLayoutPanel _timeline;
        private readonly Label _statusLabel;
        private readonly Label _onlineLabel;
        private readonly TextBox _composer;
        private readonly Label _placeholder;
        private readonly RoundedPanel _composerSurface;
        private readonly Button _sendButton;
        private readonly ContextMenuStrip _attachmentMenu;
        private readonly System.Windows.Forms.Timer _refreshTimer;
        private readonly System.Windows.Forms.Timer _statusRestoreTimer;
        private FileSystemWatcher _watcher;
        private string _timelineSignature = String.Empty;
        private string _lastNetworkDetail = "正在连接 Syncthing";
        private bool _initialTimelineLoad = true;
        private bool _allowClose;
        private bool _resourcesDisposed;
        private int _reloadPending = 1;
        private int _refreshTickCount;
        private int _unreadCount;
        private int _transferProgress = -1;
        private int _activeTransfers;
        private int _recoveringOrphans;
        private int _backfillingReceipts;
        private Action _layoutHeader;

        internal MainForm(
            SyncthingContext context,
            TimelineStore timelineStore,
            TransferService transferService,
            SyncthingStatusService statusService)
        {
            _context = context ?? throw new ArgumentNullException("context");
            _timelineStore = timelineStore ?? throw new ArgumentNullException("timelineStore");
            _transferService = transferService ?? throw new ArgumentNullException("transferService");
            _statusService = statusService ?? throw new ArgumentNullException("statusService");

            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Theme.Panel;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            TopMost = true;
            DoubleBuffered = true;
            AllowDrop = true;
            Text = "TransportHub";
            Icon = NativeWindowHelper.CreateApplicationIcon(64);
            ClientSize = new Size(ScaleValue(330), ScaleValue(480));
            MinimumSize = new Size(ScaleValue(300), ScaleValue(400));
            MaximumSize = new Size(ScaleValue(500), ScaleValue(780));

            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = ScaleValue(46),
                BackColor = Color.White,
                Padding = new Padding(ScaleValue(12), ScaleValue(6), ScaleValue(8), ScaleValue(5))
            };
            header.MouseDown += HeaderMouseDown;

            var title = new Label
            {
                AutoSize = true,
                Text = "全部电脑",
                Font = Theme.Font(9.7f, FontStyle.Bold),
                ForeColor = Theme.Ink,
                Location = new Point(ScaleValue(12), ScaleValue(5)),
                BackColor = Color.Transparent
            };
            title.MouseDown += HeaderMouseDown;
            header.Controls.Add(title);

            _statusLabel = new Label
            {
                AutoEllipsis = true,
                Text = _lastNetworkDetail,
                Font = Theme.Font(7.7f),
                ForeColor = Theme.Muted,
                Location = new Point(ScaleValue(12), ScaleValue(25)),
                Size = new Size(ScaleValue(212), ScaleValue(16)),
                BackColor = Color.Transparent
            };
            _statusLabel.MouseDown += HeaderMouseDown;
            header.Controls.Add(_statusLabel);

            _onlineLabel = new Label
            {
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "未连接",
                Font = Theme.Font(7.2f, FontStyle.Bold),
                ForeColor = Theme.Amber,
                BackColor = Theme.AmberSoft,
                Size = new Size(ScaleValue(56), ScaleValue(22)),
                Location = new Point(0, ScaleValue(14)),
                Visible = false
            };
            header.Controls.Add(_onlineLabel);

            var collapse = CreateHeaderButton("—", "折叠到屏幕边缘");
            collapse.Location = new Point(0, ScaleValue(8));
            collapse.Click += delegate { CollapseRequested?.Invoke(this, EventArgs.Empty); };
            header.Controls.Add(collapse);

            var hide = CreateHeaderButton("×", "隐藏到系统托盘");
            hide.Location = new Point(0, ScaleValue(8));
            hide.Click += delegate { HideRequested?.Invoke(this, EventArgs.Empty); };
            header.Controls.Add(hide);
            _layoutHeader = delegate
            {
                hide.Left = Math.Max(0, header.ClientSize.Width - ScaleValue(34));
                collapse.Left = Math.Max(0, hide.Left - ScaleValue(30));
                var statusRight = collapse.Left - ScaleValue(6);
                if (_onlineLabel.Visible)
                {
                    _onlineLabel.Left = Math.Max(0, collapse.Left - ScaleValue(61));
                    statusRight = _onlineLabel.Left - ScaleValue(7);
                }
                _statusLabel.Width = Math.Max(ScaleValue(72), statusRight - _statusLabel.Left);
            };
            header.Resize += delegate { _layoutHeader(); };
            _layoutHeader();
            Controls.Add(header);

            var divider = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Line };
            Controls.Add(divider);

            var composerHost = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = ScaleValue(54),
                BackColor = Color.White,
                Padding = new Padding(ScaleValue(8), ScaleValue(7), ScaleValue(8), ScaleValue(7))
            };
            var composerDivider = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Line };
            composerHost.Controls.Add(composerDivider);

            _attachmentMenu = new ContextMenuStrip
            {
                Font = Theme.Font(8.7f),
                ShowImageMargin = false
            };
            _attachmentMenu.Items.Add("选择文件", null, delegate { PromptForFiles(); });
            _attachmentMenu.Items.Add("选择文件夹", null, delegate { PromptForFolder(); });

            var attachButton = new RoundButton
            {
                Text = "\uD83D\uDCCE",
                Font = new Font("Segoe UI Symbol", 15f, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Theme.Purple,
                BackColor = Theme.PurpleSoft,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(ScaleValue(36), ScaleValue(36)),
                Location = new Point(ScaleValue(8), ScaleValue(9)),
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
                AccessibleName = "添加附件"
            };
            attachButton.FlatAppearance.BorderSize = 0;
            attachButton.Click += ShowAttachmentMenu;
            composerHost.Controls.Add(attachButton);

            _sendButton = new RoundButton
            {
                Text = "➤",
                Font = new Font("Segoe UI Symbol", 14f, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.White,
                BackColor = Theme.Purple,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(ScaleValue(36), ScaleValue(36)),
                Location = new Point(0, ScaleValue(9)),
                AccessibleName = "发送"
            };
            _sendButton.FlatAppearance.BorderSize = 0;
            _sendButton.Click += delegate { SendComposerText(); };
            composerHost.Controls.Add(_sendButton);

            _composerSurface = new RoundedPanel
            {
                BackColor = Theme.Surface,
                BorderColor = Theme.Line,
                Radius = ScaleValue(16),
                Location = new Point(ScaleValue(52), ScaleValue(8)),
                Size = new Size(ScaleValue(200), ScaleValue(38))
            };
            _composer = new TextBox
            {
                Multiline = true,
                BorderStyle = BorderStyle.None,
                AcceptsReturn = true,
                AcceptsTab = false,
                BackColor = Theme.Surface,
                ForeColor = Theme.Ink,
                Font = Theme.Font(9.4f),
                Location = new Point(ScaleValue(11), ScaleValue(6)),
                Size = new Size(_composerSurface.Width - ScaleValue(22), ScaleValue(25)),
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
                ScrollBars = ScrollBars.None,
                MaxLength = 16000
            };
            _composer.TextChanged += delegate { UpdateComposerPlaceholder(); };
            _composer.KeyDown += ComposerKeyDown;
            _composerSurface.Controls.Add(_composer);
            _placeholder = new Label
            {
                Text = "输入消息…",
                AutoSize = true,
                ForeColor = Color.FromArgb(154, 159, 173),
                BackColor = Theme.Surface,
                Font = Theme.Font(9.4f),
                Location = new Point(ScaleValue(11), ScaleValue(7)),
                Cursor = Cursors.IBeam
            };
            _placeholder.Click += delegate { _composer.Focus(); };
            _composerSurface.Controls.Add(_placeholder);
            _placeholder.BringToFront();
            composerHost.Controls.Add(_composerSurface);
            _composerSurface.Resize += delegate
            {
                _composer.Width = Math.Max(ScaleValue(40), _composerSurface.ClientSize.Width - ScaleValue(22));
            };
            composerHost.Resize += delegate
            {
                _sendButton.Left = Math.Max(0, composerHost.ClientSize.Width - ScaleValue(44));
                _composerSurface.Width = Math.Max(
                    ScaleValue(100),
                    _sendButton.Left - _composerSurface.Left - ScaleValue(7));
            };
            Controls.Add(composerHost);

            _timeline = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Theme.Surface,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(ScaleValue(8), ScaleValue(7), ScaleValue(8), ScaleValue(7)),
                TabStop = true
            };
            Controls.Add(_timeline);
            _timeline.BringToFront();

            DragEnter += HandleDragEnter;
            DragOver += HandleDragEnter;
            DragDrop += HandleDragDrop;
            Resize += HandleResize;
            Shown += delegate
            {
                RefreshTimeline(true);
                ScrollToLatest();
                RecoverOrphanedLocalItemsAsync();
            };
            VisibleChanged += delegate
            {
                if (Visible)
                {
                    MarkRead();
                    Interlocked.Exchange(ref _reloadPending, 1);
                }
            };

            _transferService.ProgressChanged += TransferProgressChanged;
            _statusService.StatusChanged += SyncthingStatusChanged;

            _refreshTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _refreshTimer.Tick += delegate
            {
                _refreshTickCount++;
                if (Interlocked.Exchange(ref _reloadPending, 0) != 0 || _refreshTickCount % 20 == 0)
                {
                    RefreshTimeline(false);
                }
            };
            _refreshTimer.Start();

            _statusRestoreTimer = new System.Windows.Forms.Timer { Interval = 3200 };
            _statusRestoreTimer.Tick += delegate
            {
                _statusRestoreTimer.Stop();
                _statusLabel.Text = _lastNetworkDetail;
                _statusLabel.ForeColor = Theme.Muted;
            };

            StartWatcher();
            HandleResize(this, EventArgs.Empty);
            RefreshTimeline(true);
        }

        internal event EventHandler CollapseRequested;
        internal event EventHandler HideRequested;
        internal event EventHandler StateChanged;

        internal int UnreadCount { get { return _unreadCount; } }
        internal int TransferProgress { get { return _transferProgress; } }
        internal Size DesiredExpandedSize { get { return new Size(ScaleValue(330), ScaleValue(480)); } }

        internal void QueuePaths(IEnumerable<string> paths)
        {
            var snapshot = (paths ?? Enumerable.Empty<string>())
                .Where(path => !String.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (snapshot.Length == 0)
            {
                return;
            }
            SendPathsAsync(snapshot);
        }

        internal void PromptForFiles()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "选择要发送的文件",
                Multiselect = true,
                CheckFileExists = true,
                RestoreDirectory = true
            })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    QueuePaths(dialog.FileNames);
                }
            }
        }

        internal void PromptForFolder()
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = "选择要发送的文件夹",
                ShowNewFolderButton = false
            })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    QueuePaths(new[] { dialog.SelectedPath });
                }
            }
        }

        internal void MarkRead()
        {
            if (_unreadCount == 0)
            {
                return;
            }
            _unreadCount = 0;
            RaiseStateChanged();
        }

        internal void RequestCloseForExit()
        {
            _allowClose = true;
            Close();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.V && (keyData & Keys.Control) == Keys.Control && ContainsFocus)
            {
                ClipboardPayload payload;
                ClipboardReadStatus status;
                string error;
                if (ClipboardPayloadReader.TryReadForForegroundWindow(Handle, out payload, out status, out error))
                {
                    if (payload.Kind == ClipboardPayloadKind.FileDrop)
                    {
                        QueuePaths(payload.FilePaths);
                        return true;
                    }
                    if (payload.Kind == ClipboardPayloadKind.Image)
                    {
                        SendClipboardImageAsync(payload);
                        return true;
                    }
                    if (payload.Kind == ClipboardPayloadKind.Url)
                    {
                        CreateLinkMessage(payload.Url.AbsoluteUri, payload.Text);
                        return true;
                    }
                    if (payload.Kind == ClipboardPayloadKind.Text && _composer.Focused)
                    {
                        _composer.SelectedText = payload.Text ?? String.Empty;
                        return true;
                    }
                }
                else if (status == ClipboardReadStatus.TooLarge || status == ClipboardReadStatus.InvalidData)
                {
                    ShowTransientStatus(String.IsNullOrWhiteSpace(error) ? "无法读取剪贴板内容" : error, Theme.Red);
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_resourcesDisposed)
            {
                _resourcesDisposed = true;
                _lifetime.Cancel();
                _lifetime.Dispose();
                _refreshTimer?.Stop();
                _refreshTimer?.Dispose();
                _statusRestoreTimer?.Stop();
                _statusRestoreTimer?.Dispose();
                if (_watcher != null)
                {
                    _watcher.Dispose();
                    _watcher = null;
                }
                _transferService.ProgressChanged -= TransferProgressChanged;
                _statusService.StatusChanged -= SyncthingStatusChanged;
                if (!_attachmentMenu.IsDisposed)
                {
                    if (_attachmentMenu.Visible)
                    {
                        _attachmentMenu.Close(ToolStripDropDownCloseReason.CloseCalled);
                    }
                    _attachmentMenu.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        private void StartWatcher()
        {
            try
            {
                if (_watcher != null)
                {
                    _watcher.Dispose();
                    _watcher = null;
                }
                _watcher = new FileSystemWatcher(_context.RootPath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler changed = delegate { Interlocked.Exchange(ref _reloadPending, 1); };
                RenamedEventHandler renamed = delegate { Interlocked.Exchange(ref _reloadPending, 1); };
                _watcher.Created += changed;
                _watcher.Changed += changed;
                _watcher.Deleted += changed;
                _watcher.Renamed += renamed;
                _watcher.Error += delegate
                {
                    Interlocked.Exchange(ref _reloadPending, 1);
                    PostToUi(StartWatcher);
                };
            }
            catch (Exception)
            {
                _watcher = null;
            }
        }

        private void RefreshTimeline(bool force)
        {
            IReadOnlyList<TimelineMessage> messages;
            try
            {
                messages = _timelineStore.LoadRecentMessages(300);
            }
            catch (Exception exception)
            {
                ShowTransientStatus("消息列表暂时不可用：" + exception.Message, Theme.Red);
                return;
            }

            var attachmentPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var message in messages.Where(item => item.Attachment != null))
            {
                attachmentPaths[message.Id] = ResolveAvailableAttachment(message.Attachment);
            }

            foreach (var message in messages.Where(item => !IsOutgoing(item)))
            {
                string receivedAttachment;
                if (message.Attachment != null &&
                    (!_statusService.Current.Running ||
                     !_statusService.Current.FolderIdle ||
                     !attachmentPaths.TryGetValue(message.Id, out receivedAttachment) ||
                     receivedAttachment == null))
                {
                    continue;
                }
                try
                {
                    _timelineStore.CreateDeliveryReceipt(message.Id);
                }
                catch (Exception)
                {
                    // A receipt is helpful but never blocks rendering a valid message.
                }
            }
            QueueReceiptBackfill();

            var viewModels = new List<TimelineItemViewModel>();
            var signatureParts = new List<string>();
            foreach (var message in messages)
            {
                var outgoing = IsOutgoing(message);
                DeliverySummary summary = null;
                try { summary = _timelineStore.GetDeliverySummary(message); } catch (Exception) { }
                var delivery = BuildDeliveryText(message, outgoing, summary);
                signatureParts.Add(message.Id + ":" + (summary == null ? "-" : summary.DeliveredCount.ToString()));

                string absolutePath = null;
                string relativePath = null;
                string mimeType = null;
                long size = 0;
                if (message.Attachment != null)
                {
                    relativePath = message.Attachment.RelativePath;
                    mimeType = message.Attachment.MimeType;
                    size = message.Attachment.SizeBytes;
                    attachmentPaths.TryGetValue(message.Id, out absolutePath);
                }
                signatureParts.Add("attachment:" + message.Id + ":" +
                    (absolutePath == null ? "pending" : "available"));

                viewModels.Add(new TimelineItemViewModel
                {
                    MessageId = message.Id,
                    Kind = message.Kind,
                    IsOutgoing = outgoing,
                    SenderName = message.SenderName,
                    Text = message.Text,
                    Url = message.LinkUrl,
                    RelativePath = relativePath,
                    AbsolutePath = absolutePath,
                    MimeType = mimeType,
                    SizeBytes = size,
                    Timestamp = message.CreatedUtc.ToLocalTime(),
                    DeliverySummary = delivery
                });
            }

            var signature = String.Join("|", signatureParts);
            var incomingNew = messages.Count(message =>
                !IsOutgoing(message) && !_knownMessageIds.Contains(message.Id));
            foreach (var message in messages)
            {
                _knownMessageIds.Add(message.Id);
            }
            if (!_initialTimelineLoad && !Visible && incomingNew > 0)
            {
                _unreadCount += incomingNew;
                RaiseStateChanged();
            }
            _initialTimelineLoad = false;

            if (!force && String.Equals(signature, _timelineSignature, StringComparison.Ordinal))
            {
                return;
            }
            _timelineSignature = signature;
            var wasNearBottom = IsNearTimelineBottom();
            _timeline.SuspendLayout();
            try
            {
                foreach (Control control in _timeline.Controls.Cast<Control>().ToArray())
                {
                    control.Dispose();
                }
                _timeline.Controls.Clear();

                if (viewModels.Count == 0)
                {
                    var empty = new Label
                    {
                        Text = "拖入文件或输入消息",
                        ForeColor = Theme.Muted,
                        Font = Theme.Font(8.5f),
                        TextAlign = ContentAlignment.MiddleCenter,
                        Height = ScaleValue(36),
                        Margin = new Padding(0, ScaleValue(56), 0, 0)
                    };
                    empty.Width = TimelineContentWidth();
                    _timeline.Controls.Add(empty);
                }
                else
                {
                    foreach (var model in viewModels)
                    {
                        var item = new TimelineItemControl(model);
                        item.SetAvailableWidth(TimelineContentWidth());
                        item.RevealRequested += TimelineRevealRequested;
                        item.LinkRequested += TimelineLinkRequested;
                        _timeline.Controls.Add(item);
                    }
                }
            }
            finally
            {
                _timeline.ResumeLayout(true);
            }
            if (wasNearBottom || force)
            {
                BeginInvoke((Action)ScrollToLatest);
            }
        }

        private string ResolveAvailableAttachment(TimelineAttachment attachment)
        {
            if (attachment == null)
            {
                return null;
            }
            try
            {
                var candidate = PathSafety.ResolveUnderRoot(_context.RootPath, attachment.RelativePath);
                PathSafety.EnsureNoReparsePoints(_context.RootPath, candidate);
                if (attachment.IsDirectory)
                {
                    return Directory.Exists(candidate) ? candidate : null;
                }
                return File.Exists(candidate) && new FileInfo(candidate).Length == attachment.SizeBytes
                    ? candidate
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void QueueReceiptBackfill()
        {
            if (Interlocked.Exchange(ref _backfillingReceipts, 1) != 0)
            {
                return;
            }
            Task.Run(delegate
            {
                try
                {
                    foreach (var message in _timelineStore.LoadRecentMessages(TimelineProtocol.MaximumRecentMessageCount))
                    {
                        if (_lifetime.IsCancellationRequested || IsOutgoing(message))
                        {
                            continue;
                        }
                        if (message.Attachment != null)
                        {
                            var available = ResolveAvailableAttachment(message.Attachment);
                            if (available == null || !_statusService.Current.Running || !_statusService.Current.FolderIdle)
                            {
                                continue;
                            }
                        }
                        _timelineStore.CreateDeliveryReceipt(message.Id);
                    }
                }
                catch (Exception)
                {
                    // The foreground refresh remains authoritative; this is a durable backlog sweep.
                }
                finally
                {
                    Interlocked.Exchange(ref _backfillingReceipts, 0);
                }
            });
        }

        private string BuildDeliveryText(TimelineMessage message, bool outgoing, DeliverySummary summary)
        {
            if (!outgoing)
            {
                return String.Empty;
            }
            if (summary == null || summary.TargetCount == 0)
            {
                return String.Empty;
            }
            return summary.IsDeliveredToAll
                ? "✓ " + summary.DeliveredCount + "/" + summary.TargetCount
                : "同步 " + summary.DeliveredCount + "/" + summary.TargetCount;
        }

        private bool IsOutgoing(TimelineMessage message)
        {
            return String.Equals(message.SenderDeviceId, _context.LocalDeviceId, StringComparison.OrdinalIgnoreCase);
        }

        private async void SendPathsAsync(string[] paths)
        {
            foreach (var path in paths)
            {
                TransferResult result = null;
                try
                {
                    BeginTransfer(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
                    result = await _transferService.SendPathAsync(path, _lifetime.Token);
                    _timelineStore.CreateAttachment(
                        result.RelativePath,
                        result.MimeType,
                        result.Size,
                        result.Sha256,
                        TargetDeviceIds());
                    Interlocked.Exchange(ref _reloadPending, 1);
                    RefreshTimeline(true);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    ShowTransientStatus("发送失败：" + exception.Message, Theme.Red);
                    if (result != null)
                    {
                        RecoverOrphanedLocalItemsAsync();
                    }
                }
                finally
                {
                    EndTransfer();
                }
            }
        }

        private async void SendClipboardImageAsync(ClipboardPayload payload)
        {
            try
            {
                BeginTransfer("粘贴的图片");
                var name = "粘贴图片-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + (payload.SuggestedFileExtension ?? ".png");
                var result = await _transferService.SendBytesAsync(
                    payload.GetImageBytes(),
                    name,
                    payload.ImageMediaType,
                    _lifetime.Token);
                _timelineStore.CreateAttachment(
                    result.RelativePath,
                    result.MimeType,
                    result.Size,
                    result.Sha256,
                    TargetDeviceIds());
                Interlocked.Exchange(ref _reloadPending, 1);
                RefreshTimeline(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                ShowTransientStatus("图片发送失败：" + exception.Message, Theme.Red);
            }
            finally
            {
                EndTransfer();
            }
        }

        private void SendComposerText()
        {
            var text = _composer.Text;
            if (String.IsNullOrWhiteSpace(text))
            {
                _composer.Focus();
                return;
            }
            try
            {
                Uri url;
                if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out url) &&
                    (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
                {
                    _timelineStore.CreateLink(url.AbsoluteUri, text.Trim(), TargetDeviceIds());
                }
                else
                {
                    _timelineStore.CreateText(text.Trim(), TargetDeviceIds());
                }
                _composer.Clear();
                RefreshTimeline(true);
            }
            catch (Exception exception)
            {
                ShowTransientStatus("消息发送失败：" + exception.Message, Theme.Red);
            }
        }

        private void CreateLinkMessage(string url, string text)
        {
            try
            {
                _timelineStore.CreateLink(url, String.IsNullOrWhiteSpace(text) ? url : text, TargetDeviceIds());
                RefreshTimeline(true);
            }
            catch (Exception exception)
            {
                ShowTransientStatus("链接发送失败：" + exception.Message, Theme.Red);
            }
        }

        private IEnumerable<string> TargetDeviceIds()
        {
            try
            {
                _context.RefreshTargetDevices();
            }
            catch (Exception)
            {
            }
            var devices = new HashSet<string>(
                _context.TargetDevices.Select(device => device.Id),
                StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var deviceId in _timelineStore.LoadKnownDeviceIds())
                {
                    devices.Add(deviceId);
                }
            }
            catch (Exception)
            {
                // Direct Syncthing peers remain a safe fallback if the registry is unavailable.
            }
            devices.Remove(_context.LocalDeviceId);
            return devices.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        private async void RecoverOrphanedLocalItemsAsync()
        {
            if (Interlocked.Exchange(ref _recoveringOrphans, 1) != 0)
            {
                return;
            }
            try
            {
                await Task.Delay(1500, _lifetime.Token);
                if (_activeTransfers != 0 || _lifetime.IsCancellationRequested)
                {
                    return;
                }
                var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var relativePath in _timelineStore.LoadAllAttachmentPaths())
                {
                    try
                    {
                        referenced.Add(PathSafety.ResolveUnderRoot(_context.RootPath, relativePath));
                    }
                    catch (Exception)
                    {
                    }
                }
                var candidates = Directory.EnumerateFileSystemEntries(_context.MachineFolder, "*", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFullPath)
                    .Where(path => !referenced.Contains(path))
                    .ToArray();
                if (candidates.Length == 0)
                {
                    return;
                }
                var targets = TargetDeviceIds().ToArray();
                var recovered = await Task.Run(() => RecoverCandidates(candidates, targets, _lifetime.Token), _lifetime.Token);
                if (recovered > 0)
                {
                    RefreshTimeline(true);
                    ShowTransientStatus("已恢复 " + recovered + " 个未完成的投递记录", Theme.Green);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                ShowTransientStatus("投递记录恢复失败：" + exception.Message, Theme.Red);
            }
            finally
            {
                Interlocked.Exchange(ref _recoveringOrphans, 0);
            }
        }

        private int RecoverCandidates(IEnumerable<string> candidates, string[] targets, CancellationToken cancellationToken)
        {
            var recovered = 0;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(candidate);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                    var relative = PathSafety.GetRelativePathUnderRoot(_context.RootPath, candidate);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        var size = ComputeSafeDirectorySize(candidate, cancellationToken);
                        _timelineStore.CreateAttachment(relative, "inode/directory", size, String.Empty, targets);
                    }
                    else
                    {
                        var before = new FileInfo(candidate);
                        var length = before.Length;
                        string hash;
                        using (var algorithm = SHA256.Create())
                        using (var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            hash = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", String.Empty).ToLowerInvariant();
                        }
                        var after = new FileInfo(candidate);
                        if (after.Length != length || after.LastWriteTimeUtc != before.LastWriteTimeUtc)
                        {
                            continue;
                        }
                        _timelineStore.CreateAttachment(relative, GuessRecoveredMimeType(candidate), length, hash, targets);
                    }
                    recovered++;
                }
                catch (Exception exception) when (
                    exception is ArgumentException ||
                    exception is IOException ||
                    exception is UnauthorizedAccessException ||
                    exception is NotSupportedException ||
                    exception is SecurityException)
                {
                }
            }
            return recovered;
        }

        private static long ComputeSafeDirectorySize(string root, CancellationToken cancellationToken)
        {
            long total = 0;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("目录包含不支持的重解析点。");
                }
                foreach (var file in Directory.GetFiles(directory))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException("目录包含不支持的重解析点。");
                    }
                    total = checked(total + new FileInfo(file).Length);
                }
                foreach (var child in Directory.GetDirectories(directory))
                {
                    pending.Push(child);
                }
            }
            return total;
        }

        private static string GuessRecoveredMimeType(string path)
        {
            switch ((Path.GetExtension(path) ?? String.Empty).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                case ".pdf": return "application/pdf";
                case ".txt": return "text/plain";
                case ".mp4": return "video/mp4";
                default: return "application/octet-stream";
            }
        }

        private void BeginTransfer(string displayName)
        {
            _activeTransfers++;
            _transferProgress = 0;
            ShowTransientStatus("正在投递：" + displayName, Theme.Purple);
            RaiseStateChanged();
        }

        private void EndTransfer()
        {
            _activeTransfers = Math.Max(0, _activeTransfers - 1);
            if (_activeTransfers == 0)
            {
                _transferProgress = -1;
            }
            RaiseStateChanged();
        }

        private void TransferProgressChanged(object sender, TransferProgressInfo progress)
        {
            PostToUi(delegate
            {
                _transferProgress = progress.Percent;
                _statusLabel.Text = "正在投递 " + progress.DisplayName + " · " + progress.Percent + "%";
                _statusLabel.ForeColor = Theme.Purple;
                RaiseStateChanged();
            });
        }

        private void SyncthingStatusChanged(object sender, EventArgs e)
        {
            PostToUi(UpdateNetworkStatus);
        }

        private void UpdateNetworkStatus()
        {
            var status = _statusService.Current;
            _lastNetworkDetail = status.Detail;
            if (status.Running && status.FolderIdle)
            {
                Interlocked.Exchange(ref _reloadPending, 1);
            }
            if (!_statusRestoreTimer.Enabled && _activeTransfers == 0)
            {
                _statusLabel.Text = _lastNetworkDetail;
                _statusLabel.ForeColor = Theme.Muted;
            }
            if (!status.Running)
            {
                _onlineLabel.Visible = true;
                _onlineLabel.Text = "未连接";
                _onlineLabel.ForeColor = Theme.Amber;
                _onlineLabel.BackColor = Theme.AmberSoft;
            }
            else if (status.TotalDevices == 0)
            {
                _onlineLabel.Visible = false;
            }
            else
            {
                _onlineLabel.Visible = true;
                _onlineLabel.Text = status.OnlineDevices + "/" + status.TotalDevices + " 在线";
                _onlineLabel.ForeColor = status.OnlineDevices > 0 ? Theme.Green : Theme.Amber;
                _onlineLabel.BackColor = status.OnlineDevices > 0 ? Theme.GreenSoft : Theme.AmberSoft;
            }
            if (_layoutHeader != null)
            {
                _layoutHeader();
            }
            RaiseStateChanged();
        }

        private void ShowTransientStatus(string text, Color color)
        {
            if (InvokeRequired)
            {
                PostToUi(() => ShowTransientStatus(text, color));
                return;
            }
            _statusLabel.Text = text;
            _statusLabel.ForeColor = color;
            _statusRestoreTimer.Stop();
            _statusRestoreTimer.Start();
        }

        private void TimelineRevealRequested(object sender, TimelineItemValueEventArgs e)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(e.Value))
                {
                    throw new FileNotFoundException("这个文件还没有同步到本机。");
                }
                var full = Path.GetFullPath(e.Value);
                var relative = PathSafety.GetRelativePathUnderRoot(_context.RootPath, full);
                var safe = PathSafety.ResolveUnderRoot(_context.RootPath, relative);
                PathSafety.EnsureNoReparsePoints(_context.RootPath, safe);
                string error;
                if (!ShellReveal.TryReveal(safe, out error))
                {
                    throw new InvalidOperationException(error);
                }
            }
            catch (Exception exception)
            {
                ShowTransientStatus(exception.Message, Theme.Red);
            }
        }

        private void TimelineLinkRequested(object sender, TimelineItemValueEventArgs e)
        {
            Uri uri;
            if (!Uri.TryCreate(e.Value, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                ShowTransientStatus("链接格式无效", Theme.Red);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                ShowTransientStatus("无法打开链接：" + exception.Message, Theme.Red);
            }
        }

        private void ComposerKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                SendComposerText();
            }
        }

        private void UpdateComposerPlaceholder()
        {
            _placeholder.Visible = _composer.TextLength == 0;
            _sendButton.BackColor = _composer.TextLength == 0 ? Theme.Purple2 : Theme.Purple;
        }

        private void ShowAttachmentMenu(object sender, EventArgs e)
        {
            var button = sender as Control;
            if (button == null || IsDisposed || Disposing || _attachmentMenu.IsDisposed)
            {
                return;
            }

            _attachmentMenu.Show(button, new Point(0, button.Height));
        }

        private void HandleDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }

        private void HandleDragDrop(object sender, DragEventArgs e)
        {
            var paths = e.Data == null ? null : e.Data.GetData(DataFormats.FileDrop) as string[];
            QueuePaths(paths);
        }

        private void HeaderMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                NativeWindowHelper.BeginWindowDrag(this);
            }
        }

        private void HandleResize(object sender, EventArgs e)
        {
            NativeWindowHelper.ApplyRoundedRegion(this, ScaleValue(16));
            if (_timeline == null)
            {
                return;
            }
            foreach (Control control in _timeline.Controls)
            {
                var item = control as TimelineItemControl;
                if (item != null)
                {
                    item.SetAvailableWidth(TimelineContentWidth());
                }
                else
                {
                    control.Width = TimelineContentWidth();
                }
            }
        }

        private int TimelineContentWidth()
        {
            return Math.Max(
                ScaleValue(220),
                _timeline.ClientSize.Width - _timeline.Padding.Horizontal - ScaleValue(20));
        }

        private bool IsNearTimelineBottom()
        {
            var vertical = _timeline.VerticalScroll;
            return vertical.Value + vertical.LargeChange >= vertical.Maximum - ScaleValue(24);
        }

        private void ScrollToLatest()
        {
            if (_timeline.Controls.Count > 0)
            {
                _timeline.ScrollControlIntoView(_timeline.Controls[_timeline.Controls.Count - 1]);
            }
        }

        private Button CreateHeaderButton(string text, string accessibleName)
        {
            var button = new Button
            {
                Text = text,
                AccessibleName = accessibleName,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Theme.Muted,
                Font = Theme.Font(11f),
                Size = new Size(ScaleValue(30), ScaleValue(30)),
                TabStop = false,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Theme.PurpleSoft;
            return button;
        }

        private void RaiseStateChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void PostToUi(Action action)
        {
            if (action == null || IsDisposed || Disposing || !IsHandleCreated)
            {
                return;
            }
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private int ScaleValue(int value)
        {
            using (var graphics = CreateGraphics())
            {
                return (int)Math.Round(value * graphics.DpiX / 96f);
            }
        }
    }

    internal sealed class RoundedPanel : Panel
    {
        internal int Radius { get; set; }
        internal Color BorderColor { get; set; }

        internal RoundedPanel()
        {
            DoubleBuffered = true;
            BorderColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(BackColor))
            {
                e.Graphics.FillRoundedRectangle(brush, ClientRectangle, Math.Max(1, Radius));
            }
            if (BorderColor != Color.Transparent)
            {
                using (var pen = new Pen(BorderColor))
                {
                    var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
                    e.Graphics.DrawRoundedRectangle(pen, bounds, Math.Max(1, Radius));
                }
            }
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            Invalidate();
        }
    }

    internal sealed class RoundButton : Button
    {
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            NativeWindowHelper.ApplyRoundedRegion(this, Math.Min(Width, Height));
        }
    }
}

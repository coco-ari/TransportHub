using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using TransportHub.Desktop.Models;
using TransportHub.Desktop.Services;
using TransportHub.Desktop.UI;

namespace TransportHub.Desktop.Forms
{
    internal sealed class ConnectionForm : Form
    {
        private readonly SyncthingContext _context;
        private readonly ConnectionService _service;
        private readonly TextBox _remoteCode;
        private readonly Button _connectButton;
        private readonly Label _feedback;
        private readonly FlowLayoutPanel _pendingList;
        private bool _working;

        internal ConnectionForm(SyncthingContext context, ConnectionService service)
        {
            _context = context ?? throw new ArgumentNullException("context");
            _service = service ?? throw new ArgumentNullException("service");

            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.White;
            ClientSize = new Size(ScaleValue(330), ScaleValue(390));
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Text = "连接电脑";
            TopMost = true;

            var title = new Label
            {
                AutoSize = true,
                Text = "连接电脑",
                Font = Theme.Font(12f, FontStyle.Bold),
                ForeColor = Theme.Ink,
                Location = new Point(ScaleValue(16), ScaleValue(14))
            };
            Controls.Add(title);

            var intro = new Label
            {
                AutoSize = false,
                Text = "在另一台电脑安装 TransportHub 后，互相连接一次即可自动同步。",
                Font = Theme.Font(7.8f),
                ForeColor = Theme.Muted,
                Location = new Point(ScaleValue(16), ScaleValue(42)),
                Size = new Size(ScaleValue(298), ScaleValue(34))
            };
            Controls.Add(intro);

            var localLabel = SectionLabel("本机连接码", 78);
            Controls.Add(localLabel);

            var localCode = new TextBox
            {
                ReadOnly = true,
                Multiline = true,
                WordWrap = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Theme.Surface,
                ForeColor = Theme.Ink,
                Font = new Font("Consolas", 7.5f, FontStyle.Regular, GraphicsUnit.Point),
                Location = new Point(ScaleValue(16), ScaleValue(99)),
                Size = new Size(ScaleValue(232), ScaleValue(50)),
                Text = _service.LocalConnectionCode
            };
            localCode.Select(0, 0);
            Controls.Add(localCode);

            var copy = ActionButton("复制", Theme.PurpleSoft, Theme.Purple);
            copy.Location = new Point(ScaleValue(256), ScaleValue(99));
            copy.Size = new Size(ScaleValue(58), ScaleValue(50));
            copy.Click += delegate
            {
                try
                {
                    Clipboard.SetText(_service.LocalConnectionCode);
                    SetFeedback("连接码已复制，发给另一台电脑。", Theme.Green);
                }
                catch (Exception exception)
                {
                    SetFeedback("复制失败：" + exception.Message, Theme.Red);
                }
            };
            Controls.Add(copy);

            var remoteLabel = SectionLabel("输入另一台电脑的连接码", 162);
            Controls.Add(remoteLabel);

            _remoteCode = new TextBox
            {
                Multiline = true,
                WordWrap = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White,
                ForeColor = Theme.Ink,
                Font = new Font("Consolas", 7.8f, FontStyle.Regular, GraphicsUnit.Point),
                Location = new Point(ScaleValue(16), ScaleValue(183)),
                Size = new Size(ScaleValue(232), ScaleValue(48))
            };
            Controls.Add(_remoteCode);

            _connectButton = ActionButton("连接", Theme.Purple, Color.White);
            _connectButton.Location = new Point(ScaleValue(256), ScaleValue(183));
            _connectButton.Size = new Size(ScaleValue(58), ScaleValue(48));
            _connectButton.Click += ConnectClicked;
            Controls.Add(_connectButton);

            _feedback = new Label
            {
                AutoEllipsis = true,
                Text = "连接码不是密码；新设备仍需由对方确认。",
                Font = Theme.Font(7.2f),
                ForeColor = Theme.Muted,
                Location = new Point(ScaleValue(16), ScaleValue(237)),
                Size = new Size(ScaleValue(298), ScaleValue(18))
            };
            Controls.Add(_feedback);

            var pendingLabel = SectionLabel("等待你确认", 266);
            Controls.Add(pendingLabel);

            _pendingList = new FlowLayoutPanel
            {
                AutoScroll = true,
                BackColor = Theme.Surface,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Location = new Point(ScaleValue(16), ScaleValue(288)),
                Size = new Size(ScaleValue(298), ScaleValue(86)),
                Padding = new Padding(ScaleValue(5))
            };
            Controls.Add(_pendingList);

            _service.PendingDevicesChanged += PendingDevicesChanged;
            FormClosed += delegate { _service.PendingDevicesChanged -= PendingDevicesChanged; };
            Shown += async delegate
            {
                RefreshPendingList();
                await _service.RefreshPendingAsync();
                RefreshPendingList();
                _remoteCode.Focus();
            };
        }

        private async void ConnectClicked(object sender, EventArgs e)
        {
            if (_working)
            {
                return;
            }
            _working = true;
            _connectButton.Enabled = false;
            SetFeedback("正在保存连接设置…", Theme.Purple);
            try
            {
                await _service.AddAndShareAsync(_remoteCode.Text);
                _context.RefreshTargetDevices();
                _remoteCode.Clear();
                SetFeedback("已发起连接，请在另一台电脑上点“接受”。", Theme.Green);
            }
            catch (Exception exception)
            {
                SetFeedback(FriendlyError(exception), Theme.Red);
            }
            finally
            {
                _working = false;
                _connectButton.Enabled = true;
            }
        }

        private void PendingDevicesChanged(object sender, EventArgs e)
        {
            if (IsDisposed || Disposing || !IsHandleCreated)
            {
                return;
            }
            try
            {
                BeginInvoke((Action)RefreshPendingList);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void RefreshPendingList()
        {
            if (IsDisposed || Disposing)
            {
                return;
            }
            while (_pendingList.Controls.Count > 0)
            {
                var control = _pendingList.Controls[0];
                _pendingList.Controls.RemoveAt(0);
                control.Dispose();
            }
            var pending = _service.PendingDevices.ToArray();
            if (pending.Length == 0)
            {
                _pendingList.Controls.Add(new Label
                {
                    Text = _context.TargetDevices.Count == 0 ? "暂无请求" : "已连接 " + _context.TargetDevices.Count + " 台电脑",
                    ForeColor = Theme.Muted,
                    Font = Theme.Font(8f),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Size = new Size(ScaleValue(272), ScaleValue(52)),
                    Margin = new Padding(ScaleValue(4))
                });
                return;
            }
            foreach (var device in pending)
            {
                var row = new Panel
                {
                    BackColor = Color.White,
                    Size = new Size(ScaleValue(276), ScaleValue(58)),
                    Margin = new Padding(ScaleValue(2))
                };
                row.Controls.Add(new Label
                {
                    Text = device.Name + "\r\n" + device.Id.Substring(0, 7),
                    AutoEllipsis = true,
                    ForeColor = Theme.Ink,
                    Font = Theme.Font(8f, FontStyle.Bold),
                    Location = new Point(ScaleValue(8), ScaleValue(7)),
                    Size = new Size(ScaleValue(184), ScaleValue(42))
                });
                var accept = ActionButton("接受", Theme.GreenSoft, Theme.Green);
                accept.Location = new Point(ScaleValue(204), ScaleValue(10));
                accept.Size = new Size(ScaleValue(62), ScaleValue(36));
                accept.Click += async delegate { await AcceptPendingAsync(device, accept); };
                row.Controls.Add(accept);
                _pendingList.Controls.Add(row);
            }
        }

        private async Task AcceptPendingAsync(PendingDeviceInfo device, Button button)
        {
            button.Enabled = false;
            SetFeedback("正在接受 " + device.Name + "…", Theme.Purple);
            try
            {
                await _service.AcceptPendingAsync(device);
                _context.RefreshTargetDevices();
                SetFeedback("已连接 " + device.Name + "，数据将自动同步。", Theme.Green);
                RefreshPendingList();
            }
            catch (Exception exception)
            {
                button.Enabled = true;
                SetFeedback(FriendlyError(exception), Theme.Red);
            }
        }

        private Label SectionLabel(string text, int top)
        {
            return new Label
            {
                AutoSize = true,
                Text = text,
                Font = Theme.Font(8f, FontStyle.Bold),
                ForeColor = Theme.Ink,
                Location = new Point(ScaleValue(16), ScaleValue(top))
            };
        }

        private Button ActionButton(string text, Color background, Color foreground)
        {
            var button = new Button
            {
                Text = text,
                BackColor = background,
                ForeColor = foreground,
                FlatStyle = FlatStyle.Flat,
                Font = Theme.Font(8f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                TabStop = true
            };
            button.FlatAppearance.BorderSize = 0;
            return button;
        }

        private void SetFeedback(string text, Color color)
        {
            _feedback.Text = text;
            _feedback.ForeColor = color;
        }

        private static string FriendlyError(Exception exception)
        {
            var message = exception == null ? String.Empty : exception.Message;
            if (String.IsNullOrWhiteSpace(message))
            {
                return "连接失败，请确认 Syncthing 正在运行。";
            }
            return message.Length <= 90 ? message : message.Substring(0, 87) + "…";
        }

        private int ScaleValue(int value)
        {
            using (var graphics = CreateGraphics())
            {
                return (int)Math.Round(value * graphics.DpiX / 96f);
            }
        }
    }
}

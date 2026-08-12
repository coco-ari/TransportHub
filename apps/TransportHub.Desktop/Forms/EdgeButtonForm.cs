using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using TransportHub.Desktop.Interop;
using TransportHub.Desktop.UI;
using NativeWindowHelper = TransportHub.Desktop.Interop.NativeWindow;

namespace TransportHub.Desktop.Forms
{
    internal sealed class PathsDroppedEventArgs : EventArgs
    {
        internal PathsDroppedEventArgs(string[] paths)
        {
            Paths = paths ?? new string[0];
        }

        internal string[] Paths { get; private set; }
    }

    internal sealed class EdgeButtonForm : Form
    {
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private Point _mouseDownScreen;
        private Point _windowDown;
        private bool _dragging;
        private bool _rightEdge = true;
        private Screen _screen;
        private int _unreadCount;
        private bool _online;
        private int _progress = -1;
        private bool _dropActive;

        internal EdgeButtonForm()
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(40, 40);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AllowDrop = true;
            BackColor = Color.White;
            DoubleBuffered = true;
            AccessibleName = "TransportHub 悬浮按钮";

            DragEnter += HandleDragEnter;
            DragOver += HandleDragEnter;
            DragLeave += (sender, args) => { _dropActive = false; Invalidate(); };
            DragDrop += HandleDragDrop;
            Resize += (sender, args) => NativeWindowHelper.ApplyRoundedRegion(this, ScaleValue(13));
            ClientSize = new Size(ScaleValue(40), ScaleValue(40));
        }

        internal event EventHandler ExpandRequested;
        internal event EventHandler<PathsDroppedEventArgs> PathsDropped;

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= WsExToolWindow | WsExNoActivate;
                return parameters;
            }
        }

        internal void ShowAt(Rectangle expandedBounds)
        {
            _screen = Screen.FromRectangle(expandedBounds);
            var work = _screen.WorkingArea;
            var centerX = expandedBounds.Left + expandedBounds.Width / 2;
            _rightEdge = Math.Abs(centerX - work.Right) <= Math.Abs(centerX - work.Left);
            var centerY = expandedBounds.Top + expandedBounds.Height / 2;
            var x = _rightEdge ? work.Right - Width - ScaleValue(8) : work.Left + ScaleValue(8);
            var y = Math.Max(work.Top + ScaleValue(6), Math.Min(work.Bottom - Height - ScaleValue(6), centerY - Height / 2));
            Location = new Point(x, y);
            if (!Visible)
            {
                Show();
            }
            Invalidate();
        }

        internal void RepositionToVisibleWorkArea()
        {
            var screen = _screen ?? Screen.FromControl(this);
            var work = screen.WorkingArea;
            var x = _rightEdge ? work.Right - Width - ScaleValue(8) : work.Left + ScaleValue(8);
            var y = Math.Max(work.Top + ScaleValue(6), Math.Min(work.Bottom - Height - ScaleValue(6), Top));
            Location = new Point(x, y);
        }

        internal Rectangle GetExpandedBounds(Size desiredSize)
        {
            var screen = _screen ?? Screen.FromControl(this);
            var work = screen.WorkingArea;
            var width = Math.Min(desiredSize.Width, Math.Max(320, work.Width - ScaleValue(16)));
            var height = Math.Min(desiredSize.Height, Math.Max(420, work.Height - ScaleValue(16)));
            var centerY = Top + Height / 2;
            var y = Math.Max(work.Top + ScaleValue(8), Math.Min(work.Bottom - height - ScaleValue(8), centerY - height / 2));
            var x = _rightEdge ? work.Right - width - ScaleValue(8) : work.Left + ScaleValue(8);
            return new Rectangle(x, y, width, height);
        }

        internal void SetState(bool online, int unreadCount, int progressPercent)
        {
            _online = online;
            _unreadCount = Math.Max(0, unreadCount);
            _progress = progressPercent < 0 ? -1 : Math.Min(100, progressPercent);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }
            _mouseDownScreen = Cursor.Position;
            _windowDown = Location;
            _dragging = false;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!Capture || e.Button != MouseButtons.Left)
            {
                return;
            }
            var cursor = Cursor.Position;
            if (!_dragging && Math.Abs(cursor.Y - _mouseDownScreen.Y) + Math.Abs(cursor.X - _mouseDownScreen.X) >= ScaleValue(4))
            {
                _dragging = true;
            }
            if (_dragging)
            {
                var screen = Screen.FromPoint(cursor);
                _screen = screen;
                var work = screen.WorkingArea;
                var x = _rightEdge ? work.Right - Width - ScaleValue(8) : work.Left + ScaleValue(8);
                var y = Math.Max(work.Top + ScaleValue(6), Math.Min(work.Bottom - Height - ScaleValue(6), _windowDown.Y + cursor.Y - _mouseDownScreen.Y));
                Location = new Point(x, y);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }
            Capture = false;
            if (!_dragging)
            {
                var handler = ExpandRequested;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
            _dragging = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(_dropActive ? Theme.PurpleSoft : Color.White);

            var inset = ScaleValue(4);
            var iconBounds = new Rectangle(inset, inset, Width - inset * 2, Height - inset * 2);
            using (var purple = new LinearGradientBrush(iconBounds, Theme.Purple, Theme.Purple2, 45f))
            {
                graphics.FillRoundedRectangle(purple, iconBounds, ScaleValue(10));
            }
            using (var font = Theme.Font(10.5f, FontStyle.Bold))
            using (var white = new SolidBrush(Color.White))
            {
                var text = "T";
                var size = graphics.MeasureString(text, font);
                graphics.DrawString(text, font, white, iconBounds.Left + (iconBounds.Width - size.Width) / 2f, iconBounds.Top + (iconBounds.Height - size.Height) / 2f - 1f);
            }

            if (_progress >= 0 && _progress < 100)
            {
                using (var pen = new Pen(Color.FromArgb(118, 235, 211), ScaleValue(2)))
                {
                    graphics.DrawArc(pen, Rectangle.Inflate(iconBounds, ScaleValue(2), ScaleValue(2)), -90f, _progress * 3.6f);
                }
            }

            var stateColor = _online ? Theme.Green : Theme.Amber;
            using (var brush = new SolidBrush(stateColor))
            using (var border = new Pen(Color.White, ScaleValue(1)))
            {
                var stateBounds = new Rectangle(Width - ScaleValue(10), Height - ScaleValue(10), ScaleValue(7), ScaleValue(7));
                graphics.FillEllipse(brush, stateBounds);
                graphics.DrawEllipse(border, stateBounds);
            }

            if (_unreadCount > 0)
            {
                var label = _unreadCount > 99 ? "99+" : _unreadCount.ToString();
                var badgeWidth = _unreadCount > 99 ? ScaleValue(19) : ScaleValue(14);
                var badge = new Rectangle(Width - badgeWidth - ScaleValue(1), ScaleValue(1), badgeWidth, ScaleValue(14));
                using (var red = new SolidBrush(Color.FromArgb(236, 83, 104)))
                using (var border = new Pen(Color.White, ScaleValue(1)))
                using (var font = Theme.Font(6f, FontStyle.Bold))
                using (var white = new SolidBrush(Color.White))
                {
                    graphics.FillEllipse(red, badge);
                    graphics.DrawEllipse(border, badge);
                    var size = graphics.MeasureString(label, font);
                    graphics.DrawString(label, font, white, badge.Left + (badge.Width - size.Width) / 2f, badge.Top + (badge.Height - size.Height) / 2f - 1f);
                }
            }
        }

        private void HandleDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
                _dropActive = true;
                Invalidate();
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void HandleDragDrop(object sender, DragEventArgs e)
        {
            _dropActive = false;
            Invalidate();
            var paths = e.Data == null ? null : e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0)
            {
                return;
            }
            var handler = PathsDropped;
            if (handler != null)
            {
                handler(this, new PathsDroppedEventArgs(paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray()));
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

    internal static class GraphicsExtensions
    {
        internal static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle rectangle, int radius)
        {
            using (var path = new GraphicsPath())
            {
                var diameter = Math.Max(1, radius * 2);
                path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
                path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
                path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                graphics.FillPath(brush, path);
            }
        }

        internal static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle rectangle, int radius)
        {
            using (var path = new GraphicsPath())
            {
                var diameter = Math.Max(1, radius * 2);
                path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
                path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
                path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                graphics.DrawPath(pen, path);
            }
        }
    }
}

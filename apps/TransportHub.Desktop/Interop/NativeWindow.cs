using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TransportHub.Desktop.Interop
{
    internal static class NativeWindow
    {
        private const int WmNclbuttondown = 0x00A1;
        private const int HtCaption = 0x0002;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        internal static void BeginWindowDrag(Form form)
        {
            if (form == null || form.IsDisposed)
            {
                return;
            }

            ReleaseCapture();
            SendMessage(form.Handle, WmNclbuttondown, new IntPtr(HtCaption), IntPtr.Zero);
        }

        internal static void ApplyRoundedRegion(Control control, int radius)
        {
            if (control == null || control.Width <= 0 || control.Height <= 0)
            {
                return;
            }

            var regionHandle = CreateRoundRectRgn(0, 0, control.Width + 1, control.Height + 1, radius, radius);
            if (regionHandle == IntPtr.Zero)
            {
                return;
            }
            try
            {
                using (var region = Region.FromHrgn(regionHandle))
                {
                    var oldRegion = control.Region;
                    control.Region = region.Clone();
                    if (oldRegion != null)
                    {
                        oldRegion.Dispose();
                    }
                }
            }
            finally
            {
                DeleteObject(regionHandle);
            }
        }

        internal static Icon CreateApplicationIcon(int size)
        {
            using (var bitmap = new Bitmap(size, size))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(UI.Theme.Purple))
            using (var font = new Font("Segoe UI", size * 0.46f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var textBrush = new SolidBrush(Color.White))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                graphics.FillEllipse(brush, 1, 1, size - 2, size - 2);
                var text = "T";
                var bounds = graphics.MeasureString(text, font);
                graphics.DrawString(text, font, textBrush, (size - bounds.Width) / 2f, (size - bounds.Height) / 2f - 1f);
                var handle = bitmap.GetHicon();
                try
                {
                    return (Icon)Icon.FromHandle(handle).Clone();
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }
    }
}

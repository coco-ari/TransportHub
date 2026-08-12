using System.Drawing;

namespace TransportHub.Desktop.UI
{
    internal static class Theme
    {
        internal static readonly Color Ink = Color.FromArgb(31, 38, 55);
        internal static readonly Color Muted = Color.FromArgb(121, 129, 147);
        internal static readonly Color Line = Color.FromArgb(230, 233, 240);
        internal static readonly Color Panel = Color.White;
        internal static readonly Color Surface = Color.FromArgb(250, 249, 255);
        internal static readonly Color Purple = Color.FromArgb(110, 86, 232);
        internal static readonly Color Purple2 = Color.FromArgb(138, 116, 245);
        internal static readonly Color PurpleSoft = Color.FromArgb(240, 237, 255);
        internal static readonly Color Green = Color.FromArgb(37, 169, 119);
        internal static readonly Color GreenSoft = Color.FromArgb(234, 248, 242);
        internal static readonly Color Amber = Color.FromArgb(199, 131, 24);
        internal static readonly Color AmberSoft = Color.FromArgb(255, 245, 223);
        internal static readonly Color Red = Color.FromArgb(216, 87, 87);

        internal static Font Font(float size, FontStyle style = FontStyle.Regular)
        {
            return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
        }
    }
}

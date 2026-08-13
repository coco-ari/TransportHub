using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TransportHub.Desktop.Core;

namespace TransportHub.Desktop.UI
{
    internal sealed class TimelineItemValueEventArgs : EventArgs
    {
        internal TimelineItemValueEventArgs(string value)
        {
            Value = value;
        }

        internal string Value { get; private set; }
    }

    /// <summary>
    /// UI-only adapter for one timeline row. Persistence models should be mapped
    /// to this type by the form instead of coupling this control to storage code.
    /// Call Bind again after changing any property.
    /// </summary>
    internal sealed class TimelineItemViewModel
    {
        internal string MessageId { get; set; }
        internal TimelineMessageKind Kind { get; set; }
        internal bool IsOutgoing { get; set; }
        internal string SenderName { get; set; }
        internal string Text { get; set; }
        internal string Url { get; set; }
        internal string RelativePath { get; set; }
        internal string AbsolutePath { get; set; }
        internal string MimeType { get; set; }
        internal long SizeBytes { get; set; }
        internal string AttachmentProgress { get; set; }
        internal DateTime Timestamp { get; set; }
        internal string DeliverySummary { get; set; }
    }

    /// <summary>
    /// A compact, auto-height timeline card. It performs presentation-only work:
    /// text clicks copy text, while attachment clicks raise RevealRequested.
    /// </summary>
    internal sealed class TimelineItemControl : UserControl
    {
        private enum PresentationKind
        {
            Text,
            Link,
            Attachment,
            Image
        }

        private const int MaximumBodyLogicalHeight = 320;
        private static readonly SemaphoreSlim ThumbnailConcurrency = new SemaphoreSlim(2, 2);

        private readonly Font bodyFont;
        private readonly Font linkFont;
        private readonly Font senderFont;
        private readonly Font metaFont;
        private readonly Font attachmentNameFont;
        private readonly ToolTip toolTip;
        private readonly System.Windows.Forms.Timer feedbackTimer;

        private TimelineItemViewModel item;
        private Image thumbnail;
        private Rectangle bubbleBounds;
        private Rectangle senderBounds;
        private Rectangle contentBounds;
        private Rectangle metaBounds;
        private Rectangle copyHitBounds;
        private Rectangle revealHitBounds;
        private Rectangle thumbnailBounds;
        private Rectangle attachmentIconBounds;
        private Rectangle attachmentNameBounds;
        private Rectangle attachmentDetailBounds;
        private bool bodyIsTruncated;
        private bool pointerIsOverAction;
        private bool updatingLayout;
        private string inlineFeedback;
        private int calculatedForWidth = -1;
        private int thumbnailGeneration;
        private TimelineItemViewModel pendingThumbnail;

        internal TimelineItemControl(TimelineItemViewModel model)
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint |
                ControlStyles.Selectable,
                true);

            BackColor = Theme.Surface;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimumSize = new Size(0, 36);
            Margin = new Padding(0);
            Padding = new Padding(0);
            TabStop = true;
            AccessibleRole = AccessibleRole.ListItem;

            bodyFont = Theme.Font(9.2f);
            linkFont = Theme.Font(9.2f, FontStyle.Underline);
            senderFont = Theme.Font(7.8f, FontStyle.Bold);
            metaFont = Theme.Font(7.4f);
            attachmentNameFont = Theme.Font(8.8f, FontStyle.Bold);
            toolTip = new ToolTip
            {
                AutomaticDelay = 250,
                AutoPopDelay = 4000,
                InitialDelay = 250,
                ReshowDelay = 100,
                ShowAlways = true
            };
            feedbackTimer = new System.Windows.Forms.Timer { Interval = 900 };
            feedbackTimer.Tick += delegate
            {
                feedbackTimer.Stop();
                inlineFeedback = null;
                Invalidate(metaBounds);
            };

            Bind(model);
        }

        /// <summary>
        /// Raised for attachment and image cards. The control deliberately does
        /// not resolve, open, download, or otherwise act on the relative path.
        /// </summary>
        internal event EventHandler<TimelineItemValueEventArgs> RevealRequested;

        internal event EventHandler<TimelineItemValueEventArgs> LinkRequested;

        internal TimelineItemViewModel Item
        {
            get { return item; }
        }

        internal void Bind(TimelineItemViewModel value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            item = value;
            feedbackTimer.Stop();
            inlineFeedback = null;
            Interlocked.Increment(ref thumbnailGeneration);
            ReplaceThumbnail(null);
            pendingThumbnail = IsImageAttachment(value) && !string.IsNullOrWhiteSpace(value.AbsolutePath)
                ? value
                : null;
            AccessibleName = BuildAccessibleName(value);
            AccessibleDescription = BuildAccessibleDescription(value);
            calculatedForWidth = -1;
            RecalculateHeight();
            Invalidate();
            if (IsHandleCreated)
            {
                QueuePendingThumbnail();
            }
        }

        internal void SetAvailableWidth(int width)
        {
            if (width <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            Width = width;
            calculatedForWidth = -1;
            RecalculateHeight();
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var width = proposedSize.Width;
            if (width <= 0)
            {
                width = Width > 0 ? Width : ScaleLogical(440);
            }

            var height = CalculateLayout(width);
            return new Size(width, height);
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);
            if (item == null)
            {
                return;
            }

            if (calculatedForWidth != ClientSize.Width)
            {
                CalculateLayout(ClientSize.Width);
            }

            var graphics = eventArgs.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            DrawBubble(graphics);
            DrawSender(graphics);
            DrawContent(graphics);
            DrawMetadata(graphics);

            if (Focused && ShowFocusCues)
            {
                var focusBounds = bubbleBounds;
                focusBounds.Inflate(ScaleLogical(2), ScaleLogical(2));
                ControlPaint.DrawFocusRectangle(graphics, focusBounds, Theme.Ink, BackColor);
            }
        }

        protected override void OnSizeChanged(EventArgs eventArgs)
        {
            base.OnSizeChanged(eventArgs);
            calculatedForWidth = -1;
            RecalculateHeight();
        }

        protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
        {
            base.OnDpiChangedAfterParent(eventArgs);
            calculatedForWidth = -1;
            RecalculateHeight();
        }

        protected override void OnMouseMove(MouseEventArgs eventArgs)
        {
            base.OnMouseMove(eventArgs);
            var isOverAction = copyHitBounds.Contains(eventArgs.Location) ||
                               (revealHitBounds.Contains(eventArgs.Location) && HasRevealTarget());
            if (isOverAction != pointerIsOverAction)
            {
                pointerIsOverAction = isOverAction;
                Cursor = isOverAction ? Cursors.Hand : Cursors.Default;
                Invalidate(contentBounds);
            }
        }

        protected override void OnMouseLeave(EventArgs eventArgs)
        {
            base.OnMouseLeave(eventArgs);
            pointerIsOverAction = false;
            Cursor = Cursors.Default;
            Invalidate(contentBounds);
        }

        protected override void OnMouseDown(MouseEventArgs eventArgs)
        {
            base.OnMouseDown(eventArgs);
        }

        protected override void OnMouseClick(MouseEventArgs eventArgs)
        {
            base.OnMouseClick(eventArgs);
            if (eventArgs.Button != MouseButtons.Left || item == null)
            {
                return;
            }

            if (copyHitBounds.Contains(eventArgs.Location))
            {
                if (GetPresentationKind() == PresentationKind.Link)
                {
                    RaiseLinkRequested();
                }
                else
                {
                    CopyDisplayedContent(eventArgs.Location);
                }
                return;
            }

            if (revealHitBounds.Contains(eventArgs.Location))
            {
                RaiseRevealRequested();
            }
        }

        protected override void OnKeyDown(KeyEventArgs eventArgs)
        {
            base.OnKeyDown(eventArgs);
            if (item == null)
            {
                return;
            }

            if (eventArgs.Control && eventArgs.KeyCode == Keys.C && IsCopyable())
            {
                CopyDisplayedContent(new Point(bubbleBounds.Left, bubbleBounds.Bottom));
                eventArgs.Handled = true;
                eventArgs.SuppressKeyPress = true;
                return;
            }

            if (eventArgs.KeyCode == Keys.Enter || eventArgs.KeyCode == Keys.Space)
            {
                if (HasRevealTarget())
                {
                    RaiseRevealRequested();
                }
                else if (GetPresentationKind() == PresentationKind.Link)
                {
                    RaiseLinkRequested();
                }
                else if (IsCopyable())
                {
                    CopyDisplayedContent(new Point(bubbleBounds.Left, bubbleBounds.Bottom));
                }

                eventArgs.Handled = true;
                eventArgs.SuppressKeyPress = true;
            }
        }

        protected override void OnGotFocus(EventArgs eventArgs)
        {
            base.OnGotFocus(eventArgs);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs eventArgs)
        {
            base.OnLostFocus(eventArgs);
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            QueuePendingThumbnail();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref thumbnailGeneration);
                pendingThumbnail = null;
                ReplaceThumbnail(null);
                toolTip.Dispose();
                feedbackTimer.Dispose();
                bodyFont.Dispose();
                linkFont.Dispose();
                senderFont.Dispose();
                metaFont.Dispose();
                attachmentNameFont.Dispose();
            }

            base.Dispose(disposing);
        }

        private void QueuePendingThumbnail()
        {
            var value = pendingThumbnail;
            if (value == null || IsDisposed || Disposing || !IsHandleCreated)
            {
                return;
            }
            pendingThumbnail = null;
            var generation = thumbnailGeneration;
            Task.Run(async delegate
            {
                await ThumbnailConcurrency.WaitAsync().ConfigureAwait(false);
                try
                {
                    return TryCreateThumbnail(value);
                }
                finally
                {
                    ThumbnailConcurrency.Release();
                }
            }).ContinueWith(task =>
            {
                var image = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                if (image == null)
                {
                    return;
                }
                if (IsDisposed || Disposing || !IsHandleCreated || generation != thumbnailGeneration)
                {
                    image.Dispose();
                    return;
                }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (IsDisposed || Disposing || generation != thumbnailGeneration)
                        {
                            image.Dispose();
                            return;
                        }
                        ReplaceThumbnail(image);
                        calculatedForWidth = -1;
                        RecalculateHeight();
                        Invalidate();
                    });
                }
                catch (InvalidOperationException)
                {
                    image.Dispose();
                }
            }, TaskScheduler.Default);
        }

        private int CalculateLayout(int controlWidth)
        {
            controlWidth = Math.Max(ScaleLogical(120), controlWidth);
            calculatedForWidth = controlWidth;
            ResetLayoutRectangles();

            if (item == null)
            {
                return ScaleLogical(48);
            }

            var outerPadding = ScaleLogical(4);
            var bubblePadding = ScaleLogical(8);
            var senderGap = ScaleLogical(3);
            var metaGap = ScaleLogical(2);
            var maximumBubbleWidth = Math.Min(
                ScaleLogical(340),
                Math.Max(ScaleLogical(100), controlWidth - outerPadding * 2));
            var minimumBubbleWidth = Math.Min(ScaleLogical(116), maximumBubbleWidth);
            var maximumContentWidth = Math.Max(ScaleLogical(80), maximumBubbleWidth - bubblePadding * 2);

            var senderText = GetSenderText();
            var metadataText = GetMetadataText();
            var desiredContentWidth = MeasureDesiredContentWidth(maximumContentWidth);
            var senderWidth = MeasureSingleLine(senderText, senderFont).Width;
            var metadataWidth = MeasureSingleLine(metadataText, metaFont).Width;
            var desiredBubbleWidth = Math.Max(
                desiredContentWidth + bubblePadding * 2,
                Math.Max(senderWidth, metadataWidth) + bubblePadding * 2);
            var bubbleWidth = Clamp(desiredBubbleWidth, minimumBubbleWidth, maximumBubbleWidth);
            var contentWidth = Math.Max(ScaleLogical(60), bubbleWidth - bubblePadding * 2);

            var hasSender = !string.IsNullOrWhiteSpace(senderText);
            var senderHeight = hasSender ? Math.Max(senderFont.Height, ScaleLogical(14)) : 0;
            var contentHeight = MeasureAndPlaceContent(contentWidth);
            var bubbleHeight = bubblePadding + contentHeight + bubblePadding;
            if (hasSender)
            {
                bubbleHeight += senderHeight + senderGap;
            }
            var bubbleX = item.IsOutgoing
                ? controlWidth - outerPadding - bubbleWidth
                : outerPadding;
            bubbleBounds = new Rectangle(bubbleX, outerPadding, bubbleWidth, bubbleHeight);
            senderBounds = hasSender
                ? new Rectangle(
                    bubbleBounds.Left + bubblePadding,
                    bubbleBounds.Top + bubblePadding,
                    contentWidth,
                    senderHeight)
                : Rectangle.Empty;

            var contentTop = hasSender
                ? senderBounds.Bottom + senderGap
                : bubbleBounds.Top + bubblePadding;
            OffsetContentRectangles(
                bubbleBounds.Left + bubblePadding,
                contentTop,
                contentWidth,
                contentHeight);

            var metaHeight = Math.Max(metaFont.Height, ScaleLogical(13));
            metaBounds = new Rectangle(
                bubbleBounds.Left,
                bubbleBounds.Bottom + metaGap,
                bubbleBounds.Width,
                metaHeight);

            return metaBounds.Bottom + outerPadding;
        }

        private int MeasureDesiredContentWidth(int maximumContentWidth)
        {
            switch (GetPresentationKind())
            {
                case PresentationKind.Text:
                case PresentationKind.Link:
                    var displayText = GetDisplayText();
                    var font = GetPresentationKind() == PresentationKind.Link ? linkFont : bodyFont;
                    var measured = TextRenderer.MeasureText(
                        displayText,
                        font,
                        new Size(maximumContentWidth, int.MaxValue),
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
                    return Clamp(measured.Width, ScaleLogical(64), maximumContentWidth);

                case PresentationKind.Attachment:
                    return Math.Min(maximumContentWidth, ScaleLogical(250));

                case PresentationKind.Image:
                    return Math.Min(maximumContentWidth, ScaleLogical(260));

                default:
                    return Math.Min(maximumContentWidth, ScaleLogical(180));
            }
        }

        private int MeasureAndPlaceContent(int contentWidth)
        {
            bodyIsTruncated = false;
            switch (GetPresentationKind())
            {
                case PresentationKind.Text:
                case PresentationKind.Link:
                    var font = GetPresentationKind() == PresentationKind.Link ? linkFont : bodyFont;
                    var measured = TextRenderer.MeasureText(
                        GetDisplayText(),
                        font,
                        new Size(contentWidth, int.MaxValue),
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
                    var maximumHeight = ScaleLogical(MaximumBodyLogicalHeight);
                    bodyIsTruncated = measured.Height > maximumHeight;
                    contentBounds = new Rectangle(0, 0, contentWidth, Math.Min(measured.Height, maximumHeight));
                    copyHitBounds = contentBounds;
                    return Math.Max(font.Height, contentBounds.Height);

                case PresentationKind.Attachment:
                    var attachmentHeight = ScaleLogical(48);
                    contentBounds = new Rectangle(0, 0, contentWidth, attachmentHeight);
                    revealHitBounds = contentBounds;
                    attachmentIconBounds = new Rectangle(
                        ScaleLogical(5),
                        ScaleLogical(6),
                        ScaleLogical(32),
                        ScaleLogical(36));
                    attachmentNameBounds = new Rectangle(
                        attachmentIconBounds.Right + ScaleLogical(9),
                        ScaleLogical(5),
                        Math.Max(ScaleLogical(20), contentWidth - attachmentIconBounds.Right - ScaleLogical(13)),
                        ScaleLogical(21));
                    attachmentDetailBounds = new Rectangle(
                        attachmentNameBounds.Left,
                        attachmentNameBounds.Bottom,
                        attachmentNameBounds.Width,
                        ScaleLogical(17));
                    return attachmentHeight;

                case PresentationKind.Image:
                    var availableImageWidth = Math.Max(ScaleLogical(80), contentWidth);
                    var imageHeight = CalculateThumbnailHeight(availableImageWidth);
                    thumbnailBounds = new Rectangle(0, 0, availableImageWidth, imageHeight);
                    var imageName = GetAttachmentName();
                    if (imageName.StartsWith("粘贴图片-", StringComparison.OrdinalIgnoreCase))
                    {
                        imageName = string.Empty;
                    }
                    var nameHeight = string.IsNullOrWhiteSpace(imageName) ? 0 : ScaleLogical(22);
                    attachmentNameBounds = new Rectangle(
                        0,
                        thumbnailBounds.Bottom + (nameHeight == 0 ? 0 : ScaleLogical(3)),
                        availableImageWidth,
                        nameHeight);
                    contentBounds = new Rectangle(
                        0,
                        0,
                        availableImageWidth,
                        thumbnailBounds.Height + (nameHeight == 0 ? 0 : ScaleLogical(3) + nameHeight));
                    revealHitBounds = contentBounds;
                    return contentBounds.Height;

                default:
                    contentBounds = new Rectangle(0, 0, contentWidth, bodyFont.Height);
                    return contentBounds.Height;
            }
        }

        private void OffsetContentRectangles(int x, int y, int width, int height)
        {
            contentBounds = new Rectangle(x, y, width, height);
            OffsetIfNotEmpty(ref copyHitBounds, x, y);
            OffsetIfNotEmpty(ref revealHitBounds, x, y);
            OffsetIfNotEmpty(ref thumbnailBounds, x, y);
            OffsetIfNotEmpty(ref attachmentIconBounds, x, y);
            OffsetIfNotEmpty(ref attachmentNameBounds, x, y);
            OffsetIfNotEmpty(ref attachmentDetailBounds, x, y);
        }

        private void DrawBubble(Graphics graphics)
        {
            var background = item.IsOutgoing ? Theme.PurpleSoft : Theme.Panel;
            var border = item.IsOutgoing ? Color.FromArgb(219, 213, 250) : Theme.Line;
            using (var path = CreateRoundedRectangle(bubbleBounds, ScaleLogical(11)))
            using (var brush = new SolidBrush(background))
            using (var pen = new Pen(border))
            {
                graphics.FillPath(brush, path);
                graphics.DrawPath(pen, path);
            }
        }

        private void DrawSender(Graphics graphics)
        {
            if (senderBounds.IsEmpty)
            {
                return;
            }
            var alignment = item.IsOutgoing
                ? TextFormatFlags.Right
                : TextFormatFlags.Left;
            TextRenderer.DrawText(
                graphics,
                GetSenderText(),
                senderFont,
                senderBounds,
                item.IsOutgoing ? Theme.Purple : Theme.Muted,
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix |
                TextFormatFlags.SingleLine |
                TextFormatFlags.VerticalCenter |
                alignment);
        }

        private void DrawContent(Graphics graphics)
        {
            switch (GetPresentationKind())
            {
                case PresentationKind.Text:
                case PresentationKind.Link:
                    DrawTextContent(graphics);
                    break;

                case PresentationKind.Attachment:
                    DrawAttachmentContent(graphics);
                    break;

                case PresentationKind.Image:
                    DrawImageContent(graphics);
                    break;
            }
        }

        private void DrawTextContent(Graphics graphics)
        {
            var isLink = GetPresentationKind() == PresentationKind.Link;
            var color = isLink ? Theme.Purple : Theme.Ink;
            var font = isLink ? linkFont : bodyFont;
            var flags = TextFormatFlags.NoPadding |
                        TextFormatFlags.NoPrefix |
                        TextFormatFlags.WordBreak |
                        TextFormatFlags.TextBoxControl;
            if (bodyIsTruncated)
            {
                flags |= TextFormatFlags.EndEllipsis;
            }

            TextRenderer.DrawText(graphics, GetDisplayText(), font, contentBounds, color, flags);

        }

        private void DrawAttachmentContent(Graphics graphics)
        {
            using (var background = new SolidBrush(
                pointerIsOverAction && revealHitBounds.Contains(PointToClient(Cursor.Position))
                    ? Color.FromArgb(235, 231, 255)
                    : Color.FromArgb(247, 245, 255)))
            using (var border = new Pen(Color.FromArgb(222, 216, 249)))
            {
                graphics.FillRectangle(background, contentBounds);
                graphics.DrawRectangle(border, contentBounds);
            }

            DrawFileGlyph(graphics, attachmentIconBounds);
            TextRenderer.DrawText(
                graphics,
                GetAttachmentName(),
                attachmentNameFont,
                attachmentNameBounds,
                Theme.Ink,
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix |
                TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(
                graphics,
                GetAttachmentDetail(),
                metaFont,
                attachmentDetailBounds,
                Theme.Muted,
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix |
                TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.VerticalCenter);
        }

        private void DrawImageContent(Graphics graphics)
        {
            using (var background = new SolidBrush(Color.FromArgb(245, 245, 248)))
            using (var border = new Pen(
                pointerIsOverAction && revealHitBounds.Contains(PointToClient(Cursor.Position))
                    ? Theme.Purple
                    : Theme.Line))
            {
                graphics.FillRectangle(background, thumbnailBounds);
                graphics.DrawRectangle(border, thumbnailBounds);
            }

            if (thumbnail != null)
            {
                var destination = FitInside(thumbnail.Size, Deflate(thumbnailBounds, ScaleLogical(1)));
                var previousInterpolation = graphics.InterpolationMode;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(thumbnail, destination);
                graphics.InterpolationMode = previousInterpolation;
            }
            else
            {
                DrawImagePlaceholder(graphics, thumbnailBounds);
            }

            if (!attachmentNameBounds.IsEmpty)
            {
                TextRenderer.DrawText(
                    graphics,
                    GetAttachmentName(),
                    metaFont,
                    attachmentNameBounds,
                    Theme.Muted,
                    TextFormatFlags.NoPadding |
                    TextFormatFlags.NoPrefix |
                    TextFormatFlags.SingleLine |
                    TextFormatFlags.EndEllipsis |
                    TextFormatFlags.VerticalCenter);
            }
        }

        private void DrawMetadata(Graphics graphics)
        {
            var alignment = item.IsOutgoing ? TextFormatFlags.Right : TextFormatFlags.Left;
            TextRenderer.DrawText(
                graphics,
                GetMetadataText(),
                metaFont,
                metaBounds,
                GetMetadataColor(),
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix |
                TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.VerticalCenter |
                alignment);
        }

        private void DrawFileGlyph(Graphics graphics, Rectangle bounds)
        {
            var page = new Rectangle(
                bounds.Left + ScaleLogical(4),
                bounds.Top + ScaleLogical(2),
                bounds.Width - ScaleLogical(8),
                bounds.Height - ScaleLogical(4));
            var fold = ScaleLogical(7);
            var points = new[]
            {
                new Point(page.Left, page.Top),
                new Point(page.Right - fold, page.Top),
                new Point(page.Right, page.Top + fold),
                new Point(page.Right, page.Bottom),
                new Point(page.Left, page.Bottom)
            };

            using (var fill = new SolidBrush(Theme.PurpleSoft))
            using (var pen = new Pen(Theme.Purple, Math.Max(1f, DeviceDpi / 96f)))
            {
                graphics.FillPolygon(fill, points);
                graphics.DrawPolygon(pen, points);
                graphics.DrawLine(
                    pen,
                    page.Right - fold,
                    page.Top,
                    page.Right - fold,
                    page.Top + fold);
                graphics.DrawLine(
                    pen,
                    page.Right - fold,
                    page.Top + fold,
                    page.Right,
                    page.Top + fold);
            }
        }

        private void DrawImagePlaceholder(Graphics graphics, Rectangle bounds)
        {
            var iconSize = Math.Min(ScaleLogical(42), Math.Min(bounds.Width, bounds.Height) / 2);
            var iconBounds = new Rectangle(
                bounds.Left + (bounds.Width - iconSize) / 2,
                bounds.Top + (bounds.Height - iconSize) / 2,
                iconSize,
                iconSize);
            using (var pen = new Pen(Theme.Muted, Math.Max(1f, DeviceDpi / 96f)))
            {
                graphics.DrawRectangle(pen, iconBounds);
                graphics.DrawEllipse(
                    pen,
                    iconBounds.Left + iconBounds.Width * 2 / 3,
                    iconBounds.Top + ScaleLogical(5),
                    ScaleLogical(6),
                    ScaleLogical(6));
                graphics.DrawLine(
                    pen,
                    iconBounds.Left + ScaleLogical(4),
                    iconBounds.Bottom - ScaleLogical(5),
                    iconBounds.Left + iconBounds.Width / 2,
                    iconBounds.Top + iconBounds.Height / 2);
                graphics.DrawLine(
                    pen,
                    iconBounds.Left + iconBounds.Width / 2,
                    iconBounds.Top + iconBounds.Height / 2,
                    iconBounds.Right - ScaleLogical(4),
                    iconBounds.Bottom - ScaleLogical(5));
            }
        }

        private void CopyDisplayedContent(Point feedbackLocation)
        {
            var text = GetCopyText();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            try
            {
                Clipboard.SetText(text, TextDataFormat.UnicodeText);
                ShowInlineFeedback(bodyIsTruncated ? "已复制全文" : "已复制");
            }
            catch (ExternalException)
            {
                ShowTransientTip("剪贴板正忙，请重试", feedbackLocation);
            }
            catch (ThreadStateException)
            {
                ShowTransientTip("无法访问剪贴板", feedbackLocation);
            }
        }

        private void RaiseRevealRequested()
        {
            if (!HasRevealTarget())
            {
                if (item != null && item.Kind == TimelineMessageKind.Attachment)
                {
                    ShowTransientTip("文件仍在同步，请稍后重试", PointToClient(Cursor.Position));
                }
                return;
            }

            var handler = RevealRequested;
            if (handler != null)
            {
                handler(this, new TimelineItemValueEventArgs(item.AbsolutePath));
            }
        }

        private void RaiseLinkRequested()
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Url))
            {
                return;
            }

            var handler = LinkRequested;
            if (handler != null)
            {
                handler(this, new TimelineItemValueEventArgs(item.Url));
            }
        }

        private void ShowTransientTip(string message, Point location)
        {
            var x = Clamp(location.X, 0, Math.Max(0, Width - 1));
            var y = Clamp(location.Y, 0, Math.Max(0, Height - 1));
            toolTip.Show(message, this, x, y, 1100);
        }

        private void ShowInlineFeedback(string message)
        {
            inlineFeedback = message;
            feedbackTimer.Stop();
            feedbackTimer.Start();
            Invalidate(metaBounds);
        }

        private bool IsCopyable()
        {
            return item != null &&
                   (item.Kind == TimelineMessageKind.Text || item.Kind == TimelineMessageKind.Link) &&
                   !string.IsNullOrEmpty(GetCopyText());
        }

        private bool HasRevealTarget()
        {
            return item != null &&
                   item.Kind == TimelineMessageKind.Attachment &&
                   !string.IsNullOrWhiteSpace(item.AbsolutePath);
        }

        private string GetCopyText()
        {
            if (item == null)
            {
                return string.Empty;
            }

            if (item.Kind == TimelineMessageKind.Link && !string.IsNullOrWhiteSpace(item.Url))
            {
                return item.Url;
            }

            return item.Text ?? string.Empty;
        }

        private string GetDisplayText()
        {
            var text = GetCopyText();
            return string.IsNullOrEmpty(text)
                ? (item != null && item.Kind == TimelineMessageKind.Link ? "（空链接）" : "（空消息）")
                : text;
        }

        private string GetSenderText()
        {
            if (item == null)
            {
                return string.Empty;
            }

            if (item.IsOutgoing)
            {
                return string.Empty;
            }

            return string.IsNullOrWhiteSpace(item.SenderName) ? "其他设备" : item.SenderName.Trim();
        }

        private string GetMetadataText()
        {
            if (item == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(inlineFeedback))
            {
                return inlineFeedback;
            }

            var timestamp = FormatTimestamp(item.Timestamp);
            var delivery = string.IsNullOrWhiteSpace(item.DeliverySummary)
                ? string.Empty
                : item.DeliverySummary.Trim();
            if (timestamp.Length == 0)
            {
                return delivery;
            }

            return delivery.Length == 0 ? timestamp : timestamp + "  ·  " + delivery;
        }

        private string GetAttachmentName()
        {
            if (item == null)
            {
                return "附件";
            }

            var path = !string.IsNullOrWhiteSpace(item.AbsolutePath)
                ? item.AbsolutePath
                : item.RelativePath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return name;
                    }
                }
                catch (ArgumentException)
                {
                    // RelativePath is display data here; malformed paths are not opened by this control.
                }
            }

            return GetPresentationKind() == PresentationKind.Image ? "图片" : "附件";
        }

        private string GetAttachmentDetail()
        {
            if (item == null)
            {
                return string.Empty;
            }

            var size = item.SizeBytes >= 0
                ? FormatBytes(item.SizeBytes)
                : string.Empty;
            var progress = string.IsNullOrWhiteSpace(item.AttachmentProgress)
                ? string.Empty
                : item.AttachmentProgress.Trim();
            if (progress.Length > 0)
            {
                return size.Length == 0 ? progress : size + "  ·  " + progress;
            }
            if (HasRevealTarget())
            {
                return size;
            }

            return size.Length == 0 ? "同步中" : size + "  ·  同步中";
        }

        private Color GetMetadataColor()
        {
            if (!string.IsNullOrWhiteSpace(inlineFeedback))
            {
                return Theme.Green;
            }

            var summary = item == null ? null : item.DeliverySummary;
            if (string.IsNullOrWhiteSpace(summary))
            {
                return Theme.Muted;
            }

            if (summary.IndexOf("失败", StringComparison.OrdinalIgnoreCase) >= 0 ||
                summary.IndexOf("错误", StringComparison.OrdinalIgnoreCase) >= 0 ||
                summary.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Theme.Red;
            }

            if (summary.IndexOf("✓", StringComparison.OrdinalIgnoreCase) >= 0 ||
                summary.IndexOf("完成", StringComparison.OrdinalIgnoreCase) >= 0 ||
                summary.IndexOf("送达", StringComparison.OrdinalIgnoreCase) >= 0 ||
                summary.IndexOf("delivered", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Theme.Green;
            }

            if (summary.IndexOf("等待", StringComparison.OrdinalIgnoreCase) >= 0 ||
                summary.IndexOf("同步", StringComparison.OrdinalIgnoreCase) >= 0 ||
                summary.IndexOf("pending", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Theme.Amber;
            }

            return Theme.Muted;
        }

        private int CalculateThumbnailHeight(int width)
        {
            if (thumbnail == null || thumbnail.Width <= 0 || thumbnail.Height <= 0)
            {
                return ScaleLogical(124);
            }

            var proportional = (int)Math.Round(width * (double)thumbnail.Height / thumbnail.Width);
            return Clamp(proportional, ScaleLogical(88), ScaleLogical(178));
        }

        private void ReplaceThumbnail(Image source)
        {
            if (thumbnail != null)
            {
                thumbnail.Dispose();
                thumbnail = null;
            }

            thumbnail = source;
        }

        private static Image TryCreateThumbnail(TimelineItemViewModel value)
        {
            if (value == null || !IsImageAttachment(value) || string.IsNullOrWhiteSpace(value.AbsolutePath))
            {
                return null;
            }

            try
            {
                var path = value.AbsolutePath;
                if (!Path.IsPathRooted(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(path))
                {
                    return null;
                }

                var info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > 100L * 1024L * 1024L)
                {
                    return null;
                }

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var source = Image.FromStream(stream, false, true))
                {
                    if (source.Width <= 0 || source.Height <= 0 || (long)source.Width * source.Height > 80L * 1000L * 1000L)
                    {
                        return null;
                    }

                    const int maximumWidth = 520;
                    const int maximumHeight = 356;
                    var scale = Math.Min(
                        1d,
                        Math.Min(maximumWidth / (double)source.Width, maximumHeight / (double)source.Height));
                    var width = Math.Max(1, (int)Math.Round(source.Width * scale));
                    var height = Math.Max(1, (int)Math.Round(source.Height * scale));
                    var result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                    using (var graphics = Graphics.FromImage(result))
                    {
                        graphics.Clear(Color.Transparent);
                        graphics.CompositingQuality = CompositingQuality.HighQuality;
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.SmoothingMode = SmoothingMode.HighQuality;
                        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
                    }

                    return result;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                 exception is ExternalException ||
                 exception is IOException ||
                 exception is NotSupportedException ||
                 exception is UnauthorizedAccessException ||
                 exception is OutOfMemoryException)
            {
                return null;
            }
        }

        private PresentationKind GetPresentationKind()
        {
            if (item == null)
            {
                return PresentationKind.Text;
            }

            switch (item.Kind)
            {
                case TimelineMessageKind.Link:
                    return PresentationKind.Link;
                case TimelineMessageKind.Attachment:
                    return IsImageAttachment(item) ? PresentationKind.Image : PresentationKind.Attachment;
                default:
                    return PresentationKind.Text;
            }
        }

        private static bool IsImageAttachment(TimelineItemViewModel value)
        {
            if (value == null || value.Kind != TimelineMessageKind.Attachment)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(value.MimeType) &&
                value.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var extension = Path.GetExtension(
                !string.IsNullOrWhiteSpace(value.AbsolutePath) ? value.AbsolutePath : value.RelativePath);
            return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".tif", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".tiff", StringComparison.OrdinalIgnoreCase);
        }

        private void RecalculateHeight()
        {
            if (updatingLayout || IsDisposed)
            {
                return;
            }

            updatingLayout = true;
            try
            {
                var width = ClientSize.Width > 0 ? ClientSize.Width : Math.Max(Width, ScaleLogical(440));
                var preferredHeight = CalculateLayout(width);
                if (Height != preferredHeight)
                {
                    Height = preferredHeight;
                }
            }
            finally
            {
                updatingLayout = false;
            }
        }

        private void ResetLayoutRectangles()
        {
            bubbleBounds = Rectangle.Empty;
            senderBounds = Rectangle.Empty;
            contentBounds = Rectangle.Empty;
            metaBounds = Rectangle.Empty;
            copyHitBounds = Rectangle.Empty;
            revealHitBounds = Rectangle.Empty;
            thumbnailBounds = Rectangle.Empty;
            attachmentIconBounds = Rectangle.Empty;
            attachmentNameBounds = Rectangle.Empty;
            attachmentDetailBounds = Rectangle.Empty;
        }

        private static void OffsetIfNotEmpty(ref Rectangle rectangle, int x, int y)
        {
            if (!rectangle.IsEmpty)
            {
                rectangle.Offset(x, y);
            }
        }

        private int ScaleLogical(int value)
        {
            return Math.Max(1, (int)Math.Round(value * DeviceDpi / 96d));
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            if (maximum < minimum)
            {
                return maximum;
            }

            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static Size MeasureSingleLine(string text, Font font)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Size.Empty;
            }

            return TextRenderer.MeasureText(
                text,
                font,
                Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        private static string FormatTimestamp(DateTime timestamp)
        {
            if (timestamp == default(DateTime))
            {
                return string.Empty;
            }

            var local = timestamp.Kind == DateTimeKind.Utc ? timestamp.ToLocalTime() : timestamp;
            return local.Date == DateTime.Now.Date
                ? local.ToString("HH:mm")
                : local.ToString("MM-dd HH:mm");
        }

        private static string FormatBytes(long bytes)
        {
            var units = new[] { "B", "KB", "MB", "GB", "TB" };
            var value = (double)bytes;
            var unit = 0;
            while (value >= 1024d && unit < units.Length - 1)
            {
                value /= 1024d;
                unit++;
            }

            return unit == 0 ? bytes + " " + units[unit] : value.ToString("0.#") + " " + units[unit];
        }

        private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            if (bounds.Width <= 1 || bounds.Height <= 1)
            {
                path.AddRectangle(bounds);
                return path;
            }

            var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            var arc = new Rectangle(bounds.Left, bounds.Top, diameter, diameter);
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Rectangle FitInside(Size source, Rectangle bounds)
        {
            if (source.Width <= 0 || source.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            {
                return Rectangle.Empty;
            }

            var scale = Math.Min(bounds.Width / (double)source.Width, bounds.Height / (double)source.Height);
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            return new Rectangle(
                bounds.Left + (bounds.Width - width) / 2,
                bounds.Top + (bounds.Height - height) / 2,
                width,
                height);
        }

        private static Rectangle Deflate(Rectangle rectangle, int amount)
        {
            rectangle.Inflate(-amount, -amount);
            return rectangle;
        }

        private static string BuildAccessibleName(TimelineItemViewModel value)
        {
            var sender = value.IsOutgoing
                ? "我"
                : (string.IsNullOrWhiteSpace(value.SenderName) ? "其他设备" : value.SenderName.Trim());
            switch (GetPresentationKind(value))
            {
                case PresentationKind.Attachment:
                    return sender + "发送的附件";
                case PresentationKind.Image:
                    return sender + "发送的图片";
                case PresentationKind.Link:
                    return sender + "发送的链接";
                default:
                    return sender + "发送的文字";
            }
        }

        private static string BuildAccessibleDescription(TimelineItemViewModel value)
        {
            switch (GetPresentationKind(value))
            {
                case PresentationKind.Text:
                    return value.Text ?? string.Empty;
                case PresentationKind.Link:
                    return !string.IsNullOrWhiteSpace(value.Url) ? value.Url : value.Text ?? string.Empty;
                case PresentationKind.Attachment:
                case PresentationKind.Image:
                    return !string.IsNullOrWhiteSpace(value.RelativePath)
                        ? value.RelativePath
                        : value.AbsolutePath ?? string.Empty;
                default:
                    return string.Empty;
            }
        }

        private static PresentationKind GetPresentationKind(TimelineItemViewModel value)
        {
            if (value == null)
            {
                return PresentationKind.Text;
            }

            if (value.Kind == TimelineMessageKind.Link)
            {
                return PresentationKind.Link;
            }

            if (value.Kind == TimelineMessageKind.Attachment)
            {
                return IsImageAttachment(value) ? PresentationKind.Image : PresentationKind.Attachment;
            }

            return PresentationKind.Text;
        }
    }
}

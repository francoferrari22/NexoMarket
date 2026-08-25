using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NexoMarket.Admin.UI
{
    internal static class Theme
    {
        public static readonly Color Background = Color.FromArgb(0, 0, 0);
        public static readonly Color Sidebar = Color.FromArgb(5, 5, 7);
        public static readonly Color CardBackground = Color.FromArgb(8, 8, 10);
        public static readonly Color Card2 = Color.FromArgb(14, 14, 17);
        public static readonly Color Line = Color.FromArgb(45, 45, 50);
        public static readonly Color Text = Color.FromArgb(244, 247, 252);
        public static readonly Color Muted = Color.FromArgb(157, 169, 188);
        public static readonly Color Accent = Color.FromArgb(245, 245, 250);
        public static readonly Color AccentDark = Color.FromArgb(190, 190, 200);
        public static readonly Color Green = Color.FromArgb(57, 255, 102);
        public static readonly Color NeonGreen = Color.FromArgb(57, 255, 20);
        public static readonly Color NeonWhite = Color.FromArgb(255, 255, 255);
        public static readonly Color Warning = Color.FromArgb(255, 220, 50);
        public static readonly Color Danger = Color.FromArgb(255, 55, 70);

        public static Font Font(float size, FontStyle style)
        {
            return new Font("Segoe UI", size, style, GraphicsUnit.Point);
        }

        public static Font Font(float size)
        {
            return Font(size, FontStyle.Regular);
        }

        public static Button NavButton(string text)
        {
            ModernButton b = new ModernButton();
            b.Text = text;
            b.Height = 44;
            b.Width = 205;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Sidebar;
            b.NormalBackColor = Sidebar;
            b.HoverBackColor = Card2;
            b.PressedBackColor = AccentDark;
            b.ForeColor = Muted;
            b.TextAlign = ContentAlignment.MiddleLeft;
            b.Padding = new Padding(16, 0, 8, 0);
            b.Font = Font(9.5f, FontStyle.Regular);
            b.Cursor = Cursors.Hand;
            return b;
        }

        public static Button Primary(string text)
        {
            ModernButton b = new ModernButton();
            b.Text = text;
            b.Height = 40;
            b.AutoSize = true;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Accent;
            b.NormalBackColor = Accent;
            b.HoverBackColor = Color.FromArgb(255, 255, 255);
            b.PressedBackColor = AccentDark;
            b.ForeColor = Color.White;
            b.Font = Font(9f, FontStyle.Bold);
            b.Padding = new Padding(16, 0, 16, 0);
            b.Cursor = Cursors.Hand;
            return b;
        }

        public static Button Secondary(string text)
        {
            ModernButton b = (ModernButton)Primary(text);
            b.BackColor = Card2;
            b.NormalBackColor = Card2;
            b.HoverBackColor = Color.FromArgb(24, 24, 28);
            b.PressedBackColor = Color.FromArgb(25, 39, 61);
            b.ForeColor = Text;
            return b;
        }

        public static Panel Card()
        {
            ModernCardPanel p = new ModernCardPanel();
            p.BackColor = CardBackground;
            p.Padding = new Padding(18);
            p.Margin = new Padding(7);
            return p;
        }

        public static Panel HeroCard()
        {
            ModernHeroPanel p = new ModernHeroPanel();
            p.BackColor = CardBackground;
            p.Padding = new Padding(18);
            p.Margin = new Padding(7);
            return p;
        }

        public static DataGridView Grid()
        {
            DataGridView g = new DataGridView();
            g.BackgroundColor = CardBackground;
            g.BorderStyle = BorderStyle.None;
            g.GridColor = Line;
            g.ForeColor = Text;
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersDefaultCellStyle.BackColor = Card2;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Text;
            g.ColumnHeadersDefaultCellStyle.Font = Font(9f, FontStyle.Bold);
            g.ColumnHeadersHeight = 40;
            g.DefaultCellStyle.BackColor = Color.FromArgb(27, 34, 45);
            g.DefaultCellStyle.ForeColor = Text;
            g.DefaultCellStyle.SelectionBackColor = Accent;
            g.DefaultCellStyle.SelectionForeColor = Color.White;
            g.RowTemplate.Height = 36;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            g.ScrollBars = ScrollBars.Both;
            g.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            g.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.ReadOnly = true;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.MultiSelect = false;
            g.RowHeadersVisible = false;
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            return g;
        }
    }

    internal sealed class ModernButton : Button
    {
        public Color NormalBackColor = Theme.Accent;
        public Color HoverBackColor = Color.FromArgb(255, 255, 255);
        public Color PressedBackColor = Theme.AccentDark;
        public string ShortcutText = "";
        private bool _hover;
        private bool _pressed;

        public ModernButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            MouseEnter += delegate { _hover = true; Invalidate(); };
            MouseLeave += delegate { _hover = false; _pressed = false; Invalidate(); };
            MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } };
            MouseUp += delegate { _pressed = false; Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            Color baseColor = BackColor;
            Color top = _pressed ? PressedBackColor : (_hover ? HoverBackColor : baseColor);
            Color bottom = _pressed ? top : ControlPaint.Dark(top, 0.10f);
            using (GraphicsPath path = Rounded(r, 7))
            using (LinearGradientBrush brush = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical))
            {
                e.Graphics.FillPath(brush, path);
                if (_hover)
                {
                    using (Pen glow1 = new Pen(Color.FromArgb(34, 255, 255, 255), 4)) e.Graphics.DrawPath(glow1, path);
                    using (Pen glow2 = new Pen(Color.FromArgb(95, 255, 255, 255), 1)) e.Graphics.DrawPath(glow2, path);
                }
                else
                {
                    using (Pen pen = new Pen(Color.FromArgb(70, Color.White), 1)) e.Graphics.DrawPath(pen, path);
                }
            }
            if (!string.IsNullOrWhiteSpace(ShortcutText))
            {
                Rectangle textRect = new Rectangle(r.X + 14, r.Y, Math.Max(1, r.Width - 58), r.Height);
                TextRenderer.DrawText(e.Graphics, Text, Font, textRect, ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                Rectangle keyRect = new Rectangle(r.Right - 50, r.Y + 8, 38, Math.Max(1, r.Height - 16));
                using (GraphicsPath keyPath = Rounded(keyRect, 5))
                using (SolidBrush keyBrush = new SolidBrush(Color.FromArgb(70, Color.White)))
                using (Pen keyPen = new Pen(Color.FromArgb(85, Color.White), 1))
                {
                    e.Graphics.FillPath(keyBrush, keyPath); e.Graphics.DrawPath(keyPen, keyPath);
                }
                using (Font keyFont = Theme.Font(7.5f, FontStyle.Bold))
                    TextRenderer.DrawText(e.Graphics, ShortcutText, keyFont, keyRect, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
            {
                TextRenderer.DrawText(e.Graphics, Text, Font, r, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        private GraphicsPath Rounded(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal sealed class ModernHeroPanel : Panel
    {
        public ModernHeroPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Rectangle r = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 4));
            using (GraphicsPath path = Rounded(r, 11))
            using (LinearGradientBrush brush = new LinearGradientBrush(r, Color.FromArgb(31, 42, 60), Color.FromArgb(21, 27, 38), LinearGradientMode.Horizontal))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
                using (Pen pen = new Pen(Color.FromArgb(85, Theme.Accent), 1)) e.Graphics.DrawPath(pen, path);
            }
        }

        private GraphicsPath Rounded(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal sealed class TexturedPanel : Panel
    {
        public int TextureOpacity { get; set; }

        public TexturedPanel()
        {
            TextureOpacity = 70;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Rectangle r = ClientRectangle;
            if (r.Width <= 0 || r.Height <= 0) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Theme.Background);
            using (SolidBrush overlay = new SolidBrush(Color.FromArgb(Math.Max(0, Math.Min(255, TextureOpacity)), Theme.Sidebar)))
                e.Graphics.FillRectangle(overlay, r);

            using (GraphicsPath glowPath = new GraphicsPath())
            {
                glowPath.AddBezier(new Point(-80, r.Height / 3), new Point(r.Width / 5, -20), new Point(r.Width / 2, r.Height + 20), new Point(r.Width + 100, r.Height / 3));
                using (Pen glow = new Pen(Color.FromArgb(18, Theme.NeonGreen), 3)) e.Graphics.DrawPath(glow, glowPath);
                using (Pen line = new Pen(Color.FromArgb(42, Theme.NeonGreen), 1)) e.Graphics.DrawPath(line, glowPath);
            }
            using (GraphicsPath curve = new GraphicsPath())
            {
                curve.AddBezier(new Point(-40, r.Height - 40), new Point(r.Width / 3, r.Height / 2), new Point(r.Width * 2 / 3, r.Height / 2), new Point(r.Width + 50, 80));
                using (Pen glow = new Pen(Color.FromArgb(15, 167, 103, 255), 3)) e.Graphics.DrawPath(glow, curve);
                using (Pen line = new Pen(Color.FromArgb(38, 167, 103, 255), 1)) e.Graphics.DrawPath(line, curve);
            }
            using (GraphicsPath whiteCurve = new GraphicsPath())
            {
                whiteCurve.AddBezier(new Point(r.Width + 30, r.Height - 80), new Point(r.Width * 3 / 4, r.Height / 2), new Point(r.Width / 2, r.Height / 3), new Point(-30, 70));
                using (Pen glow = new Pen(Color.FromArgb(10, 255, 255, 255), 3)) e.Graphics.DrawPath(glow, whiteCurve);
                using (Pen line = new Pen(Color.FromArgb(26, 255, 255, 255), 1)) e.Graphics.DrawPath(line, whiteCurve);
            }
            using (LinearGradientBrush shade = new LinearGradientBrush(r, Color.FromArgb(75, 0, 0, 0), Color.FromArgb(18, 0, 0, 0), LinearGradientMode.Horizontal))
                e.Graphics.FillRectangle(shade, r);
        }
    }

    internal sealed class ModernCardPanel : Panel
    {
        public ModernCardPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle shadow = new Rectangle(3, 4, Math.Max(1, Width - 5), Math.Max(1, Height - 5));
            using (GraphicsPath shadowPath = Rounded(shadow, 9))
            using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(45, Color.Black)))
                e.Graphics.FillPath(shadowBrush, shadowPath);

            Rectangle r = new Rectangle(1, 1, Math.Max(1, Width - 4), Math.Max(1, Height - 5));
            using (GraphicsPath path = Rounded(r, 9))
            using (LinearGradientBrush brush = new LinearGradientBrush(r, Theme.CardBackground, Color.FromArgb(31, 39, 53), LinearGradientMode.Vertical))
            {
                e.Graphics.FillPath(brush, path);
                using (Pen pen = new Pen(Theme.Line, 1)) e.Graphics.DrawPath(pen, path);
            }
        }

        private GraphicsPath Rounded(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}

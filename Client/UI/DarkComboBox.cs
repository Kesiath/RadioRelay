using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace RadioRelay.Client.UI
{
    /// 
    /// Simple dark selector for compact radio-row values such as EAR routing.
    /// This is intentionally not a native WinForms ComboBox: native ComboBox
    /// hover/focus chrome can briefly repaint a light or outlined box before
    /// owner-draw code gets control. This control paints a stable borderless
    /// collapsed face and uses a dark ListBox popup for the dropdown.
    /// 
    public sealed class DarkComboBox : Control
    {
        private int _selectedIndex = -1;
        private ToolStripDropDown? _dropDown;

        public DarkComboBox()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable |
                ControlStyles.UserPaint,
                true);

            Items = new List<object>();
            BackColor = Theme.FieldBackground;
            ForeColor = Theme.Text;
            Font = Theme.MonoFont;
            Margin = new Padding(0);
            Height = 21;
            TabStop = true;
        }

        public event EventHandler? SelectedIndexChanged;

        public List<object> Items { get; }

        public int DropDownWidth { get; set; } = 96;

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                var coerced = value >= 0 && value < Items.Count ? value : -1;
                if (_selectedIndex == coerced)
                {
                    return;
                }

                _selectedIndex = coerced;
                Text = SelectedItem?.ToString() ?? string.Empty;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public object? SelectedItem
        {
            get => _selectedIndex >= 0 && _selectedIndex < Items.Count ? Items[_selectedIndex] : null;
            set
            {
                for (var i = 0; i < Items.Count; i++)
                {
                    if (Equals(Items[i], value))
                    {
                        SelectedIndex = i;
                        return;
                    }
                }

                SelectedIndex = -1;
            }
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            ShowDropDown();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.KeyCode is Keys.Enter or Keys.Space or Keys.Down)
            {
                ShowDropDown();
                e.Handled = true;
                return;
            }

            if (e.KeyCode == Keys.Up && Items.Count > 0)
            {
                SelectedIndex = Math.Max(0, SelectedIndex - 1);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Down && Items.Count > 0)
            {
                SelectedIndex = Math.Min(Items.Count - 1, SelectedIndex + 1);
                e.Handled = true;
            }
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            PaintCollapsedFace(e.Graphics);
        }

        private void ShowDropDown()
        {
            if (!Enabled || Items.Count == 0)
            {
                return;
            }

            CloseDropDown(_dropDown);

            var listBox = new ListBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.FieldBackground,
                ForeColor = Theme.Text,
                Font = Font,
                DrawMode = DrawMode.OwnerDrawFixed,
                IntegralHeight = false,
                ItemHeight = Math.Max(18, Font.Height + 4),
                Width = Math.Max(Width, DropDownWidth),
                Height = Math.Min(10, Math.Max(1, Items.Count)) * Math.Max(18, Font.Height + 4)
            };
            listBox.Items.AddRange(Items.ToArray());
            listBox.SelectedIndex = SelectedIndex;
            listBox.DrawItem += (_, e) => DrawDropDownItem(listBox, e);
            listBox.Click += (_, _) =>
            {
                if (listBox.SelectedIndex >= 0)
                {
                    SelectedIndex = listBox.SelectedIndex;
                }

                CloseDropDown(_dropDown);
            };
            listBox.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter && listBox.SelectedIndex >= 0)
                {
                    SelectedIndex = listBox.SelectedIndex;
                    CloseDropDown(_dropDown);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    CloseDropDown(_dropDown);
                    e.Handled = true;
                }
            };

            var host = new ToolStripControlHost(listBox)
            {
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                AutoSize = false,
                Size = listBox.Size
            };

            _dropDown = new ToolStripDropDown
            {
                AutoClose = true,
                BackColor = Theme.FieldBackground,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };
            _dropDown.Items.Add(host);
            var dropDown = _dropDown;
            dropDown.Closed += (_, _) =>
            {
                if (ReferenceEquals(_dropDown, dropDown))
                {
                    _dropDown = null;
                }
            };
            dropDown.Show(this, new Point(0, Height));
            listBox.Focus();
        }

        private void CloseDropDown(ToolStripDropDown? dropDown)
        {
            if (dropDown == null)
            {
                return;
            }

            if (dropDown.IsDisposed)
            {
                if (ReferenceEquals(_dropDown, dropDown))
                {
                    _dropDown = null;
                }

                return;
            }

            dropDown.Close(ToolStripDropDownCloseReason.CloseCalled);
        }

        private static void DrawDropDownItem(ListBox listBox, DrawItemEventArgs e)
        {
            if (e.Index < 0)
            {
                return;
            }

            var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using var back = new SolidBrush(selected ? Theme.SoftBorder : Theme.FieldBackground);
            e.Graphics.FillRectangle(back, e.Bounds);

            var textBounds = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top, Math.Max(0, e.Bounds.Width - 12), e.Bounds.Height);
            TextRenderer.DrawText(
                e.Graphics,
                listBox.Items[e.Index]?.ToString() ?? string.Empty,
                listBox.Font,
                textBounds,
                Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private void PaintCollapsedFace(Graphics g)
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                return;
            }

            using var back = new SolidBrush(Theme.FieldBackground);
            g.FillRectangle(back, ClientRectangle);

            var arrowWidth = Math.Min(18, Math.Max(12, ClientSize.Width / 4));
            var textRect = new Rectangle(6, 0, Math.Max(0, ClientSize.Width - arrowWidth - 8), ClientSize.Height);
            TextRenderer.DrawText(
                g,
                Text,
                Font,
                textRect,
                Enabled ? Theme.Text : Theme.FaintText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            PaintArrow(g, new Rectangle(ClientSize.Width - arrowWidth, 0, arrowWidth, ClientSize.Height));
        }

        private static void PaintArrow(Graphics g, Rectangle bounds)
        {
            var centerX = bounds.Left + bounds.Width / 2;
            var centerY = bounds.Top + bounds.Height / 2 + 1;
            var points = new[]
            {
                new Point(centerX - 4, centerY - 2),
                new Point(centerX + 4, centerY - 2),
                new Point(centerX, centerY + 2)
            };

            using var brush = new SolidBrush(Theme.MutedText);
            g.FillPolygon(brush, points);
        }
    }
}

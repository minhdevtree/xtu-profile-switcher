// The profile table and the tab strip.
//
// Clicking a row applies that profile (that is what people expect from a list of profiles).
// Hotkey and delete are secondary, so they live in a "..." menu at the end of each row
// (right-click and the Menu key open it too).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XtuProfileSwitcher
{
    internal sealed class Row
    {
        public string Name, Key;
        public decimal Pl1, Pl2, Window;
        public bool Pl2On = true, Active, Custom;
        public object Tag;
    }

    internal sealed class RowEventArgs : EventArgs
    {
        public readonly Row Row;
        public readonly Point Anchor;   // in the table's own coordinates
        public RowEventArgs(Row row, Point anchor) { Row = row; Anchor = anchor; }
    }

    // Hairlines, right-aligned numbers, a drawn radio mark for the profile in use.
    internal sealed class ProfileTable : Flat
    {
        private readonly List<Row> rows = new List<Row>();
        private int hover = -1, focus = -1, pressed = -1, scroll;

        public event EventHandler<RowEventArgs> Activated;
        public event EventHandler<RowEventArgs> MenuRequested;

        private int HeaderH { get { return Ui.S(30); } }
        private int RowH { get { return Ui.S(40); } }
        private int DotsW { get { return Ui.S(40); } }

        public ProfileTable()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
        }

        public void SetRows(List<Row> newRows)
        {
            string keep = focus >= 0 && focus < rows.Count ? rows[focus].Name : null;
            rows.Clear();
            rows.AddRange(newRows);
            focus = -1;
            if (keep != null)
                for (int i = 0; i < rows.Count; i++) if (rows[i].Name == keep) focus = i;
            hover = -1;
            pressed = -1;
            ClampScroll();
            Invalidate();
        }

        // Used by the --snapshot --click self-test: where a click on this row (or its "..." button) lands.
        public Point RowPoint(string name, bool dots)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Name == name)
                    return new Point(dots ? RightEdge - DotsW / 2 : Ui.S(120), HeaderH + i * RowH - scroll + RowH / 2);
            return new Point(-1, -1);
        }

        public void TestClick(Point p, MouseButtons button)
        {
            OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, p.X, p.Y, 0));
            OnMouseDown(new MouseEventArgs(button, 1, p.X, p.Y, 0));
            OnMouseUp(new MouseEventArgs(button, 1, p.X, p.Y, 0));
        }

        private int ContentH { get { return rows.Count * RowH; } }
        private int ViewH { get { return Math.Max(0, Height - HeaderH); } }
        private bool HasBar { get { return ContentH > ViewH; } }
        private int RightEdge { get { return Width - (HasBar ? Ui.S(10) : 0); } }

        private void ClampScroll()
        {
            int max = Math.Max(0, ContentH - ViewH);
            if (scroll > max) scroll = max;
            if (scroll < 0) scroll = 0;
        }

        private int IndexAt(Point p)
        {
            if (p.Y < HeaderH || p.X > RightEdge) return -1;
            int i = (p.Y - HeaderH + scroll) / RowH;
            return i >= 0 && i < rows.Count ? i : -1;
        }

        private Rectangle RowRect(int i) { return new Rectangle(0, HeaderH + i * RowH - scroll, RightEdge, RowH); }

        private bool InDots(Point p) { return p.X >= RightEdge - DotsW; }

        private void Raise(EventHandler<RowEventArgs> handler, int i, Point anchor)
        {
            if (handler != null && i >= 0 && i < rows.Count) handler(this, new RowEventArgs(rows[i], anchor));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i != hover) { hover = i; Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; pressed = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            int i = IndexAt(e.Location);
            if (e.Button == MouseButtons.Left && i >= 0) { pressed = i; focus = i; Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            int was = pressed;
            pressed = -1;
            Invalidate();
            if (i >= 0)
            {
                if (e.Button == MouseButtons.Right)
                    Raise(MenuRequested, i, e.Location);
                else if (e.Button == MouseButtons.Left && i == was)
                {
                    if (InDots(e.Location)) Raise(MenuRequested, i, new Point(RightEdge - DotsW, RowRect(i).Bottom));
                    else Raise(Activated, i, e.Location);
                }
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll -= Math.Sign(e.Delta) * RowH;
            ClampScroll();
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override bool IsInputKey(Keys k)
        {
            return k == Keys.Up || k == Keys.Down || k == Keys.Home || k == Keys.End || k == Keys.Enter || k == Keys.Space || base.IsInputKey(k);
        }

        private void MoveFocus(int i)
        {
            if (rows.Count == 0) return;
            focus = Math.Max(0, Math.Min(rows.Count - 1, i));
            int top = focus * RowH, bottom = top + RowH;
            if (top < scroll) scroll = top;
            if (bottom > scroll + ViewH) scroll = bottom - ViewH;
            ClampScroll();
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down) { MoveFocus(focus + 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Up) { MoveFocus(focus < 0 ? 0 : focus - 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Home) { MoveFocus(0); e.Handled = true; }
            else if (e.KeyCode == Keys.End) { MoveFocus(rows.Count - 1); e.Handled = true; }
            else if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && focus >= 0)
            { Raise(Activated, focus, new Point(Ui.S(40), RowRect(focus).Bottom)); e.Handled = true; }
            else if ((e.KeyCode == Keys.Apps || (e.Shift && e.KeyCode == Keys.F10)) && focus >= 0)
            { Raise(MenuRequested, focus, new Point(RightEdge - DotsW, RowRect(focus).Bottom)); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { if (focus < 0 && rows.Count > 0) focus = 0; Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnSizeChanged(EventArgs e) { ClampScroll(); base.OnSizeChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int pad = Ui.S(4);
            int right = RightEdge;
            int wKey = Ui.S(112), wWin = Ui.S(62), wPl2 = Ui.S(54), wPl1 = Ui.S(54);
            int xKey = right - DotsW - wKey, xWin = xKey - wWin, xPl2 = xWin - wPl2, xPl1 = xPl2 - wPl1;
            int xName = pad + Ui.S(26);

            // header: column names in the quiet colour, then a hairline
            Font small = Theme.Body(8.5f);
            Ui.Text(g, Loc.T("col.name"), small, Theme.Sub, new Rectangle(xName, 0, xPl1 - xName, HeaderH), TextFormatFlags.Left);
            Ui.Text(g, Loc.T("col.pl1"), small, Theme.Sub, new Rectangle(xPl1, 0, wPl1 - Ui.S(10), HeaderH), TextFormatFlags.Right);
            Ui.Text(g, Loc.T("col.pl2"), small, Theme.Sub, new Rectangle(xPl2, 0, wPl2 - Ui.S(10), HeaderH), TextFormatFlags.Right);
            Ui.Text(g, Loc.T("col.window"), small, Theme.Sub, new Rectangle(xWin, 0, wWin - Ui.S(10), HeaderH), TextFormatFlags.Right);
            Ui.Text(g, Loc.T("col.key"), small, Theme.Sub, new Rectangle(xKey + Ui.S(10), 0, wKey - Ui.S(10), HeaderH), TextFormatFlags.Left);
            using (Pen line = new Pen(Theme.Line)) g.DrawLine(line, 0, HeaderH - 1, right, HeaderH - 1);

            g.SetClip(new Rectangle(0, HeaderH, Width, Math.Max(0, Height - HeaderH)));
            Font name = Theme.Strong(10f), num = Theme.Body(10f), tag = Theme.Body(8.5f);
            for (int i = 0; i < rows.Count; i++)
            {
                Rectangle rr = RowRect(i);
                if (rr.Bottom < HeaderH || rr.Y > Height) continue;
                Row r = rows[i];
                int y = rr.Y;

                bool lit = i == hover || (i == focus && Focused);
                if (i == pressed)
                    using (SolidBrush b = new SolidBrush(Theme.Mix(Theme.Raised, Theme.Line, 0.6))) g.FillRectangle(b, rr);
                else if (lit)
                    using (SolidBrush b = new SolidBrush(Theme.Raised)) g.FillRectangle(b, rr);
                using (Pen line = new Pen(Theme.Line)) g.DrawLine(line, 0, y + RowH - 1, right, y + RowH - 1);

                // radio mark: a filled ember dot means "in use" (shape, not only colour)
                float cx = pad + Ui.S(9f), cy = y + RowH / 2f, d = Ui.S(11f);
                if (r.Active) Ui.Dot(g, Theme.Ember, cx, cy, d);
                else using (Pen ring = new Pen(i == hover ? Theme.Text : Theme.Sub, 1.3f)) g.DrawEllipse(ring, cx - d / 2, cy - d / 2, d, d);

                int nameW = Ui.Width(g, r.Name, name);
                Ui.Text(g, r.Name, name, Theme.Text, new Rectangle(xName, y, xPl1 - xName - Ui.S(8), RowH), TextFormatFlags.Left);
                int tx = xName + nameW + Ui.S(8);
                if (r.Active && tx + Ui.S(40) < xPl1)
                {
                    string inUse = Loc.T("row.inuse");
                    Ui.Text(g, inUse, tag, Theme.Ember, new Rectangle(tx, y, xPl1 - tx, RowH), TextFormatFlags.Left);
                    tx += Ui.Width(g, inUse, tag) + Ui.S(8);
                }
                if (r.Custom && tx + Ui.S(30) < xPl1)
                    Ui.Text(g, Loc.T("row.custom"), tag, Theme.Sub, new Rectangle(tx, y, xPl1 - tx, RowH), TextFormatFlags.Left);

                Ui.Text(g, r.Pl1.ToString("0.###"), num, Theme.Text, new Rectangle(xPl1, y, wPl1 - Ui.S(10), RowH), TextFormatFlags.Right);
                Ui.Text(g, r.Pl2On ? r.Pl2.ToString("0.###") : Loc.T("ro.off"), num, r.Pl2On ? Theme.Text : Theme.Sub,
                    new Rectangle(xPl2, y, wPl2 - Ui.S(10), RowH), TextFormatFlags.Right);
                Ui.Text(g, r.Window.ToString("0.###"), num, Theme.Text, new Rectangle(xWin, y, wWin - Ui.S(10), RowH), TextFormatFlags.Right);
                Ui.Text(g, string.IsNullOrEmpty(r.Key) ? "–" : r.Key, tag, Theme.Sub, new Rectangle(xKey + Ui.S(10), y, wKey - Ui.S(10), RowH), TextFormatFlags.Left);

                // the "..." menu: three drawn dots, stronger on the row under the pointer
                Color dc = lit ? Theme.Text : Theme.Sub;
                float mx = right - DotsW / 2f, dd = Ui.S(3.4f), gap = Ui.S(7f);
                for (int k = -1; k <= 1; k++) Ui.Dot(g, dc, mx + k * gap, cy, dd);
            }
            g.ResetClip();

            if (HasBar)
            {
                float trackH = ViewH, thumbH = Math.Max(Ui.S(24), trackH * ViewH / ContentH);
                float thumbY = HeaderH + (trackH - thumbH) * scroll / Math.Max(1, ContentH - ViewH);
                using (SolidBrush b = new SolidBrush(Theme.Line))
                    g.FillRectangle(b, Width - Ui.S(5), thumbY, Ui.S(3), thumbH);
            }
        }
    }

    // Two (or more) tabs: text, with a 2px line under the current one.
    internal sealed class TabStrip : Flat
    {
        public readonly List<string> Items = new List<string>();
        private int sel, hover = -1;
        private readonly List<Rectangle> rects = new List<Rectangle>();
        public event EventHandler SelectedChanged;

        public TabStrip()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Height = Ui.S(40);
        }

        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                if (value < 0 || value >= Items.Count || value == sel) return;
                sel = value;
                Invalidate();
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
        }

        private int At(Point p)
        {
            for (int i = 0; i < rects.Count; i++) if (rects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = At(e.Location);
            if (i != hover) { hover = i; Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int i = At(e.Location);
            if (e.Button == MouseButtons.Left && i >= 0) { Focus(); SelectedIndex = i; }
            base.OnMouseUp(e);
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Right) { SelectedIndex = Math.Min(Items.Count - 1, sel + 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Left) { SelectedIndex = Math.Max(0, sel - 1); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int m = Ui.S(28);
            rects.Clear();
            using (Pen line = new Pen(Theme.Line)) g.DrawLine(line, m, Height - 1, Width - m, Height - 1);

            int x = m;
            for (int i = 0; i < Items.Count; i++)
            {
                bool on = i == sel;
                Font f = on ? Theme.Strong(10.5f) : Theme.Body(10.5f);
                int w = Ui.Width(g, Items[i], Theme.Strong(10.5f));
                Rectangle r = new Rectangle(x, 0, w, Height);
                rects.Add(new Rectangle(x - Ui.S(8), 0, w + Ui.S(16), Height));
                Ui.Text(g, Items[i], f, on ? Theme.Text : (i == hover ? Theme.Text : Theme.Sub), r, TextFormatFlags.Left);
                if (on) using (Pen ul = new Pen(Theme.Text, Ui.S(2f))) g.DrawLine(ul, x, Height - Ui.S(2), x + w, Height - Ui.S(2));
                if (on && Focused && ShowFocusCues)
                    using (Pen ring = new Pen(Theme.Ember, 1.5f)) g.DrawRectangle(ring, x - Ui.S(6), Ui.S(6), w + Ui.S(12), Height - Ui.S(12));
                x += w + Ui.S(28);
            }
        }
    }
}

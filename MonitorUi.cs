// One metric over time: its name and current value on the left, the last few minutes as a line on the right.
// One line per row and a direct label instead of a legend; an optional dashed reference line (PL1 on the
// power row) so throttling can be read against the limit.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XtuProfileSwitcher
{
    internal sealed class SeriesRow : Flat
    {
        public string Title = "", Unit = "", Current = "–", RefLabel = "", SpanLabel = "", NowLabel = "";
        public Color Hue = Theme.Ember;
        public double[] Values = new double[0];
        public double Lo = 0, Hi = 100;
        public bool AutoHi;
        public double Ref = double.NaN;
        public int Capacity = 300;       // samples across the plot width

        public SeriesRow() { Height = Ui.S(74); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int m = Ui.S(28), leftW = Ui.S(150);

            Ui.Text(g, Title, Theme.Body(9f), Theme.Sub, new Rectangle(m, Ui.S(6), leftW, Ui.S(18)), TextFormatFlags.Left);
            Font fig = Theme.Figure(20f);
            int vw = Ui.Width(g, Current, fig);
            Ui.Text(g, Current, fig, Current == "–" ? Theme.Sub : Theme.Text, new Rectangle(m, Ui.S(24), vw + Ui.S(4), Ui.S(34)), TextFormatFlags.Left);
            if (Unit.Length > 0 && Current != "–")
                Ui.Text(g, Unit, Theme.Body(10f), Theme.Sub, new Rectangle(m + vw + Ui.S(4), Ui.S(30), Ui.S(40), Ui.S(26)), TextFormatFlags.Left);

            int px0 = m + leftW + Ui.S(10), px1 = Width - m, py0 = Ui.S(8), py1 = Height - Ui.S(22);
            if (px1 - px0 < Ui.S(60)) return;

            double hi = Hi;
            if (AutoHi)
            {
                double mx = double.IsNaN(Ref) ? 0 : Ref;
                foreach (double v in Values) if (!double.IsNaN(v) && v > mx) mx = v;
                hi = Math.Max(20, Math.Ceiling(mx * 1.15 / 10.0) * 10.0);
            }
            double lo = Lo, span = Math.Max(1e-6, hi - lo);

            using (Pen grid = new Pen(Theme.Line))
            {
                g.DrawLine(grid, px0, py0, px1, py0);
                g.DrawLine(grid, px0, (py0 + py1) / 2, px1, (py0 + py1) / 2);
                g.DrawLine(grid, px0, py1, px1, py1);
            }
            Font small = Theme.Body(8f);
            Ui.Text(g, hi.ToString("0.#"), small, Theme.Sub, new Rectangle(px1 - Ui.S(44), py0 + Ui.S(2), Ui.S(44), Ui.S(14)), TextFormatFlags.Right);
            Ui.Text(g, lo.ToString("0.#"), small, Theme.Sub, new Rectangle(px1 - Ui.S(44), py1 - Ui.S(16), Ui.S(44), Ui.S(14)), TextFormatFlags.Right);
            Ui.Text(g, SpanLabel, small, Theme.Sub, new Rectangle(px0, py1 + Ui.S(3), Ui.S(120), Ui.S(16)), TextFormatFlags.Left);
            Ui.Text(g, NowLabel, small, Theme.Sub, new Rectangle(px1 - Ui.S(60), py1 + Ui.S(3), Ui.S(60), Ui.S(16)), TextFormatFlags.Right);

            if (!double.IsNaN(Ref))
            {
                float ry = (float)(py1 - (Ref - lo) / span * (py1 - py0));
                if (ry > py0 && ry < py1)
                    using (Pen dash = new Pen(Theme.Sub, 1f))
                    {
                        dash.DashStyle = DashStyle.Dash;
                        g.DrawLine(dash, px0, ry, px1, ry);
                        Ui.Text(g, RefLabel, small, Theme.Sub, new Rectangle(px0 + Ui.S(4), (int)ry - Ui.S(15), Ui.S(120), Ui.S(14)), TextFormatFlags.Left);
                    }
            }

            int n = Values.Length;
            if (n == 0) return;
            float step = (px1 - px0) / (float)Math.Max(1, Capacity - 1);
            List<PointF> run = new List<PointF>();
            PointF lastPt = PointF.Empty;
            bool haveLast = false;
            using (Pen line = new Pen(Hue, Math.Max(1.6f, Ui.S(1.8f))))
            {
                line.LineJoin = LineJoin.Round;
                for (int i = 0; i < n; i++)
                {
                    double v = Values[i];
                    if (double.IsNaN(v))
                    {
                        if (run.Count > 1) g.DrawLines(line, run.ToArray());
                        run.Clear();
                        continue;
                    }
                    float x = px1 - (n - 1 - i) * step;
                    float y = (float)(py1 - (Math.Max(lo, Math.Min(hi, v)) - lo) / span * (py1 - py0));
                    run.Add(new PointF(x, y));
                    lastPt = new PointF(x, y);
                    haveLast = true;
                }
                if (run.Count > 1) g.DrawLines(line, run.ToArray());
            }
            if (haveLast) Ui.Dot(g, Hue, lastPt.X, lastPt.Y, Ui.S(6f));
        }
    }
}

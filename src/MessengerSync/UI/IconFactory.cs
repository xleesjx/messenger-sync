using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MessengerSync.UI;

public enum TrayState { Normal, Busy, Warning, Paused }

/// <summary>트레이 아이콘을 코드로 그린다 (상태별 색).</summary>
public static class IconFactory
{
    private static readonly Dictionary<TrayState, Icon> Cache = new();

    public static Icon Get(TrayState state)
    {
        if (Cache.TryGetValue(state, out var icon)) return icon;
        var color = state switch
        {
            TrayState.Busy => Color.FromArgb(37, 99, 235),
            TrayState.Warning => Color.FromArgb(220, 38, 38),
            TrayState.Paused => Color.FromArgb(120, 120, 120),
            _ => Color.FromArgb(13, 148, 136),
        };
        var size = SystemInformation.SmallIconSize.Width >= 24 ? 32 : 16;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var b = new SolidBrush(color);
            g.FillEllipse(b, 0, 0, size - 1, size - 1);
            using var pen = new Pen(Color.White, size / 8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var m = size * 0.27f;
            var r = new RectangleF(m, m, size - 2 * m, size - 2 * m);
            g.DrawArc(pen, r, 200, 130);  // 위쪽 화살표 호
            g.DrawArc(pen, r, 20, 130);   // 아래쪽 화살표 호
            var a = size * 0.14f;
            DrawHead(g, Color.White, new PointF(r.Right, r.Top + r.Height * 0.40f), a, true);
            DrawHead(g, Color.White, new PointF(r.Left, r.Top + r.Height * 0.60f), a, false);
        }
        var h = bmp.GetHicon();
        icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        Cache[state] = icon;
        return icon;
    }

    private static void DrawHead(Graphics g, Color c, PointF p, float a, bool down)
    {
        using var b = new SolidBrush(c);
        var pts = down
            ? new[] { new PointF(p.X - a, p.Y - a * 0.3f), new PointF(p.X + a, p.Y - a * 0.3f), new PointF(p.X, p.Y + a) }
            : new[] { new PointF(p.X - a, p.Y + a * 0.3f), new PointF(p.X + a, p.Y + a * 0.3f), new PointF(p.X, p.Y - a) };
        g.FillPolygon(b, pts);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}

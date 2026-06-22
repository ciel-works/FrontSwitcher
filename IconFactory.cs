using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace FrontSwitcher;

/// <summary>トレイ用の「☑」風チェックマークアイコンを実行時に生成する。</summary>
internal static class IconFactory
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>白地の角丸チェックボックスに緑のチェックを描いたアイコンを返す。</summary>
    public static Icon CreateCheckIcon()
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // チェックボックス（白地＋濃いグレーの枠）
            var box = new RectangleF(3f, 3f, size - 7f, size - 7f);
            using var path = RoundedRect(box, 6f);
            using var fill = new SolidBrush(Color.White);
            using var border = new Pen(Color.FromArgb(80, 80, 80), 2.4f);
            g.FillPath(fill, path);
            g.DrawPath(border, path);

            // チェックマーク（緑・太め・丸端）
            using var check = new Pen(Color.FromArgb(38, 166, 65), 4.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            PointF[] stroke =
            {
                new(9f, 16.5f),
                new(14f, 22f),
                new(24f, 9.5f),
            };
            g.DrawLines(check, stroke);
        }

        // GDI ハンドルから管理 Icon を作り、ハンドルは解放する
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

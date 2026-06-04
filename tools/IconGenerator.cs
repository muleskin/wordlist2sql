using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

class IconGen
{
    static void Main()
    {
        string outPath = @"C:\Users\Jedd\Desktop\Developing\Private\wordlist2sql\app.ico";
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };

        var dibs = new System.Collections.Generic.List<byte[]>();
        foreach (int s in sizes)
        {
            using (var bmp = Render(s))
                dibs.Add(ToDib(bmp));
        }

        WriteIco(outPath, sizes, dibs);
        Console.WriteLine("Wrote " + outPath + " (" + new FileInfo(outPath).Length + " bytes, " + sizes.Length + " sizes)");
    }

    static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            float S = size;
            float radius = S * 0.20f;

            // --- Rounded background with blue gradient ---
            var bgRect = new RectangleF(S * 0.04f, S * 0.04f, S * 0.92f, S * 0.92f);
            using (var path = Rounded(bgRect, radius))
            using (var brush = new LinearGradientBrush(bgRect,
                       Color.FromArgb(255, 56, 130, 246),   // #3882F6
                       Color.FromArgb(255, 23, 49, 110),    // #17316E
                       LinearGradientMode.Vertical))
            {
                g.FillPath(brush, path);
            }

            // --- Database cylinder (SQLite) ---
            float cx = S * 0.50f;
            float cylW = S * 0.46f;
            float left = cx - cylW / 2f;
            float ellH = S * 0.135f;       // ellipse cap height
            float top = S * 0.245f;        // top of top-cap
            float bottom = S * 0.70f;      // center line of bottom cap
            float bodyTop = top + ellH / 2f;
            float bodyBot = bottom;

            var capWhite = Color.FromArgb(255, 248, 250, 252);
            var bodyWhite = Color.FromArgb(255, 226, 235, 248);

            // Cylinder body (rectangle between cap centers) + side fill
            var bodyRect = new RectangleF(left, bodyTop, cylW, bodyBot - bodyTop);
            using (var bodyBrush = new LinearGradientBrush(
                       new RectangleF(left, bodyTop - 1, cylW, (bodyBot - bodyTop) + 2),
                       capWhite, bodyWhite, LinearGradientMode.Horizontal))
            {
                g.FillRectangle(bodyBrush, bodyRect);
            }
            // Bottom cap
            using (var capBrush = new SolidBrush(bodyWhite))
                g.FillEllipse(capBrush, left, bodyBot - ellH / 2f, cylW, ellH);
            // Re-fill body front edge so bottom cap blends
            using (var bodyBrush = new SolidBrush(capWhite))
                g.FillRectangle(bodyBrush, left, bodyTop, cylW, bodyBot - bodyTop - ellH * 0.0f);
            using (var capBrush2 = new SolidBrush(bodyWhite))
                g.FillEllipse(capBrush2, left, bodyBot - ellH / 2f, cylW, ellH);

            // Accent ring bands (the "stripes" of a DB icon)
            float pen = Math.Max(1f, S * 0.022f);
            var accent = Color.FromArgb(255, 37, 99, 235); // #2563EB
            using (var ringPen = new Pen(accent, pen))
            {
                float band1 = bodyTop + (bodyBot - bodyTop) * 0.33f;
                float band2 = bodyTop + (bodyBot - bodyTop) * 0.66f;
                g.DrawArc(ringPen, left, band1 - ellH / 2f, cylW, ellH, 20, 140);
                g.DrawArc(ringPen, left, band2 - ellH / 2f, cylW, ellH, 20, 140);
            }

            // Top cap (drawn last so it sits on top)
            using (var topBrush = new SolidBrush(capWhite))
                g.FillEllipse(topBrush, left, top, cylW, ellH);
            using (var topPen = new Pen(accent, pen))
                g.DrawEllipse(topPen, left, top, cylW, ellH);
        }
        return bmp;
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2f;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // Encode a 32bpp bitmap as a DIB icon image: BITMAPINFOHEADER with a
    // doubled height (XOR colour bitmap + AND mask), bottom-up rows.
    static byte[] ToDib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        byte[] src = new byte[stride * h];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, src, 0, src.Length);
        bmp.UnlockBits(data);

        int xorRow = w * 4;
        int andRow = ((w + 31) / 32) * 4; // 1bpp mask, 32-bit aligned
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            // BITMAPINFOHEADER
            bw.Write(40);            // biSize
            bw.Write(w);             // biWidth
            bw.Write(h * 2);         // biHeight = colour + mask
            bw.Write((short)1);      // biPlanes
            bw.Write((short)32);     // biBitCount
            bw.Write(0);             // biCompression = BI_RGB
            bw.Write(xorRow * h);    // biSizeImage (colour only)
            bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);

            // XOR colour data, bottom-up (BGRA already in src as 32bppArgb LE)
            for (int y = h - 1; y >= 0; y--)
                bw.Write(src, y * stride, xorRow);

            // AND mask: alpha drives transparency, so an all-zero mask is fine.
            byte[] zeroRow = new byte[andRow];
            for (int y = 0; y < h; y++)
                bw.Write(zeroRow, 0, andRow);

            return ms.ToArray();
        }
    }

    static void WriteIco(string path, int[] sizes, System.Collections.Generic.List<byte[]> imgs)
    {
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0);            // reserved
            w.Write((short)1);            // type = icon
            w.Write((short)sizes.Length); // image count

            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s)); // width  (0 => 256)
                w.Write((byte)(s >= 256 ? 0 : s)); // height
                w.Write((byte)0);  // palette
                w.Write((byte)0);  // reserved
                w.Write((short)1); // color planes
                w.Write((short)32);// bits per pixel
                w.Write(imgs[i].Length);
                w.Write(offset);
                offset += imgs[i].Length;
            }
            foreach (var img in imgs)
                w.Write(img);
        }
    }
}

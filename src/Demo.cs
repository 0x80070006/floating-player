using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace FloatingPlayer
{
    // Fictional tracks with generated artwork, used for screenshots (--demo=1, 2 or 3).
    static class Demo
    {
        public static MediaSnapshot Snapshot(string variant)
        {
            string[] titles = { "Lignes de fuite", "Marée basse", "Papier calque" };
            string[] artists = { "Atelier Nord", "Les Phares", "Orchestre Minuit" };
            Color[,] colors =
            {
                { Color.FromArgb(24, 62, 120), Color.FromArgb(240, 142, 44) },
                { Color.FromArgb(22, 96, 88), Color.FromArgb(236, 214, 120) },
                { Color.FromArgb(112, 34, 58), Color.FromArgb(244, 170, 150) }
            };
            int i;
            if (!int.TryParse(variant, out i) || i < 1 || i > 3) i = 1;
            i--;
            byte[] art = Cover(colors[i, 0], colors[i, 1], i);
            return new MediaSnapshot
            {
                HasSession = true, App = "Démo", Title = titles[i], Artist = artists[i], Playing = true,
                CanPrevious = true, CanNext = true, CanToggle = true, CanSeek = true, Art = art, ArtKey = "demo" + i,
                Position = TimeSpan.FromSeconds(82 + i * 40), Duration = TimeSpan.FromSeconds(214), PositionAt = DateTime.UtcNow
            };
        }

        static byte[] Cover(Color back, Color front, int shape)
        {
            using (var bitmap = new Bitmap(320, 320))
            using (var g = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(front))
            using (var stream = new MemoryStream())
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(back);
                if (shape == 0) g.FillEllipse(brush, 110, 70, 150, 150);
                else if (shape == 1) g.FillRectangle(brush, 0, 205, 320, 55);
                else g.FillPolygon(brush, new[] { new Point(50, 260), new Point(160, 70), new Point(270, 260) });
                bitmap.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }
    }
}
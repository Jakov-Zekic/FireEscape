using System;
using System.Collections.Generic;
using System.Globalization;
using FireEscape.Core;
using UnityEngine;

namespace FireEscape
{
    /// <summary>Self-labelled probability map for PNG export, independent of camera and display size.</summary>
    public static class HeatmapImage
    {
        private static readonly Dictionary<char, string[]> Font = MakeFont();
        private static readonly Color32 Background = new Color32(12, 20, 29, 255);
        private static readonly Color32 Ink = new Color32(224, 240, 246, 255);
        private static readonly Color32 Muted = new Color32(146, 174, 190, 255);

        public static Texture2D Create(GridMap map, double[] probabilities, double time, bool final, int runCount)
        {
            if (map == null || probabilities == null || probabilities.Length != map.Cells.Length)
                throw new ArgumentException("A map and matching probability array are required.");
            int cell = Mathf.Clamp(720 / Math.Max(map.Width, map.Depth), 6, 18);
            int margin = 20, header = 76, footer = 103;
            int mapWidth = map.Width * cell, mapHeight = map.Depth * cell;
            int width = Math.Max(440, mapWidth + margin * 2), height = header + mapHeight + footer;
            int mapX = (width - mapWidth) / 2;
            var pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = Background;
            string title = final ? "FINAL POSITIONS" : "POSITION AT T = " + time.ToString("0.00", CultureInfo.InvariantCulture) + " S";
            Write(pixels, width, height, margin, 16, title, Ink, 2);
            string detail = "N = " + runCount.ToString(CultureInfo.InvariantCulture) + "  |  CELL = " + map.CellSize.ToString("0.00", CultureInfo.InvariantCulture) + " M  |  TOP = +Z";
            Write(pixels, width, height, margin, 43, detail, Muted, 1);
            if (final) Write(pixels, width, height, margin, 58, "EACH RUN AT ITS OWN TERMINATION TIME", Muted, 1);
            else Write(pixels, width, height, margin, 58, "TERMINAL POSITIONS ARE RETAINED AFTER EXIT OR FAILURE", Muted, 1);
            for (int z = 0; z < map.Depth; z++)
                for (int x = 0; x < map.Width; x++)
                {
                    int index = z * map.Width + x;
                    Cell modelCell = map.Cells[index];
                    Color32 colour = modelCell.Blocked ? new Color32(33, 43, 51, 255) : new Color32(65, 78, 86, 255);
                    if (probabilities[index] > 0)
                    {
                        Color p = EnvironmentView.ProbabilityColor((float)probabilities[index]); p.a = 1;
                        colour = p;
                    }
                    int px = mapX + x * cell, py = header + (map.Depth - 1 - z) * cell;
                    Rect(pixels, width, height, px, py, cell, cell, colour);
                    // Cell boundaries preserve the estimator's actual spatial resolution.
                    Rect(pixels, width, height, px, py, cell, 1, new Color32(23, 33, 43, 255));
                    Rect(pixels, width, height, px, py, 1, cell, new Color32(23, 33, 43, 255));
                    if (modelCell.Exit && !modelCell.Blocked)
                    {
                        Color32 green = new Color32(99, 246, 163, 255);
                        Rect(pixels, width, height, px + 1, py + 1, cell - 2, 1, green);
                        Rect(pixels, width, height, px + 1, py + cell - 2, cell - 2, 1, green);
                        Rect(pixels, width, height, px + 1, py + 1, 1, cell - 2, green);
                        Rect(pixels, width, height, px + cell - 2, py + 1, 1, cell - 2, green);
                    }
                }
            int footerY = header + mapHeight;
            Write(pixels, width, height, margin, footerY + 12, "P(CELL) = COUNT / N   |   FIXED ABSOLUTE SCALE", Ink, 1);
            int legendX = margin, legendY = footerY + 31, legendWidth = width - margin * 2;
            for (int x = 0; x < legendWidth; x++)
            {
                // First entry uses the limiting positive colour. Zero itself is shown separately.
                Color c = EnvironmentView.ProbabilityColor(Mathf.Max(.000001f, (float)x / (legendWidth - 1))); c.a = 1;
                Rect(pixels, width, height, legendX + x, legendY, 1, 13, c);
            }
            Write(pixels, width, height, legendX, legendY + 19, "0%", Muted, 1);
            Write(pixels, width, height, legendX + legendWidth / 4 - 8, legendY + 19, "25%", Muted, 1);
            Write(pixels, width, height, legendX + legendWidth / 2 - 8, legendY + 19, "50%", Muted, 1);
            Write(pixels, width, height, legendX + legendWidth - 24, legendY + 19, "100%", Muted, 1);
            Write(pixels, width, height, margin, footerY + 73, "ZERO = GREY  |  EXITS = GREEN OUTLINE", Muted, 1);
            Write(pixels, width, height, margin, footerY + 88, "SYNTHETIC RESEARCH MODEL - NOT A SAFETY ASSESSMENT", Muted, 1);
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.name = final ? "FinalPositionDistribution" : "PositionDistributionAtTime";
            texture.filterMode = FilterMode.Point;
            texture.SetPixels32(pixels); texture.Apply(); return texture;
        }

        private static void Rect(Color32[] pixels, int width, int height, int x, int y, int w, int h, Color32 colour)
        {
            for (int yy = Math.Max(0, y); yy < Math.Min(height, y + h); yy++)
                for (int xx = Math.Max(0, x); xx < Math.Min(width, x + w); xx++)
                    pixels[(height - 1 - yy) * width + xx] = colour;
        }

        private static void Write(Color32[] pixels, int width, int height, int x, int y, string text, Color32 colour, int scale)
        {
            foreach (char raw in text.ToUpperInvariant())
            {
                string[] glyph;
                if (!Font.TryGetValue(raw, out glyph)) glyph = Font['?'];
                for (int row = 0; row < 7; row++)
                    for (int column = 0; column < 5; column++)
                        if (glyph[row][column] == '1') Rect(pixels, width, height, x + column * scale, y + row * scale, scale, scale, colour);
                x += 6 * scale;
            }
        }

        private static Dictionary<char, string[]> MakeFont()
        {
            var f = new Dictionary<char, string[]>();
            Add(f,' ',"00000/00000/00000/00000/00000/00000/00000");
            Add(f,'A',"01110/10001/10001/11111/10001/10001/10001");
            Add(f,'B',"11110/10001/10001/11110/10001/10001/11110");
            Add(f,'C',"01111/10000/10000/10000/10000/10000/01111");
            Add(f,'D',"11110/10001/10001/10001/10001/10001/11110");
            Add(f,'E',"11111/10000/10000/11110/10000/10000/11111");
            Add(f,'F',"11111/10000/10000/11110/10000/10000/10000");
            Add(f,'G',"01111/10000/10000/10111/10001/10001/01111");
            Add(f,'H',"10001/10001/10001/11111/10001/10001/10001");
            Add(f,'I',"11111/00100/00100/00100/00100/00100/11111");
            Add(f,'J',"00111/00010/00010/00010/00010/10010/01100");
            Add(f,'K',"10001/10010/10100/11000/10100/10010/10001");
            Add(f,'L',"10000/10000/10000/10000/10000/10000/11111");
            Add(f,'M',"10001/11011/10101/10101/10001/10001/10001");
            Add(f,'N',"10001/11001/10101/10011/10001/10001/10001");
            Add(f,'O',"01110/10001/10001/10001/10001/10001/01110");
            Add(f,'P',"11110/10001/10001/11110/10000/10000/10000");
            Add(f,'Q',"01110/10001/10001/10001/10101/10010/01101");
            Add(f,'R',"11110/10001/10001/11110/10100/10010/10001");
            Add(f,'S',"01111/10000/10000/01110/00001/00001/11110");
            Add(f,'T',"11111/00100/00100/00100/00100/00100/00100");
            Add(f,'U',"10001/10001/10001/10001/10001/10001/01110");
            Add(f,'V',"10001/10001/10001/10001/10001/01010/00100");
            Add(f,'W',"10001/10001/10001/10101/10101/10101/01010");
            Add(f,'X',"10001/10001/01010/00100/01010/10001/10001");
            Add(f,'Y',"10001/10001/01010/00100/00100/00100/00100");
            Add(f,'Z',"11111/00001/00010/00100/01000/10000/11111");
            Add(f,'0',"01110/10001/10011/10101/11001/10001/01110");
            Add(f,'1',"00100/01100/00100/00100/00100/00100/01110");
            Add(f,'2',"01110/10001/00001/00010/00100/01000/11111");
            Add(f,'3',"11110/00001/00001/01110/00001/00001/11110");
            Add(f,'4',"00010/00110/01010/10010/11111/00010/00010");
            Add(f,'5',"11111/10000/10000/11110/00001/00001/11110");
            Add(f,'6',"01110/10000/10000/11110/10001/10001/01110");
            Add(f,'7',"11111/00001/00010/00100/01000/01000/01000");
            Add(f,'8',"01110/10001/10001/01110/10001/10001/01110");
            Add(f,'9',"01110/10001/10001/01111/00001/00001/01110");
            Add(f,'.',"00000/00000/00000/00000/00000/00110/00110");
            Add(f,':',"00000/00110/00110/00000/00110/00110/00000");
            Add(f,'=',"00000/00000/11111/00000/11111/00000/00000");
            Add(f,'+',"00000/00100/00100/11111/00100/00100/00000");
            Add(f,'-',"00000/00000/00000/11111/00000/00000/00000");
            Add(f,'|',"00100/00100/00100/00100/00100/00100/00100");
            Add(f,'/',"00001/00001/00010/00100/01000/10000/10000");
            Add(f,'(',"00010/00100/01000/01000/01000/00100/00010");
            Add(f,')',"01000/00100/00010/00010/00010/00100/01000");
            Add(f,'%',"11001/11010/00100/01000/10110/00110/00000");
            Add(f,'?',"01110/10001/00001/00010/00100/00000/00100");
            return f;
        }
        private static void Add(Dictionary<char,string[]> font, char character, string rows) { font[character] = rows.Split('/'); }
    }
}

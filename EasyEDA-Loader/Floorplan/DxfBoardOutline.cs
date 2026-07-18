using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace EasyEDA_Loader.Floorplan
{
    /// <summary>
    /// Minimal ASCII DXF reader for board outlines (LWPOLYLINE / POLYLINE / LINE).
    /// Units assumed mm unless INSUNITS says otherwise; convert to mils.
    /// </summary>
    internal static class DxfBoardOutline
    {
        public static BoardOutlineSpec Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("DXF file not found.", path);

            var lines = File.ReadAllLines(path);
            var pairs = new List<KeyValuePair<int, string>>(lines.Length / 2);
            for (var i = 0; i + 1 < lines.Length; i += 2)
            {
                int code;
                if (!int.TryParse(lines[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
                    continue;
                pairs.Add(new KeyValuePair<int, string>(code, lines[i + 1].Trim()));
            }

            var unitScaleToMm = DetectUnitScaleToMm(pairs);
            var polylines = ExtractLwPolylines(pairs, unitScaleToMm);
            polylines.AddRange(ExtractPolylines(pairs, unitScaleToMm));
            polylines.AddRange(ExtractLinesAsSegments(pairs, unitScaleToMm));

            if (polylines.Count == 0)
                throw new InvalidOperationException(
                    "No LWPOLYLINE / POLYLINE / LINE geometry found in DXF. Export the board outline as a closed polyline.");

            // Prefer the largest closed polyline as outline.
            var outline = polylines
                .OrderByDescending(p => PolyAreaAbs(p))
                .ThenByDescending(p => p.Count)
                .First();

            if (outline.Count < 2)
                throw new InvalidOperationException("DXF outline has too few points.");

            double minX = outline.Min(p => p.X);
            double minY = outline.Min(p => p.Y);
            double maxX = outline.Max(p => p.X);
            double maxY = outline.Max(p => p.Y);

            // Normalize so lower-left is near origin with small margin later.
            var normalized = outline
                .Select(p => new BoardPoint(p.X - minX, p.Y - minY))
                .ToList();

            return new BoardOutlineSpec
            {
                Source = BoardOutlineSource.Dxf,
                WidthMils = Math.Max(50, maxX - minX),
                HeightMils = Math.Max(50, maxY - minY),
                PolygonMils = normalized,
                Label = Path.GetFileName(path),
            };
        }

        private static double DetectUnitScaleToMm(List<KeyValuePair<int, string>> pairs)
        {
            // INSUNITS: 1=in, 4=mm, 5=cm, 6=m. Default treat as mm.
            for (var i = 0; i < pairs.Count - 1; i++)
            {
                if (pairs[i].Key == 9 &&
                    string.Equals(pairs[i].Value, "$INSUNITS", StringComparison.OrdinalIgnoreCase) &&
                    pairs[i + 1].Key == 70)
                {
                    int u;
                    if (!int.TryParse(pairs[i + 1].Value, out u))
                        break;
                    switch (u)
                    {
                        case 1: return 25.4;          // inches → mm
                        case 4: return 1.0;           // mm
                        case 5: return 10.0;          // cm
                        case 6: return 1000.0;        // m
                        default: return 1.0;
                    }
                }
            }

            return 1.0;
        }

        private static double MmToMils(double mm) => mm / 0.0254;

        private static List<List<BoardPoint>> ExtractLwPolylines(
            List<KeyValuePair<int, string>> pairs,
            double unitScaleToMm)
        {
            var result = new List<List<BoardPoint>>();
            for (var i = 0; i < pairs.Count; i++)
            {
                if (pairs[i].Key != 0 ||
                    !string.Equals(pairs[i].Value, "LWPOLYLINE", StringComparison.OrdinalIgnoreCase))
                    continue;

                var pts = new List<BoardPoint>();
                double? pendingX = null;
                var closed = false;
                for (var j = i + 1; j < pairs.Count; j++)
                {
                    var code = pairs[j].Key;
                    var val = pairs[j].Value;
                    if (code == 0)
                        break;
                    if (code == 70)
                    {
                        int flags;
                        if (int.TryParse(val, out flags))
                            closed = (flags & 1) != 0;
                    }
                    else if (code == 10)
                    {
                        double x;
                        if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out x))
                            pendingX = MmToMils(x * unitScaleToMm);
                    }
                    else if (code == 20 && pendingX.HasValue)
                    {
                        double y;
                        if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                        {
                            pts.Add(new BoardPoint(pendingX.Value, MmToMils(y * unitScaleToMm)));
                            pendingX = null;
                        }
                    }
                }

                if (closed && pts.Count >= 3 &&
                    (Math.Abs(pts[0].X - pts[pts.Count - 1].X) > 0.5 ||
                     Math.Abs(pts[0].Y - pts[pts.Count - 1].Y) > 0.5))
                {
                    pts.Add(pts[0]);
                }

                if (pts.Count >= 2)
                    result.Add(pts);
            }

            return result;
        }

        private static List<List<BoardPoint>> ExtractPolylines(
            List<KeyValuePair<int, string>> pairs,
            double unitScaleToMm)
        {
            var result = new List<List<BoardPoint>>();
            for (var i = 0; i < pairs.Count; i++)
            {
                if (pairs[i].Key != 0 ||
                    !string.Equals(pairs[i].Value, "POLYLINE", StringComparison.OrdinalIgnoreCase))
                    continue;

                var pts = new List<BoardPoint>();
                var closed = false;
                for (var j = i + 1; j < pairs.Count; j++)
                {
                    if (pairs[j].Key == 0 &&
                        string.Equals(pairs[j].Value, "SEQEND", StringComparison.OrdinalIgnoreCase))
                        break;
                    if (pairs[j].Key == 70)
                    {
                        int flags;
                        if (int.TryParse(pairs[j].Value, out flags))
                            closed = (flags & 1) != 0;
                    }

                    if (pairs[j].Key == 0 &&
                        string.Equals(pairs[j].Value, "VERTEX", StringComparison.OrdinalIgnoreCase))
                    {
                        double? x = null, y = null;
                        for (var k = j + 1; k < pairs.Count; k++)
                        {
                            if (pairs[k].Key == 0)
                                break;
                            if (pairs[k].Key == 10)
                            {
                                double vx;
                                if (double.TryParse(pairs[k].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out vx))
                                    x = MmToMils(vx * unitScaleToMm);
                            }
                            else if (pairs[k].Key == 20)
                            {
                                double vy;
                                if (double.TryParse(pairs[k].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out vy))
                                    y = MmToMils(vy * unitScaleToMm);
                            }
                        }

                        if (x.HasValue && y.HasValue)
                            pts.Add(new BoardPoint(x.Value, y.Value));
                    }
                }

                if (closed && pts.Count >= 3)
                    pts.Add(pts[0]);
                if (pts.Count >= 2)
                    result.Add(pts);
            }

            return result;
        }

        private static List<List<BoardPoint>> ExtractLinesAsSegments(
            List<KeyValuePair<int, string>> pairs,
            double unitScaleToMm)
        {
            // Bundle all LINE entities into one polyline of endpoints (best-effort for simple outlines).
            var pts = new List<BoardPoint>();
            for (var i = 0; i < pairs.Count; i++)
            {
                if (pairs[i].Key != 0 ||
                    !string.Equals(pairs[i].Value, "LINE", StringComparison.OrdinalIgnoreCase))
                    continue;

                double? x1 = null, y1 = null, x2 = null, y2 = null;
                for (var j = i + 1; j < pairs.Count; j++)
                {
                    if (pairs[j].Key == 0)
                        break;
                    double v;
                    if (!double.TryParse(pairs[j].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                        continue;
                    v = MmToMils(v * unitScaleToMm);
                    switch (pairs[j].Key)
                    {
                        case 10: x1 = v; break;
                        case 20: y1 = v; break;
                        case 11: x2 = v; break;
                        case 21: y2 = v; break;
                    }
                }

                if (x1.HasValue && y1.HasValue && x2.HasValue && y2.HasValue)
                {
                    pts.Add(new BoardPoint(x1.Value, y1.Value));
                    pts.Add(new BoardPoint(x2.Value, y2.Value));
                }
            }

            if (pts.Count < 4)
                return new List<List<BoardPoint>>();
            return new List<List<BoardPoint>> { pts };
        }

        private static double PolyAreaAbs(List<BoardPoint> poly)
        {
            if (poly == null || poly.Count < 3)
                return 0;
            double a = 0;
            for (var i = 0; i < poly.Count - 1; i++)
                a += poly[i].X * poly[i + 1].Y - poly[i + 1].X * poly[i].Y;
            return Math.Abs(a) * 0.5;
        }
    }

    internal struct BoardPoint
    {
        public BoardPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    internal enum BoardOutlineSource
    {
        Auto,
        Manual,
        Dxf,
    }

    internal sealed class BoardOutlineSpec
    {
        public BoardOutlineSource Source { get; set; }
        public double WidthMils { get; set; }
        public double HeightMils { get; set; }
        public List<BoardPoint> PolygonMils { get; set; }
        public string Label { get; set; }

        public static BoardOutlineSpec FromRectangle(double widthMils, double heightMils, BoardOutlineSource source, string label)
        {
            widthMils = Math.Max(200, widthMils);
            heightMils = Math.Max(200, heightMils);
            return new BoardOutlineSpec
            {
                Source = source,
                WidthMils = widthMils,
                HeightMils = heightMils,
                Label = label,
                PolygonMils = new List<BoardPoint>
                {
                    new BoardPoint(0, 0),
                    new BoardPoint(widthMils, 0),
                    new BoardPoint(widthMils, heightMils),
                    new BoardPoint(0, heightMils),
                    new BoardPoint(0, 0),
                },
            };
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "{0}: {1:0.#} × {2:0.#} mil ({3:0.#} × {4:0.#} mm)",
                Label ?? Source.ToString(),
                WidthMils,
                HeightMils,
                WidthMils * 0.0254,
                HeightMils * 0.0254);
            return sb.ToString();
        }
    }
}

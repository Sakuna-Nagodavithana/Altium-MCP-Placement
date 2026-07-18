using EDP;
using SCH;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using static EasyEDA_Loader.EeSymbolShape;

namespace EasyEDA_Loader
{
    public class AltiumSymbolRectangle
    {
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }

        public double Width
        {
            get { return X2 - X1; }
        }
        public double Height
        {
            get { return Y2 - Y1; }
        }
    }

    public enum PinOrientation
    {
        Top = 0,
        Left,
        Right,
        Bottom
    }

    public class AltiumSymbolPin
    {
        static public TPinElectrical FromEEPinType(EasyedaPinType pinType)
        {
            switch (pinType)
            {
                case EasyedaPinType.Bidirectional:
                    return TPinElectrical.eElectricIO;
                case EasyedaPinType.Input:
                    return TPinElectrical.eElectricInput;
                case EasyedaPinType.Output:
                    return TPinElectrical.eElectricOutput;
                case EasyedaPinType.Power:
                    return TPinElectrical.eElectricPower;
                default:
                    return TPinElectrical.eElectricPassive;
            }
        }
        static public TRotationBy90 FromOrientation(PinOrientation orientation)
        {
            switch (orientation)
            {
                case PinOrientation.Top:
                    return TRotationBy90.eRotate90;
                case PinOrientation.Right:
                    return TRotationBy90.eRotate0;
                case PinOrientation.Left:
                    return TRotationBy90.eRotate180;
                case PinOrientation.Bottom:
                    return TRotationBy90.eRotate270;
                default:
                    return TRotationBy90.eRotate180;
            }
        }

        public double X { get; set; }
        public double Y { get; set; }
        public string Designator { get; set; }
        public string Name { get; set; }
        public TRotationBy90 Orientation { get; set; }
        public double Length { get; set; }
        public TPinElectrical PinType { get; set; }
        public bool ShowName { get; set; }
    }

    public class SymbolDrawing
    {
        static void DistributeEvenly<T>(List<T> source, List<List<T>> targets)
        {
            // Keep track of how many items are in each target list
            var counts = targets.Select(l => l.Count).ToList();

            foreach (var item in source)
            {
                // Find the index of the smallest list
                int minIndex = 0;
                int minCount = counts[0];

                for (int i = 1; i < counts.Count; i++)
                {
                    if (counts[i] < minCount)
                    {
                        minIndex = i;
                        minCount = counts[i];
                    }
                }

                // Add the item to the smallest list
                targets[minIndex].Add(item);
                counts[minIndex]++;
            }
        }


        static public void DrawAltiumRectangle(Canvas c, AltiumSymbolRectangle arect)
        {
            var rect = new Rectangle
            {
                Width = arect.X2 - arect.X1,
                Height = arect.Y2 - arect.Y1,
                Stroke = Brushes.Red,
                StrokeThickness = 2,
            };
            Canvas.SetLeft(rect, arect.X1);
            Canvas.SetTop(rect, arect.Y1);

            c.Children.Add(rect);
        }

        static public void DrawAltiumPin(Canvas c, AltiumSymbolPin pin, double lineLength)
        {
            double x2 = pin.X, y2 = pin.Y;

            double angle = 0;
            Point anchorPoint = new Point(0, 0.5);
            switch (pin.Orientation)
            {
                case TRotationBy90.eRotate90:
                    x2 = pin.X;
                    y2 = pin.Y - lineLength;
                    anchorPoint = new Point(1.0, 0.5);
                    angle = -90;
                    break;
                case TRotationBy90.eRotate180:
                    x2 = pin.X - lineLength;
                    y2 = pin.Y;
                    anchorPoint = new Point(0.0, 0.5);
                    angle = 0;
                    break;
                case TRotationBy90.eRotate0:
                    x2 = pin.X + lineLength;
                    y2 = pin.Y;
                    anchorPoint = new Point(0.0, 0.5);
                    angle = 0;
                    break;
                case TRotationBy90.eRotate270:
                    x2 = pin.X;
                    y2 = pin.Y + lineLength;
                    anchorPoint = new Point(1.0, 0.5);
                    angle = 90;
                    break;
                default:
                    break;
            }

            var line = new Line
            {
                X1 = pin.X,
                Y1 = pin.Y,
                X2 = x2,
                Y2 = y2,
                Stroke = Brushes.Red,
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };

            TextBlock label = new TextBlock
            {
                Text = pin.Designator,
                FontSize = 50,
                Foreground = Brushes.Red,
                RenderTransformOrigin = anchorPoint,
            };

            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double textWidth = label.DesiredSize.Width;
            double textHeight = label.DesiredSize.Height;

            double margin = 50; // distance in pixels
            if (pin.Orientation != TRotationBy90.eRotate180)
            {

                double angleRadians = angle * Math.PI / 180;

                // Offset text away from line start, along line direction
                double offsetX = -Math.Cos(angleRadians) * margin;
                double offsetY = -Math.Sin(angleRadians) * margin;

                Canvas.SetLeft(label, line.X1 - textWidth + offsetX);
                Canvas.SetTop(label, line.Y1 - textHeight / 2 + offsetY);
            }
            else
            {
                Canvas.SetLeft(label, line.X1 + margin);
                Canvas.SetTop(label, line.Y1 - textHeight / 2);
            }


            label.RenderTransform = new RotateTransform(angle);
            c.Children.Add(label);

            c.Children.Add(line);
        }

        static public (AltiumSymbolRectangle, List<AltiumSymbolPin>) LayoutPins(List<EeSymbolShape> Shapes, int widthMargin = 8, int heightMargin = 8, int gridSize = 100)
        {
            Shapes ??= new List<EeSymbolShape>();
            var pinShapes = Shapes.OfType<EeSymbolPin>()
                .Where(shape => shape?.Name != null && shape.Settings != null)
                .ToList();

            List<List<EeSymbolPin>> items = new()
            {
                // Top
                pinShapes.Where(shape => shape.Name.Rotation == 270 && shape.Name.TextAnchor == "end").OrderBy(s => s.Settings.PosX).ToList(),
                // Left
                pinShapes.Where(shape => shape.Name.Rotation == 0 && shape.Name.TextAnchor == "start").OrderBy(s => s.Settings.PosY).ToList(),
                // Right
                pinShapes.Where(shape => shape.Name.Rotation == 0 && shape.Name.TextAnchor == "end").OrderBy(s => s.Settings.PosY).ToList(),
                // Bottom
                pinShapes.Where(shape => shape.Name.Rotation == 270 && shape.Name.TextAnchor == "start").OrderBy(s => s.Settings.PosX).ToList()
            };

            // If there were uncategorized pins, put them somewhere
            var uncategorized = pinShapes.Except(items[0].Union(items[1]).Union(items[2]).Union(items[3])).ToList();

            var populated = items.Where(item => item.Count != 0).OrderBy(item => item.Count).ToList();
            if (populated.Count == 0) // Everything was uncategorized? Weird, add everything to the left
            {
                items[1].AddRange(uncategorized);
            }
            else if (populated.Count == 1) // If there's only one direction, just add everything to it
            {
                populated.FirstOrDefault().AddRange(uncategorized);
            }
            else // There are multiple available directions, distribute them starting with the least populated
            {
                DistributeEvenly(uncategorized, items);
            }

            // Select the largest of the two sides, these will determine the dimensions of the encompassing rect
            var widthPins = items[0].Count > items[3].Count ? items[0] : items[3];
            var heightPins = items[1].Count > items[2].Count ? items[1] : items[2];

            var halfWidthMargin = widthMargin / 2;
            var halfHeightMargin = heightMargin / 2;

            if (items[0].Count == 0 && items[3].Count == 0) // Only Left/Right
            {
                heightMargin = 0;
                halfHeightMargin = heightMargin / 2;
            }
            else if (items[1].Count == 0 && items[2].Count == 0) // Only Top/Bottom
            {
                widthMargin = 0;
                halfWidthMargin = widthMargin / 2;
            }

            var altiumRect = new AltiumSymbolRectangle
            {
                X1 = 0,
                Y1 = 0,
                X2 = (widthPins.Count + widthMargin) * gridSize + gridSize,
                Y2 = (heightPins.Count + heightMargin) * gridSize + gridSize,
            };

            List<(double x, double y)> offsets = new()
            {
                (halfWidthMargin * gridSize, 0),
                (0, halfHeightMargin * gridSize + gridSize),
                (altiumRect.Width, halfHeightMargin * gridSize + gridSize),
                (halfWidthMargin * gridSize, altiumRect.Height)
            };

            List<AltiumSymbolPin> pins = new();
            for (var i = 0; i < items.Count; ++i)
            {
                double offset_x = offsets[i].x, offset_y = offsets[i].y;
                for (var p = 0; p < items[i].Count; ++p)
                {
                    if (items[i][p]?.Settings == null || items[i][p].Name == null)
                        continue;

                    var x = offset_x;
                    var y = offset_y;
                    switch ((PinOrientation)i)
                    {
                        case PinOrientation.Top:
                            x += p * gridSize;
                            break;
                        case PinOrientation.Left:
                            y += p * gridSize;
                            break;
                        case PinOrientation.Right:
                            y += p * gridSize;
                            break;
                        case PinOrientation.Bottom:
                            x += p * gridSize;
                            break;
                        default:
                            break;
                    }

                    pins.Add(new AltiumSymbolPin
                    {
                        X = x,
                        Y = y,
                        Orientation = AltiumSymbolPin.FromOrientation((PinOrientation)i),
                        Designator = items[i][p].Settings.SpicePinNumber,
                        Name = items[i][p].Name.Text,
                        Length = 200,
                        ShowName = items[i][p].Name.IsDisplayed,
                        PinType = AltiumSymbolPin.FromEEPinType(items[i][p].Settings.Type)
                    });
                }
            }
            return (altiumRect, pins);
        }

        static public void DrawComponent(Canvas c, List<EeSymbolShape> Shapes)
        {
            if (Shapes != null)
            {
                c.Children.Clear();

                (var rect, var pins) = LayoutPins(Shapes);

                DrawAltiumRectangle(c, rect);
                foreach (var pin in pins)
                {
                    DrawAltiumPin(c, pin, 200);
                }

            }
        }

        static public void CreateComponent(ISch_Lib schLib, ISch_Component component, string pcbLibraryPath, string package, SymbolData ee_symbol)
        {
            (var rect, var pins) = SymbolDrawing.LayoutPins(ee_symbol.Shapes);

            // EasyEDA sends the designator prefix as "R?", "C?", etc. -- strip non-letters.
            var rawPrefix = (ee_symbol?.Head?.Parameters?.Pre ?? "").Trim();
            var prefix = new string(rawPrefix.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
            bool drewStandard = false;

            // 2-pin passives: force a clear left/right geometry so the body is always
            // readable (LayoutPins often packs pins too close for a tiny EasyEDA body).
            if (pins.Count == 2 && IsPassivePrefix(prefix))
            {
                // Pin hotspots ~200 mil apart (standard Altium passive spacing).
                // Leads + body should dominate over designator/comment text.
                const double bodySpan = 200.0;
                const double midY = 100.0;
                const double pinLen = 100.0;
                var left = pins[0];
                var right = pins[1];
                left.X = 0;
                left.Y = midY;
                left.Orientation = AltiumSymbolPin.FromOrientation(PinOrientation.Left);
                left.Length = pinLen;
                left.ShowName = false;
                right.X = bodySpan;
                right.Y = midY;
                right.Orientation = AltiumSymbolPin.FromOrientation(PinOrientation.Right);
                right.Length = pinLen;
                right.ShowName = false;
                drewStandard = DrawStandardPassive(schLib, component, left, right, prefix, midY * 2);
                pins = new List<AltiumSymbolPin> { left, right };
                foreach (var pin in pins)
                {
                    EESCH.CreatePin(
                        schLib,
                        component,
                        pin.X,
                        midY * 2 - pin.Y,
                        pin.Designator,
                        pin.Name,
                        pin.Orientation,
                        pin.Length,
                        pin.PinType,
                        pin.ShowName,
                        null);
                }
                EESCH.AssignFootprint(component, pcbLibraryPath, package, "");
                schLib.AddSchComponent(component);
                return;
            }

            if (!drewStandard)
            {
                // EasyEDA body art lives in a different coordinate space than LayoutPins.
                // Using it for multi-pin ICs produces offset/wrong graphics. Keep a clean
                // bounding box for ICs; only try EasyEDA shapes for unknown 2-pin parts.
                if (pins.Count == 2)
                {
                    bool drewAnyShape = DrawEasyEdaShapes(schLib, component, ee_symbol.Shapes, rect.Height);
                    if (!drewAnyShape)
                        EESCH.CreateRectangle(schLib, component, rect.X1, rect.Height - rect.Y1, rect.X2, rect.Height - rect.Y2);
                }
                else
                {
                    EESCH.CreateRectangle(schLib, component, rect.X1, rect.Height - rect.Y1, rect.X2, rect.Height - rect.Y2);
                }
            }

            foreach (var pin in pins)
            {
                EESCH.CreatePin(schLib, component, pin.X, rect.Height - pin.Y, pin.Designator, pin.Name, pin.Orientation, pin.Length, pin.PinType, pin.ShowName, null);
            }
            EESCH.AssignFootprint(component, pcbLibraryPath, package, "");
            schLib.AddSchComponent(component);
        }

        static bool IsPassivePrefix(string prefix) =>
            prefix == "R" || prefix == "C" || prefix == "CP" || prefix == "CPOL" ||
            prefix == "L" || prefix == "D" || prefix == "LED" || prefix == "TVS" ||
            prefix == "FB" || prefix == "BEAD" || prefix == "FERRITE" || prefix == "F";

        /// <summary>
        /// Draw a standard schematic symbol for a 2-pin passive between its two pin
        /// positions. Pin coordinates are in the LayoutPins grid space; symHeight is
        /// the mirror axis used for the y-flip (same as pin placement).
        /// </summary>
        static bool DrawStandardPassive(ISch_Lib schLib, ISch_Component component,
            AltiumSymbolPin p1, AltiumSymbolPin p2, string prefix, double symHeight)
        {
            // Altium coords (y mirrored about symHeight, same as EESCH.CreatePin call below).
            double x1 = p1.X, y1 = symHeight - p1.Y;
            double x2 = p2.X, y2 = symHeight - p2.Y;
            double mx = (x1 + x2) / 2.0, my = (y1 + y2) / 2.0;
            bool horizontal = System.Math.Abs(x2 - x1) >= System.Math.Abs(y2 - y1);

            switch (prefix)
            {
                case "C":
                case "CP":
                case "CPOL":
                    DrawCapacitorPlates(schLib, component, x1, y1, x2, y2, mx, my, horizontal, prefix != "C");
                    return true;
                case "D":
                case "LED":
                case "TVS":
                    DrawDiodeSymbol(schLib, component, x1, y1, x2, y2, mx, my, horizontal);
                    return true;
                case "L":
                    DrawInductorLoops(schLib, component, x1, y1, x2, y2, mx, my, horizontal);
                    return true;
                case "R":
                case "FB":
                case "BEAD":
                case "FERRITE":
                case "F":
                default:
                    DrawResistorBox(schLib, component, x1, y1, x2, y2, mx, my, horizontal);
                    return true;
            }
        }

        static void DrawCapacitorPlates(ISch_Lib schLib, ISch_Component component,
            double x1, double y1, double x2, double y2, double mx, double my, bool horizontal, bool polarized)
        {
            // Classic schematic capacitor: two parallel plates drawn as thick lines.
            // Previous 4-mil filled rectangles looked like a speck next to the leads.
            const double gap = 28;          // spacing between plates
            const double plateHalf = 70;    // half-length of each plate (140 mil total)
            const double plateSpread = 4;   // draw 3 parallel strokes for visual weight

            if (horizontal)
            {
                // Left plate (vertical strokes)
                for (int i = -1; i <= 1; i++)
                {
                    double px = mx - gap / 2 + i * plateSpread;
                    EESCH.CreateLine(schLib, component, px, my - plateHalf, px, my + plateHalf, thick: true);
                }
                // Right plate
                for (int i = -1; i <= 1; i++)
                {
                    double px = mx + gap / 2 + i * plateSpread;
                    EESCH.CreateLine(schLib, component, px, my - plateHalf, px, my + plateHalf, thick: true);
                }
                // Leads stop at the outer face of each plate stack
                EESCH.CreateLine(schLib, component, x1, y1, mx - gap / 2 - plateSpread, my, thick: false);
                EESCH.CreateLine(schLib, component, x2, y2, mx + gap / 2 + plateSpread, my, thick: false);
                if (polarized)
                {
                    EESCH.CreateLine(schLib, component, mx - gap / 2 - 18, my + plateHalf + 12, mx - gap / 2 + 8, my + plateHalf + 12, thick: true);
                    EESCH.CreateLine(schLib, component, mx - gap / 2 - 5, my + plateHalf + 4, mx - gap / 2 - 5, my + plateHalf + 20, thick: true);
                }
            }
            else
            {
                for (int i = -1; i <= 1; i++)
                {
                    double py = my - gap / 2 + i * plateSpread;
                    EESCH.CreateLine(schLib, component, mx - plateHalf, py, mx + plateHalf, py, thick: true);
                }
                for (int i = -1; i <= 1; i++)
                {
                    double py = my + gap / 2 + i * plateSpread;
                    EESCH.CreateLine(schLib, component, mx - plateHalf, py, mx + plateHalf, py, thick: true);
                }
                EESCH.CreateLine(schLib, component, x1, y1, mx, my - gap / 2 - plateSpread, thick: false);
                EESCH.CreateLine(schLib, component, x2, y2, mx, my + gap / 2 + plateSpread, thick: false);
            }
        }

        static void DrawResistorBox(ISch_Lib schLib, ISch_Component component,
            double x1, double y1, double x2, double y2, double mx, double my, bool horizontal)
        {
            // IEEE rectangle sized to match a normal Altium passive (~standard R).
            const double boxW = 100, boxH = 36;

            if (horizontal)
            {
                EESCH.CreateRectangle(schLib, component, mx - boxW / 2, my - boxH / 2, mx + boxW / 2, my + boxH / 2);
                EESCH.CreateLine(schLib, component, x1, y1, mx - boxW / 2, my);
                EESCH.CreateLine(schLib, component, x2, y2, mx + boxW / 2, my);
            }
            else
            {
                EESCH.CreateRectangle(schLib, component, mx - boxH / 2, my - boxW / 2, mx + boxH / 2, my + boxW / 2);
                EESCH.CreateLine(schLib, component, x1, y1, mx, my - boxW / 2);
                EESCH.CreateLine(schLib, component, x2, y2, mx, my + boxW / 2);
            }
        }

        static void DrawDiodeSymbol(ISch_Lib schLib, ISch_Component component,
            double x1, double y1, double x2, double y2, double mx, double my, bool horizontal)
        {
            const double tri = 20; // triangle half-size

            if (horizontal)
            {
                double dir = x2 > x1 ? 1 : -1;
                // Triangle: base on the p1 side, apex toward p2.
                EESCH.CreateLine(schLib, component, mx - dir * tri / 2, my - tri / 2, mx - dir * tri / 2, my + tri / 2);
                EESCH.CreateLine(schLib, component, mx - dir * tri / 2, my - tri / 2, mx + dir * tri / 2, my);
                EESCH.CreateLine(schLib, component, mx - dir * tri / 2, my + tri / 2, mx + dir * tri / 2, my);
                // Cathode bar on the p2 side.
                EESCH.CreateLine(schLib, component, mx + dir * tri / 2, my - tri / 2, mx + dir * tri / 2, my + tri / 2);
                // Leads.
                EESCH.CreateLine(schLib, component, x1, y1, mx - dir * tri / 2, my);
                EESCH.CreateLine(schLib, component, x2, y2, mx + dir * tri / 2, my);
            }
            else
            {
                double dir = y2 > y1 ? 1 : -1;
                EESCH.CreateLine(schLib, component, mx - tri / 2, my - dir * tri / 2, mx + tri / 2, my - dir * tri / 2);
                EESCH.CreateLine(schLib, component, mx - tri / 2, my - dir * tri / 2, mx, my + dir * tri / 2);
                EESCH.CreateLine(schLib, component, mx + tri / 2, my - dir * tri / 2, mx, my + dir * tri / 2);
                EESCH.CreateLine(schLib, component, mx - tri / 2, my + dir * tri / 2, mx + tri / 2, my + dir * tri / 2);
                EESCH.CreateLine(schLib, component, x1, y1, mx, my - dir * tri / 2);
                EESCH.CreateLine(schLib, component, x2, y2, mx, my + dir * tri / 2);
            }
        }

        static void DrawInductorLoops(ISch_Lib schLib, ISch_Component component,
            double x1, double y1, double x2, double y2, double mx, double my, bool horizontal)
        {
            // Four small arcs (loops) along the signal path. Altium schematic arcs are
            // center+radius+start/end angle; we draw four half-circles to form the
            // classic inductor "bumps".
            const double r = 8;
            const int loops = 4;
            double span = horizontal ? System.Math.Abs(x2 - x1) : System.Math.Abs(y2 - y1);
            double start = horizontal ? System.Math.Min(x1, x2) : System.Math.Min(y1, y2);
            double loopSpan = System.Math.Min(span / 2.0, loops * r * 2);
            double step = loopSpan / loops;

            if (horizontal)
            {
                double leadEnd = mx - loopSpan / 2;
                EESCH.CreateLine(schLib, component, x1, y1, leadEnd, my);
                for (int i = 0; i < loops; i++)
                {
                    double cx = leadEnd + i * step + step / 2;
                    // Upper half-circle bump: 180 to 360 (top half).
                    EESCH.CreateArc(schLib, component, cx, my, r, 180, 360);
                }
                EESCH.CreateLine(schLib, component, leadEnd + loopSpan, my, x2, y2);
            }
            else
            {
                double leadEnd = my - loopSpan / 2;
                EESCH.CreateLine(schLib, component, x1, y1, mx, leadEnd);
                for (int i = 0; i < loops; i++)
                {
                    double cy = leadEnd + i * step + step / 2;
                    // Right half-circle bump: 90 to 270... use 270 to 90 going through 0.
                    EESCH.CreateArc(schLib, component, mx, cy, r, 270, 90);
                }
                EESCH.CreateLine(schLib, component, mx, leadEnd + loopSpan, x2, y2);
            }
        }

        // Draw every non-pin EasyEDA shape as its native Altium schematic primitive.
        // EasyEDA y-axis points down; Altium schematic y-axis points up -- we mirror
        // every shape about the symbol height, exactly like the pin placement does.
        // Returns true if at least one body shape was emitted.
        static bool DrawEasyEdaShapes(ISch_Lib schLib, ISch_Component component, List<EeSymbolShape> shapes, double symHeight)
        {
            if (shapes == null) return false;
            bool emitted = false;

            foreach (var shape in shapes)
            {
                // Pins are placed separately by the caller; skip them here.
                if (shape is EeSymbolPin) continue;

                switch (shape)
                {
                    case EeSymbolRectangle r:
                        EmitRectangle(schLib, component, r, symHeight);
                        emitted = true;
                        break;
                    case EeSymbolEllipse e:
                        EmitEllipse(schLib, component, e, symHeight);
                        emitted = true;
                        break;
                    case EeSymbolCircle c:
                        EmitCircle(schLib, component, c, symHeight);
                        emitted = true;
                        break;
                    case EeSymbolPolyline pl: // also covers EeSymbolPolygon (subclass)
                        EmitPolyline(schLib, component, pl, symHeight, shape is EeSymbolPolygon);
                        emitted = true;
                        break;
                    case EeSymbolArc a:
                        EmitArc(schLib, component, a, symHeight);
                        emitted = true;
                        break;
                    case EeSymbolPath pt when !string.IsNullOrWhiteSpace(pt.Paths):
                        EmitPath(schLib, component, pt, symHeight);
                        emitted = true;
                        break;
                }
            }

            return emitted;
        }

        static void EmitRectangle(ISch_Lib schLib, ISch_Component component, EeSymbolRectangle r, double symHeight)
        {
            // EasyEDA rect: PosX/PosY is the top-left corner, Width/Height extend +x/+y.
            // Both shapes drawn here are body graphics, so re-use CreateRectangle -- it
            // draws a hollow outline which matches schematic-body rectangles.
            double x1 = r.PosX;
            double y1 = r.PosY;
            double x2 = r.PosX + r.Width;
            double y2 = r.PosY + r.Height;
            EESCH.CreateRectangle(schLib, component, x1, symHeight - y1, x2, symHeight - y2);
        }

        static void EmitEllipse(ISch_Lib schLib, ISch_Component component, EeSymbolEllipse e, double symHeight)
        {
            EESCH.CreateEllipse(schLib, component, e.CenterX, symHeight - e.CenterY, e.RadiusX, e.RadiusY);
        }

        static void EmitCircle(ISch_Lib schLib, ISch_Component component, EeSymbolCircle c, double symHeight)
        {
            // Treat EasyEDA circle as an ellipse with equal radii (its native Altium type).
            EESCH.CreateEllipse(schLib, component, c.CenterX, symHeight - c.CenterY, c.Radius, c.Radius);
        }

        // EasyEDA polyline Points format is the literal SVG points-list: pairs of
        // "x,y" separated by spaces (e.g. "10,0 10,20 -10,20 -10,0"). Emit each
        // consecutive pair as an Altium schematic line.
        static void EmitPolyline(ISch_Lib schLib, ISch_Component component, EeSymbolPolyline pl, double symHeight, bool closed)
        {
            var pts = ParsePointList(pl.Points);
            if (pts.Count < 2) return;
            for (int i = 0; i < pts.Count - 1; i++)
                EESCH.CreateLine(schLib, component, pts[i].X, symHeight - pts[i].Y, pts[i + 1].X, symHeight - pts[i + 1].Y);
            if (closed && pts.Count > 2)
                EESCH.CreateLine(schLib, component, pts[pts.Count - 1].X, symHeight - pts[pts.Count - 1].Y, pts[0].X, symHeight - pts[0].Y);
        }

        static void EmitArc(ISch_Lib schLib, ISch_Component component, EeSymbolArc a, double symHeight)
        {
            // The EasyEDA arc Body is an SVG path string. We accept the common case
            // "M sx sy A rx ry xDeg large sweep ex ey" and emit a single Altium arc.
            // Anything more complex falls through to the path handler below.
            var segs = ParseSvgPath(a.Path);
            if (segs == null || segs.Count == 0) return;

            // Back off to the path-line approximation unless this is exactly one
            // move-then-arc segment (matches capacitor curves / inductor loops).
            if (segs.Count >= 2 && segs[segs.Count - 1].Kind == SvgSegKind.Arc)
            {
                var start = segs[segs.Count - 2].End;
                var arcSeg = segs[segs.Count - 1];
                double radius = (arcSeg.RadiusX + arcSeg.RadiusY) / 2.0;
                if (radius <= 0) radius = 1.0;
                // Altium start/end angles are CW degrees measured from +x; the SVG
                // arc helper already returns this convention.
                var arc = EasyEDA_Loader.SvgArcUtils.ComputeArc(
                    start.X, start.Y,
                    arcSeg.RadiusX, arcSeg.RadiusY, arcSeg.XAxisRotation,
                    arcSeg.LargeArc, arcSeg.Sweep,
                    arcSeg.End.X, arcSeg.End.Y);
                EESCH.CreateArc(schLib, component, arc.X, symHeight - arc.Y, arc.Radius, arc.StartAngle, arc.EndAngle);
            }
            else
            {
                EmitParsedPathAsLines(schLib, component, segs, symHeight);
            }
        }

        static void EmitPath(ISch_Lib schLib, ISch_Component component, EeSymbolPath pt, double symHeight)
        {
            var segs = ParseSvgPath(pt.Paths);
            if (segs == null || segs.Count == 0) return;
            EmitParsedPathAsLines(schLib, component, segs, symHeight);
        }

        // Decompose an arbitrary parsed SVG path into Altium schematic lines.
        // Arc subpaths are flattened to short segments; this keeps cap/resistor
        // bodies (lines) and inductor loops (small arcs) correct enough for a
        // schematic symbol, with no dependency on the Altium Bezier interface.
        static void EmitParsedPathAsLines(ISch_Lib schLib, ISch_Component component, List<SvgPathSeg> segs, double symHeight)
        {
            if (segs.Count < 2) return;
            for (int i = 1; i < segs.Count; i++)
            {
                var prev = segs[i - 1].End;
                var cur = segs[i];
                if (cur.Kind == SvgSegKind.Arc)
                {
                    int steps = 12;
                    for (int s = 0; s <= steps; s++)
                    {
                        double t0 = (s - 1) / (double)steps;
                        double t1 = s / (double)steps;
                        if (s == 0) continue;
                        var p0 = PointOnArc(segs[i - 1].End, cur, t0);
                        var p1 = PointOnArc(segs[i - 1].End, cur, t1);
                        EESCH.CreateLine(schLib, component, p0.X, symHeight - p0.Y, p1.X, symHeight - p1.Y);
                    }
                }
                else
                {
                    EESCH.CreateLine(schLib, component, prev.X, symHeight - prev.Y, cur.End.X, symHeight - cur.End.Y);
                }
            }
        }

        // --- minimal SVG path + points-list parsers ---

        static List<(double X, double Y)> ParsePointList(string raw)
        {
            var result = new List<(double X, double Y)>();
            if (string.IsNullOrWhiteSpace(raw)) return result;

            // Split on whitespace and commas, then read numbers two at a time.
            // The EasyEDA "x,y x,y ..." form is the most common; some symbols use
            // "x y x y" too -- handle both by tokenising on any of " ,\t\r\n".
            var tokens = raw.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < tokens.Length; i += 2)
            {
                if (double.TryParse(tokens[i], out double x) &&
                    double.TryParse(tokens[i + 1], out double y))
                    result.Add((x, y));
            }
            return result;
        }

        enum SvgSegKind { Move, Line, Arc }

        class SvgPathSeg
        {
            public SvgSegKind Kind;
            public (double X, double Y) End;
            public double RadiusX, RadiusY, XAxisRotation;
            public bool LargeArc, Sweep;
        }

        // Parse a tiny subset of SVG path syntax: M/m, L/l, H/h, V/v, A/a, Z/z.
        // Relative commands are resolved against the previous endpoint. This is
        // sufficient for EasyEDA schematic symbols, which only use these tokens.
        static List<SvgPathSeg> ParseSvgPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var segs = new List<SvgPathSeg>();
            double cx = 0, cy = 0;
            double sx = 0, sy = 0; // subpath start (for Z)
            int i = 0;
            char cmd = '\0';

            void MoveTo(double x, double y) { cx = x; cy = y; sx = x; sy = y; }
            void LineTo(double x, double y)
            {
                segs.Add(new SvgPathSeg { Kind = SvgSegKind.Line, End = (x, y) });
                cx = x; cy = y;
            }
            void ArcTo(double rx, double ry, double rot, bool large, bool sweep, double x, double y)
            {
                segs.Add(new SvgPathSeg { Kind = SvgSegKind.Arc, End = (x, y), RadiusX = rx, RadiusY = ry, XAxisRotation = rot, LargeArc = large, Sweep = sweep });
                cx = x; cy = y;
            }

            while (i < raw.Length)
            {
                char ch = raw[i];
                if (char.IsWhiteSpace(ch) || ch == ',') { i++; continue; }

                // A number (no command yet) means implicit repetition of the last command.
                if (IsNumberStart(ch))
                {
                    if (cmd == '\0') return null;
                }
                else
                {
                    cmd = ch;
                    i++;
                    if (i < raw.Length && (raw[i] == ' ' || raw[i] == ',')) i++;
                }

                bool rel = char.IsLower(cmd);
                char c = char.ToUpperInvariant(cmd);

                switch (c)
                {
                    case 'M':
                    {
                        if (!ReadNumber(raw, ref i, out double x)) return segs;
                        if (!ReadNumber(raw, ref i, out double y)) return segs;
                        if (rel) { x += cx; y += cy; }
                        // Subsequent implicit M coords are treated as L by SVG.
                        MoveTo(x, y);
                        segs.Add(new SvgPathSeg { Kind = SvgSegKind.Move, End = (x, y) });
                        cmd = rel ? 'l' : 'L';
                        break;
                    }
                    case 'L':
                    {
                        if (!ReadNumber(raw, ref i, out double x)) return segs;
                        if (!ReadNumber(raw, ref i, out double y)) return segs;
                        if (rel) { x += cx; y += cy; }
                        LineTo(x, y);
                        break;
                    }
                    case 'H':
                    {
                        if (!ReadNumber(raw, ref i, out double x)) return segs;
                        if (rel) x += cx;
                        LineTo(x, cy);
                        break;
                    }
                    case 'V':
                    {
                        if (!ReadNumber(raw, ref i, out double y)) return segs;
                        if (rel) y += cy;
                        LineTo(cx, y);
                        break;
                    }
                    case 'A':
                    {
                        if (!ReadNumber(raw, ref i, out double rx)) return segs;
                        if (!ReadNumber(raw, ref i, out double ry)) return segs;
                        if (!ReadNumber(raw, ref i, out double rot)) return segs;
                        if (!ReadFlag(raw, ref i, out bool large)) return segs;
                        if (!ReadFlag(raw, ref i, out bool sweep)) return segs;
                        if (!ReadNumber(raw, ref i, out double x)) return segs;
                        if (!ReadNumber(raw, ref i, out double y)) return segs;
                        if (rel) { x += cx; y += cy; }
                        ArcTo(rx, ry, rot, large, sweep, x, y);
                        break;
                    }
                    case 'Z':
                        LineTo(sx, sy);
                        break;
                    default:
                        // Unknown command -- give up cleanly on what we have.
                        return segs;
                }
            }
            return segs;
        }

        static bool IsNumberStart(char ch) => (ch >= '0' && ch <= '9') || ch == '.' || ch == '-' || ch == '+';

        static bool ReadNumber(string s, ref int i, out double val)
        {
            val = 0;
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
            int start = i;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
            bool sawDot = false;
            while (i < s.Length)
            {
                char ch = s[i];
                if (ch >= '0' && ch <= '9') { i++; continue; }
                if (ch == '.' && !sawDot) { sawDot = true; i++; continue; }
                if ((ch == 'e' || ch == 'E') && i + 1 < s.Length && (s[i + 1] == '+' || s[i + 1] == '-' || (s[i + 1] >= '0' && s[i + 1] <= '9')))
                { i += 2; continue; }
                break;
            }
            if (i == start) return false;
            return double.TryParse(s.Substring(start, i - start), out val);
        }

        static bool ReadFlag(string s, ref int i, out bool val)
        {
            val = false;
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
            if (i >= s.Length) return false;
            char ch = s[i++];
            if (ch == '1') { val = true; return true; }
            if (ch == '0') { val = false; return true; }
            return false;
        }

        // Sample a point along an SVG arc at parameter t in [0,1], flattening it
        // through SvgArcUtils.ComputeArc. Good enough for symbol-grade rendering.
        static (double X, double Y) PointOnArc((double X, double Y) start, SvgPathSeg arcSeg, double t)
        {
            var arc = EasyEDA_Loader.SvgArcUtils.ComputeArc(
                start.X, start.Y,
                arcSeg.RadiusX, arcSeg.RadiusY, arcSeg.XAxisRotation,
                arcSeg.LargeArc, arcSeg.Sweep,
                arcSeg.End.X, arcSeg.End.Y);
            double angle = arc.StartAngle + (arc.EndAngle - arc.StartAngle) * t;
            double rad = angle * System.Math.PI / 180.0;
            return (arc.X + arc.Radius * System.Math.Cos(rad), arc.Y + arc.Radius * System.Math.Sin(rad));
        }
    }
}

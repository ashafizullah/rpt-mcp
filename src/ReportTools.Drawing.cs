using System;
using System.Drawing;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using E = CrystalDecisions.CrystalReports.Engine;
using RD = CrystalDecisions.ReportAppServer.ReportDefModel;

namespace RptMcp
{
    /// <summary>Lines, boxes and pictures.</summary>
    internal static partial class ReportTools
    {
        private static JToken AddLine(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var section = ras.Section((string)a["section"]);
            int x1 = (int)a["x1"], y1 = (int)a["y1"], x2 = (int)a["x2"], y2 = (int)a["y2"];
            if (x1 != x2 && y1 != y2) throw new ToolError("Crystal lines are horizontal or vertical: x1 must equal x2, or y1 must equal y2.");

            var endSection = string.IsNullOrWhiteSpace((string)a["end_section"]) ? section.Name : ras.Section((string)a["end_section"]).Name;
            bool spans = !Eq(endSection, section.Name);
            // Within one section the points can be given in any order. When the line runs into a later section,
            // y1 is in the start section and y2 in the end section, so they must not be swapped.
            var line = new RD.LineObject
            {
                Left = Math.Min(x1, x2), Right = Math.Max(x1, x2),
                Top = spans ? y1 : Math.Min(y1, y2), Bottom = spans ? y2 : Math.Max(y1, y2),
                SectionName = section.Name,
                EndSectionName = endSection,
                LineStyle = RasLineStyle((string)a["line_style"] ?? "single"),
                LineThickness = (int?)a["line_thickness"] ?? 20,
                LineColor = ColorRef((string)a["line_color"] ?? "#000000"),
            };
            if (!string.IsNullOrWhiteSpace((string)a["name"])) line.Name = (string)a["name"];
            return AddObject(ras, line, section);
        });

        private static JToken AddBox(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var section = ras.Section((string)a["section"]);
            var box = new RD.BoxObject
            {
                Left = (int)a["left"], Top = (int)a["top"], Right = (int)a["right"], Bottom = (int)a["bottom"],
                SectionName = section.Name,
                EndSectionName = string.IsNullOrWhiteSpace((string)a["end_section"]) ? section.Name : ras.Section((string)a["end_section"]).Name,
                LineStyle = RasLineStyle((string)a["line_style"] ?? "single"),
                LineThickness = (int?)a["line_thickness"] ?? 20,
                LineColor = ColorRef((string)a["line_color"] ?? "#000000"),
            };
            if (box.Right <= box.Left || box.Bottom <= box.Top) throw new ToolError("right/bottom must be greater than left/top.");
            if (!string.IsNullOrWhiteSpace((string)a["fill_color"])) box.FillColor = ColorRef((string)a["fill_color"]);
            if (a["corner_radius"] != null) box.CornerEllipseWidth = box.CornerEllipseHeight = (int)a["corner_radius"] * 2;
            if (!string.IsNullOrWhiteSpace((string)a["name"])) box.Name = (string)a["name"];
            return AddObject(ras, box, section);
        });

        private static JToken AddPicture(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var section = ras.Section((string)a["section"]);
            var image = ImagePath((string)a["image_path"]);
            var name = ImportPicture(ras, image, section, (int)a["left"], (int)a["top"], (int?)a["width"], (int?)a["height"], (string)a["name"]);
            return new JObject { ["object"] = name, ["section"] = section.Name, ["action"] = "added", ["box"] = Box(ras.Object(name)) };
        });

        private static JToken ReplacePicture(JObject a) => Edit(a, main =>
        {
            var ras = Ras.For(main, Sub(a));
            var old = ras.Object((string)a["object"]);
            if (!(old is RD.PictureObject)) throw new ToolError($"'{old.Name}' is a {old.Kind}, not a picture.");
            var image = ImagePath((string)a["image_path"]);
            var section = ras.Section(old.SectionName);
            int left = old.Left, top = old.Top, width = old.Width, height = old.Height;
            var name = old.Name;
            bool keepSize = (bool?)a["keep_size"] ?? true;

            ras.ReportDef.ReportObjectController.Remove(old);
            var added = ImportPicture(ras, image, section, left, top, keepSize ? width : (int?)null, keepSize ? height : (int?)null, name);
            return new JObject { ["object"] = added, ["section"] = section.Name, ["action"] = "replaced", ["box"] = Box(ras.Object(added)) };
        });

        /// <summary>
        /// Adds a picture object built from the image bytes. (ImportPicture cannot set a name, RAS refuses renames
        /// through Modify, and a cloned picture loses its data, so the object is constructed directly.)
        /// </summary>
        private static string ImportPicture(Ras ras, string image, RD.Section section, int left, int top, int? width, int? height, string name)
        {
            int naturalW, naturalH;
            using (var img = Image.FromFile(image))
            {
                naturalW = (int)Math.Round(img.Width * 1440.0 / (img.HorizontalResolution > 1 ? img.HorizontalResolution : 96));
                naturalH = (int)Math.Round(img.Height * 1440.0 / (img.VerticalResolution > 1 ? img.VerticalResolution : 96));
            }
            int w = width ?? (height != null ? (int)Math.Round(naturalW * (double)height.Value / naturalH) : naturalW);
            int h = height ?? (width != null ? (int)Math.Round(naturalH * (double)width.Value / naturalW) : naturalH);

            RD.PictureObjectClass Build(byte[] bytes)
            {
                var data = new CrystalDecisions.ReportAppServer.CommonObjectModel.ByteArray { ByteArray = bytes };
                var pic = new RD.PictureObjectClass
                {
                    PictureData = data, Left = left, Top = top, Width = w, Height = h,
                    OriginalWidth = naturalW, OriginalHeight = naturalH, SectionName = section.Name
                };
                if (!string.IsNullOrWhiteSpace(name)) pic.Name = name;
                return pic;
            }

            // PictureData must be a DIB (a .bmp without its 14-byte file header): PNG/JPEG or full .bmp bytes
            // are accepted by Add but make the report fail to save ("Invalid text or object handle").
            using (var img = Image.FromFile(image))
            using (var bmp = new Bitmap(img.Width, img.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            using (var ms = new MemoryStream())
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.White); // flatten transparency
                    g.DrawImage(img, 0, 0, img.Width, img.Height);
                }
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                const int BitmapFileHeader = 14;
                return (string)AddObject(ras, Build(ms.ToArray().Skip(BitmapFileHeader).ToArray()), section)["object"];
            }
        }

        private static string ImagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ToolError("image_path is required.");
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            if (!File.Exists(full)) throw new ToolError("Image not found: " + full);
            var ext = Path.GetExtension(full).ToLowerInvariant();
            if (!new[] { ".bmp", ".jpg", ".jpeg", ".png", ".gif", ".tif", ".tiff" }.Contains(ext))
                throw new ToolError("Supported images: bmp, jpg, png, gif, tif.");
            return full;
        }

        private const string DoubleLineError =
            "The Crystal runtime rejects the double line style (for lines and boxes). Use single, dashed or dotted, or draw two parallel lines.";

        private static string Box(RD.ISCRReportObject o) => $"{o.Left},{o.Top} {o.Width}x{o.Height}";

        // ---------- engine-side line/box properties (used by set_object_props) ----------

        private static void ApplyDrawingProps(E.ReportObject obj, JObject a, JArray done)
        {
            if (!Has(a, "right", "bottom", "line_style", "line_thickness", "line_color", "fill_color", "extend_to_bottom")) return;
            if (!(obj is E.DrawingObject d))
                throw new ToolError($"right/bottom/line_*/fill_color only apply to lines and boxes ('{obj.Name}' is {obj.Kind}).");

            if (a["right"] != null) { d.Right = (int)a["right"]; done.Add("right"); }
            if (a["bottom"] != null) { d.Bottom = (int)a["bottom"]; done.Add("bottom"); }
            if (a["line_style"] != null)
            {
                d.LineStyle = EngineLineStyle((string)a["line_style"]);
                done.Add("line_style");
            }
            if (a["line_thickness"] != null) { d.LineThickness = (int)a["line_thickness"]; done.Add("line_thickness"); }
            if (a["line_color"] != null) { d.LineColor = ColorTranslator.FromHtml((string)a["line_color"]); done.Add("line_color"); }
            if (a["extend_to_bottom"] != null) { d.EnableExtendToBottomOfSection = (bool)a["extend_to_bottom"]; done.Add("extend_to_bottom"); }
            if (a["fill_color"] != null)
            {
                if (!(d is E.BoxObject b)) throw new ToolError("fill_color only applies to boxes.");
                var c = (string)a["fill_color"];
                b.FillColor = string.IsNullOrWhiteSpace(c) ? Color.Empty : ColorTranslator.FromHtml(c);
                done.Add("fill_color");
            }
        }

        private static CrystalDecisions.Shared.LineStyle EngineLineStyle(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "none": return CrystalDecisions.Shared.LineStyle.NoLine;
                case "single": return CrystalDecisions.Shared.LineStyle.SingleLine;
                case "double": throw new ToolError(DoubleLineError);
                case "dashed": return CrystalDecisions.Shared.LineStyle.DashLine;
                case "dotted": return CrystalDecisions.Shared.LineStyle.DotLine;
                default: throw new ToolError($"Invalid line_style '{s}'. Use none, single, dashed, dotted.");
            }
        }

        private static RD.CrLineStyleEnum RasLineStyle(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "none": return RD.CrLineStyleEnum.crLineStyleNoLine;
                case "single": return RD.CrLineStyleEnum.crLineStyleSingle;
                case "double": throw new ToolError(DoubleLineError);
                case "dashed": return RD.CrLineStyleEnum.crLineStyleDashed;
                case "dotted": return RD.CrLineStyleEnum.crLineStyleDotted;
                default: throw new ToolError($"Invalid line_style '{s}'. Use none, single, dashed, dotted.");
            }
        }

        /// <summary>#RRGGBB / color name → Windows COLORREF (0x00BBGGRR), the format RAS uses.</summary>
        private static uint ColorRef(string html)
        {
            var c = ColorTranslator.FromHtml(html);
            return (uint)(c.R | (c.G << 8) | (c.B << 16));
        }
    }
}

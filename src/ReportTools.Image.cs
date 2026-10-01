using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace RptMcp
{
    internal static partial class ReportTools
    {
        /// <summary>
        /// Renders pages of an exported PDF to PNG/JPEG with the PDF renderer built into Windows (Windows.Data.Pdf),
        /// since the Crystal runtime cannot export images itself. One page goes to <paramref name="output"/>;
        /// several go to &lt;name&gt;-&lt;page&gt;.&lt;ext&gt; next to it.
        /// </summary>
        private static JObject RasterizePdf(string pdf, string output, bool jpeg, int dpi, string pages)
        {
            try
            {
                // WinRT async calls are awaited on a thread-pool (MTA) thread; the server's main thread is STA.
                return Task.Run(() => RasterizeAsync(pdf, output, jpeg, dpi, pages)).GetAwaiter().GetResult();
            }
            catch (ToolError) { throw; }
            catch (Exception ex) when (ex is TypeLoadException || ex is FileNotFoundException || ex is PlatformNotSupportedException
                                       || ex is System.Runtime.InteropServices.COMException)
            {
                throw new ToolError("Rendering the PDF to an image failed (" + ex.Message.Trim() + "). " +
                                    "Image export uses the PDF renderer built into Windows 10 / Server 2016 or later (Windows.Data.Pdf); " +
                                    "on older Windows export to pdf instead.");
            }
        }

        private static async Task<JObject> RasterizeAsync(string pdf, string output, bool jpeg, int dpi, string pages)
        {
            var doc = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(pdf));
            var total = (int)doc.PageCount;
            var selected = ParsePages(pages, total);

            var dir = Path.GetDirectoryName(output);
            var name = Path.GetFileNameWithoutExtension(output);
            var ext = Path.GetExtension(output);
            if (string.IsNullOrEmpty(ext)) ext = jpeg ? ".jpg" : ".png";

            var files = new JArray();
            var sizes = new JArray();
            foreach (var n in selected)
            {
                var file = selected.Count == 1 ? Path.Combine(dir, name + ext) : Path.Combine(dir, $"{name}-{n}{ext}");
                using (var page = doc.GetPage((uint)(n - 1)))
                using (var stream = new InMemoryRandomAccessStream())
                {
                    // Page.Size is in DIPs (1/96 inch).
                    var options = new PdfPageRenderOptions
                    {
                        DestinationWidth = (uint)Math.Round(page.Size.Width * dpi / 96.0),
                        DestinationHeight = (uint)Math.Round(page.Size.Height * dpi / 96.0),
                        BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
                        BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
                    };
                    await page.RenderToStreamAsync(stream, options);
                    using (var png = stream.AsStreamForRead())
                    {
                        if (jpeg)
                            using (var img = Image.FromStream(png))
                                SaveJpeg(img, file, 90);
                        else
                            using (var fs = File.Create(file)) png.CopyTo(fs);
                    }
                    files.Add(file);
                    sizes.Add(new JObject { ["page"] = n, ["width_px"] = options.DestinationWidth, ["height_px"] = options.DestinationHeight });
                }
            }

            return new JObject
            {
                ["exported"] = files,
                ["format"] = jpeg ? "Jpeg" : "Png",
                ["dpi"] = dpi,
                ["total_pages"] = total,
                ["pages"] = sizes,
                ["size_kb"] = Math.Round(files.Sum(f => new FileInfo((string)f).Length) / 1024.0, 1),
            };
        }

        private static void SaveJpeg(Image img, string file, long quality)
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using (var ps = new EncoderParameters(1))
            {
                ps.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                img.Save(file, codec, ps);
            }
        }

        /// <summary>"1", "1-3", "1,3,5-6" (1-based) → sorted distinct page numbers; null/empty = all pages.</summary>
        private static List<int> ParsePages(string spec, int total)
        {
            if (string.IsNullOrWhiteSpace(spec)) return Enumerable.Range(1, total).ToList();
            var result = new SortedSet<int>();
            foreach (var part in spec.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()))
            {
                var range = part.Split('-');
                if (range.Length > 2 || !int.TryParse(range[0].Trim(), out var from) || !int.TryParse(range[range.Length - 1].Trim(), out var to))
                    throw new ToolError($"Invalid pages '{spec}'; use e.g. \"1\", \"1-3\" or \"1,3\".");
                if (from < 1 || to > total || from > to)
                    throw new ToolError($"Pages '{part}' out of range; the report has {total} page(s).");
                for (var i = from; i <= to; i++) result.Add(i);
            }
            if (result.Count == 0) throw new ToolError($"Invalid pages '{spec}'.");
            return result.ToList();
        }
    }
}

using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Navigation;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Util;

namespace ApiElasticSearch.Module
{
    public static class PdfHighlighter
    {
        public static byte[] HighlightSpan(string inputPath, int pageNumber, int endIndex1Based, int count, string? note = null)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "count doit être >= 1");

            // 1) Récupérer les rectangles des mots avec PdfPig
            List<Rectangle> rects;
            int startIdx0, endIdx0;
            using (var pigDoc = UglyToad.PdfPig.PdfDocument.Open(inputPath))
            {
                var pigPage = pigDoc.GetPage(pageNumber);
                var words = DefaultWordExtractor.Instance.GetWords(pigPage.Letters).ToList();
                if (words.Count == 0) throw new InvalidOperationException("Aucun mot détecté sur la page.");

                endIdx0 = endIndex1Based - 1; // 1-based → 0-based
                if (endIdx0 < 0 || endIdx0 >= words.Count)
                    throw new ArgumentOutOfRangeException(nameof(endIndex1Based), $"Hors bornes (1..{words.Count})");

                startIdx0 = endIdx0 - (count - 1);
                if (startIdx0 < 0)
                    throw new ArgumentOutOfRangeException(nameof(count), $"La plage sortirait avant le début: start={startIdx0}");

                rects = new List<Rectangle>(capacity: count);
                for (int i = startIdx0; i <= endIdx0; i++)
                {
                    var bb = words[i].BoundingBox;
                    rects.Add(new Rectangle(
                        (float)bb.Left,
                        (float)bb.Bottom,
                        (float)(bb.Right - bb.Left),
                        (float)(bb.Top - bb.Bottom)
                    ));
                }
            }

            if (rects.Count == 0) return Array.Empty<byte>();

            // 2) Annoter le PDF en mémoire avec iText7
            using var ms = new MemoryStream();
            using var reader = new PdfReader(inputPath);
            using var writer = new PdfWriter(ms);
            using var pdfDoc = new PdfDocument(reader, writer);

            var itextPage = pdfDoc.GetPage(pageNumber);

            var quadPoints = new iText.Kernel.Pdf.PdfArray();
            foreach (var r in rects)
            {
                quadPoints.Add(new iText.Kernel.Pdf.PdfNumber(r.GetLeft()));  quadPoints.Add(new iText.Kernel.Pdf.PdfNumber(r.GetTop()));
                quadPoints.Add(new iText.Kernel.Pdf.PdfNumber(r.GetRight())); quadPoints.Add(new iText.Kernel.Pdf.PdfNumber(r.GetTop()));
                quadPoints.Add(new iText.Kernel.Pdf.PdfNumber(r.GetLeft()));  quadPoints.Add(new iText.Kernel.Pdf.PdfNumber(r.GetBottom()));
                quadPoints.Add(new iText.Kernel.Pdf.PdfNumber(r.GetRight())); quadPoints.Add(new iText.Kernel.Pdf.PdfNumber(r.GetBottom()));
            }

            var union = UnionRectangles(rects);

            PdfAnnotation annot = PdfTextMarkupAnnotation.CreateHighLight(union, Array.Empty<float>())
                .SetQuadPoints(quadPoints)
                .SetColor(ColorConstants.YELLOW)
                .SetTitle(new iText.Kernel.Pdf.PdfString("Recherche"))
                .SetContents(new iText.Kernel.Pdf.PdfString(
                    note ?? $"Mots {endIndex1Based - (count - 1)}..{endIndex1Based} page {pageNumber}"
                ));

            itextPage.AddAnnotation(annot);

            // OpenAction (facultatif)
            var dest = PdfExplicitDestination.CreateXYZ(itextPage, union.GetLeft(), union.GetTop(), 1.5f);
            pdfDoc.GetCatalog().SetOpenAction(dest);

            pdfDoc.Close();
            return ms.ToArray();
        }

        private static Rectangle UnionRectangles(IEnumerable<Rectangle> rects)
        {
            using var e = rects.GetEnumerator();
            if (!e.MoveNext()) return new Rectangle(0, 0);
            float l = e.Current.GetLeft(), b = e.Current.GetBottom(), r = e.Current.GetRight(), t = e.Current.GetTop();
            while (e.MoveNext())
            {
                l = Math.Min(l, e.Current.GetLeft());
                b = Math.Min(b, e.Current.GetBottom());
                r = Math.Max(r, e.Current.GetRight());
                t = Math.Max(t, e.Current.GetTop());
            }
            return new Rectangle(l, b, r - l, t - b);
        }
    }
}

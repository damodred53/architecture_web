using System.Text;
using System.Text.Json;
using ApiElasticSearch.Worker;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Nest;
using UglyToad.PdfPig.Util;
using PigPdfDocument = UglyToad.PdfPig.PdfDocument;

using iPdf = iText.Kernel.Pdf;
using iPdfAction = iText.Kernel.Pdf.Action;
using iAnnot = iText.Kernel.Pdf.Annot;
using iColors = iText.Kernel.Colors;
using iGeom = iText.Kernel.Geom;
using iNav = iText.Kernel.Pdf.Navigation;

using Path = System.IO.Path;

namespace ApiElasticSearch.Controller
{
    [ApiController]
    [Route("api")]
    public class PdfController : ControllerBase
    {
        
        private readonly IElasticClient _es;
        private readonly IDistributedCache _cache;
        private readonly IBackgroundTaskQueue _backgroundTaskQueue;
        private readonly IServiceScopeFactory _scopeFactory;
        private const string IndexName = "documents";
        private const string PipelineName = "attachments";
        public PdfController(IElasticClient es, IDistributedCache cache, IBackgroundTaskQueue backgroundTaskQueue,
            IServiceScopeFactory scopeFactory)
        {
            _es = es;
            _cache = cache;
            _backgroundTaskQueue = backgroundTaskQueue;
            _scopeFactory = scopeFactory;
        }

   
        [HttpGet("pdf/{StartChar:int}/{EndChar:int}/{fichier}")]
        public async Task<IActionResult> GetPdfByChar(string fichier, int StartChar, int EndChar, [FromQuery] float zoom = 1.25f)
        {
            var inputPath = Path.Combine(Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory), fichier);
            if (!System.IO.File.Exists(inputPath))
                return NotFound($"Fichier introuvable : {fichier}");

            if (StartChar < 0 || EndChar <= StartChar)
                return BadRequest("Plage de caractères invalide.");

  
            var index = BuildWordIndex(inputPath);

         
            var hits = SelectWordsByCharRange(index, StartChar, EndChar);
            if (hits.Count == 0)
                return NotFound("Aucune correspondance trouvée pour cette plage de caractères (selon la reconstruction du texte).");

         
            var output = AddHighlightsAndOpen(inputPath, hits, zoom);

            Response.Headers["Content-Disposition"] = $"inline; filename=\"{Path.GetFileName(fichier.Replace("é", "e"))}\"";
            return File(output, "application/pdf");
        }

        
        [HttpGet("pdf/words/{StartWord:int}/{EndWord:int}/{fichier}")]
        public async Task<IActionResult> GetPdfByWord(string fichier, int StartWord, int EndWord, [FromQuery] float zoom = 1.25f)
        {
             var inputPath = Path.Combine(Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory), fichier);
            if (!System.IO.File.Exists(inputPath))
                return NotFound($"Fichier introuvable : {fichier}");

            if (StartWord < 0 || EndWord <= StartWord)
                return BadRequest("Plage de mots invalide.");

            var index = BuildWordIndex(inputPath);
            if (StartWord >= index.Words.Count || EndWord > index.Words.Count)
                return BadRequest($"Indices hors limites (0..{index.Words.Count - 1}).");

            var hits = index.Words.GetRange(StartWord, EndWord - StartWord);
            if (hits.Count == 0)
                return NotFound("Aucun mot sélectionné.");

            var output = AddHighlightsAndOpen(inputPath, hits, zoom);

            Response.Headers["Content-Disposition"] = $"inline; filename=\"{Path.GetFileName(fichier)}\"";
            return File(output, "application/pdf");
        }

        [HttpGet("pdf/Mot/{Mot}/{Index:int}/{fichier}")]

        public async Task<IActionResult> GetPdfByMot(string Mot, int Index,string fichier, [FromQuery] float zoom = 1.25f)
        {
/*
            var resp = await _es.SearchAsync<dynamic>(s => s
                    .Index(IndexName)
                    .Size(10)
                    .TrackTotalHits(true)
                    .Query(qry => qry
                        .Bool(b => b
                            .Should(
                                sh => sh.SimpleQueryString(sqs => sqs
                                    .Fields(f => f
                                        .Field("attachment.content")
                                        .Field("fileName")
                                    )
                                    .Query(Mot)
                                    .DefaultOperator(Operator.And)
                                    .AnalyzeWildcard(false)
                                ),
                                sh => sh.Match(m => m
                                    .Field("attachment.content")
                                    .Query(Mot)
                                    .Operator(Operator.And)
                                    .MinimumShouldMatch("100%")
                                )
                            )
                            .MinimumShouldMatch(1)
                        )
                    )
                    .Highlight(h => h
                        .PreTags("<em>").PostTags("</em>")
                        .RequireFieldMatch(false)
                        .Fields(f => f
                            .Field("attachment.content")
                            .NumberOfFragments(20000)
                            .FragmentSize(100)
                        )
                    ),CancellationToken.None
                
            );
            // 2) Analyse des tokens de la requête (si demandé)
            string[] queryTokens = Array.Empty<string>();
            
                var analyze = await _es.Indices.AnalyzeAsync(a => a
                        .Index(IndexName)
                        .Analyzer("fr_text")
                        .Text(Mot)
                 
                );

                queryTokens = analyze.Tokens?
                    .Select(t => t.Token)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray() ?? [];
            
            var queryTokenSet = new HashSet<string>(queryTokens, StringComparer.Ordinal);
            var enriched = new List<object>(resp.Hits.Count);

            foreach (var h in resp.Hits)
            {
                string fileName = string.Empty;
                if (h.Source is IDictionary<string, object> dict &&
                    dict.TryGetValue("fileName", out var v) && v is not null)
                {
                    fileName = v.ToString()!;
                }

                var snippets = Array.Empty<string>();
                if (h.Highlight != null &&
                    h.Highlight.TryGetValue("attachment.content", out var hs) &&
                    hs != null)
                {
                    snippets = hs.ToArray();
                }

                int totalOccurrences = 0;
                Dictionary<string, int>? countsByTerm = null;
                List<object>? occurrences = null;

                if ( queryTokens.Length > 0)
                {
                    var tv = await _es.TermVectorsAsync<object>(t => t
                        .Index(IndexName)
                        .Id(h.Id)
                        .Fields("attachment.content")
                        .Offsets(true)
                        .Positions(true)
                        .Payloads(false)
                        .TermStatistics(false)
                        .FieldStatistics(false)
                        
                    );

                    countsByTerm = new Dictionary<string, int>(StringComparer.Ordinal);
                    occurrences = new List<object>();

                    if (tv?.TermVectors != null &&
                        tv.TermVectors.TryGetValue("attachment.content", out var tvField) &&
                        tvField.Terms != null)
                    {
                        foreach (var kv in tvField.Terms)
                        {
                            string term = kv.Key;

                            if (!queryTokenSet.Contains(term)) continue;

                            var info = kv.Value;
                            countsByTerm[term] = info.TermFrequency;
                            totalOccurrences += info.TermFrequency;

                            if (info.Tokens != null)
                            {
                                foreach (var tok in info.Tokens)
                                {
                                    occurrences.Add(new
                                    {
                                        term,
                                        position = tok.Position,
                                        start = tok.StartOffset,
                                        end = tok.EndOffset
                                    });
                                }
                            }
                        }
                    }
                }

                enriched.Add(new
                {
                    id = h.Id,
                    score = h.Score,
                    fileName,
                    snippets,
                    totalOccurrences,
                    countsByTerm,
                    occurrences
                });
            }

            var payload = new
            {
                total = resp.Total,
                tookMs = resp.Took,
                analyzedQueryTokens = queryTokens,
                results = enriched
            };

            var json = JsonSerializer.Serialize(payload);
                

         

            return Content(json, "application/json");
            */

            var inputPath = Path.Combine(Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory) ?? throw new InvalidOperationException(), fichier);
            if (!System.IO.File.Exists(inputPath))
                return NotFound($"Fichier introuvable : {fichier}");
            List<int> pagesContenantLeMot = new List<int>();

            using (var doc = PigPdfDocument.Open(inputPath))
            {
                foreach (var page in doc.GetPages())
                {
                    var pgWords = DefaultWordExtractor.Instance.GetWords(page.Letters);
                    foreach (var w in pgWords)
                    {
                        if (w.Text.Contains(Mot))
                        {
                            pagesContenantLeMot.Add(page.Number);
                        }
                    }
                }
            }
            return Ok(pagesContenantLeMot);

        }
        
     

    
        private record WordHit(int Page, string Text, double X, double Y, double W, double H, int CharStart, int CharEnd, int LineKey);

     
        private class WordIndex
        {
            public string ReconstructedText { get; init; } = "";
            public List<WordHit> Words { get; init; } = new();
        }

        
        private static WordIndex BuildWordIndex(string pdfPath)
        {
            var words = new List<WordHit>();
            var sb = new StringBuilder();
            int globalChar = 0;

   
            using var doc = PigPdfDocument.Open(pdfPath);

            foreach (var page in doc.GetPages())
            {
      
                var pgWords = DefaultWordExtractor.Instance.GetWords(page.Letters);

                int MakeLineKey(UglyToad.PdfPig.Content.Word w)
                    => (int)Math.Round((w.BoundingBox.Top), 1);

                bool firstWordOnPage = true;

                foreach (var w in pgWords)
                {
                    var text = w.Text;
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    int lineKey = MakeLineKey(w);

                    
                    if (!firstWordOnPage)
                    {
                        sb.Append(' ');
                        globalChar += 1;
                    }
                    else
                    {
                        firstWordOnPage = false;
                    }

                    int start = globalChar;
                    sb.Append(text);
                    globalChar += text.Length;
                    int end = globalChar;

                    var bb = w.BoundingBox; 
                    words.Add(new WordHit(
                        Page: page.Number,
                        Text: text,
                        X: bb.Left,
                        Y: bb.Bottom,
                        W: bb.Width,
                        H: bb.Height,
                        CharStart: start,
                        CharEnd: end,
                        LineKey: lineKey
                    ));
                }

                sb.Append('\n');
                globalChar += 1;
            }

            return new WordIndex
            {
                ReconstructedText = sb.ToString(),
                Words = words
            };
        }

      
        private static List<WordHit> SelectWordsByCharRange(WordIndex index, int startChar, int endChar)
        {
            var list = new List<WordHit>();
            foreach (var w in index.Words)
            {
               
                if (w.CharEnd > startChar && w.CharStart < endChar)
                    list.Add(w);
            }
            return list;
        }

       
        private static List<(int page, iGeom.Rectangle rect)> MergeByLine(List<WordHit> hits, double inflate = 0)
        {
            var groups = hits
                .GroupBy(h => h.Page)
                .SelectMany(g =>
                    g.GroupBy(h => h.LineKey)
                     .Select(gg =>
                     {
                         double minX = gg.Min(x => x.X);
                         double maxX = gg.Max(x => x.X + x.W);
                         double minY = gg.Min(x => x.Y);
                         double maxY = gg.Max(x => x.Y + x.H);

                         if (inflate > 0)
                         {
                             minX -= inflate; maxX += inflate;
                             minY -= inflate; maxY += inflate;
                         }

                         return (page: g.Key, rect: new iGeom.Rectangle(
                             (float)minX, (float)minY, (float)(maxX - minX), (float)(maxY - minY)
                         ));
                     })
                )
              
                .OrderBy(t => t.page)
                .ThenByDescending(t => t.rect.GetY())
                .ToList();

            return groups;
        }

        
        private static byte[] AddHighlightsAndOpen(string inputPath, List<WordHit> hits, float zoom)
    {
        using var ms = new MemoryStream();

   
        using var reader = new iPdf.PdfReader(inputPath);
        var props  = new iPdf.WriterProperties();
        using var writer = new iPdf.PdfWriter(ms, props);

 
        using (var pdf = new iPdf.PdfDocument(reader, writer))
        {
            var bands = MergeByLine(hits, inflate: 0.5); // léger padding


            foreach (var (page, rect) in bands)
            {
                if (page < 1 || page > pdf.GetNumberOfPages()) continue;
                var pg = pdf.GetPage(page);

        
                float tlx = rect.GetLeft(),  tly = rect.GetTop();
                float trx = rect.GetRight(), try_ = rect.GetTop();
                float blx = rect.GetLeft(),  bly = rect.GetBottom();
                float brx = rect.GetRight(), bry = rect.GetBottom();

                float[] quads = {
                    tlx, tly,  trx, try_,
                    blx, bly,  brx, bry
                };

                var highlight = iAnnot.PdfTextMarkupAnnotation.CreateHighLight(rect, quads);
                highlight.SetColor(new iColors.DeviceRgb(255, 255, 0));
                highlight.SetOpacity(new iPdf.PdfNumber(0.35f)); // PdfNumber requis en C#
                highlight.SetTitle(new iPdf.PdfString("Highlight"));
                highlight.SetContents("Surlignage généré");

                pg.AddAnnotation(highlight);
            }


            if (bands.Count > 0)
            {
                var (p, r) = bands[0];
                var pg = pdf.GetPage(p);
                var dest = iNav.PdfExplicitDestination.CreateXYZ(pg, r.GetLeft(), r.GetTop(), zoom);
                pdf.GetCatalog().SetOpenAction(iPdfAction.PdfAction.CreateGoTo(dest));
            }
        }

        return ms.ToArray();
    }

    }
}

using Elasticsearch.Net;
using Microsoft.AspNetCore.Mvc;
using Nest;
using iText.Kernel.Pdf;
using System.Text.Json;

namespace ApiElasticSearch.Controller
{
    [ApiController]
    [Route("api/docs")]
    public class DocsController : ControllerBase
    {
        private readonly IElasticClient _es;
        private const string IndexName = "documents";
        private const string PipelineName = "attachments";

        // Compteur de recherches global
        public static int _totalSearches = 0;

        public DocsController(IElasticClient es) => _es = es;

        [HttpPost("/init")]
        public async Task<IActionResult> CreatePipeline()
        {
            var body = @"
            {
                ""description"": ""Extract text from PDF"",
                ""processors"": [
                    { ""attachment"": { ""field"": ""data"", ""indexed_chars"": -1 } }
                ]
            }";

            var putPipelineResponse = await _es.LowLevel.Ingest.PutPipelineAsync<StringResponse>(PipelineName, body);
            if (!putPipelineResponse.Success)
            {
                var error = putPipelineResponse.OriginalException?.Message;
                return Problem(error);
            }

            return Ok("Pipeline créé avec succès");
        }

        [HttpPost("upload")]
        public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
        {
            if (file is null || file.Length == 0)
                return BadRequest("Fichier manquant.");
            if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return BadRequest("Seuls les PDF sont acceptés.");

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);

            // --- Compter le nombre de pages ---
            int pageCount;
            using (var reader = new PdfReader(new MemoryStream(ms.ToArray())))
            using (var pdf = new PdfDocument(reader))
            {
                pageCount = pdf.GetNumberOfPages();
            }

            var base64 = Convert.ToBase64String(ms.ToArray());
            var id = Guid.NewGuid().ToString("N");

            var indexReq = new IndexRequest<dynamic>(IndexName, id)
            {
                Pipeline = PipelineName,
                Document = new
                {
                    fileName = file.FileName,
                    data = base64,
                    uploadedAt = DateTime.UtcNow,
                    pages = pageCount
                },
                Refresh = Refresh.True
            };

            var resp = await _es.IndexAsync(indexReq, ct);
            if (!resp.IsValid)
                return Problem($"Indexation échouée: {resp.ServerError?.Error?.Reason ?? resp.OriginalException?.Message}");

            return Ok(new { id, file = file.FileName, pages = pageCount });
        }

        [HttpGet("search")]
        public async Task<IActionResult> Search([FromQuery] string q,
                                                [FromQuery] int size = 10,
                                                [FromQuery] int from = 0,
                                                CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(q)) return BadRequest("Paramètre 'q' requis.");
            size = size is > 0 and <= 100 ? size : 10;
            from = Math.Max(0, from);

            // Incrémenter le compteur global
            _totalSearches++;

            var resp = await _es.SearchAsync<dynamic>(s => s
                .Index(IndexName)
                .From(from)
                .Size(size)
                .TrackTotalHits(true)
                .Query(qry => qry
                    .Bool(b => b
                        .Should(
                            sh => sh.SimpleQueryString(sqs => sqs
                                .Fields(f => f.Field("attachment.content").Field("fileName"))
                                .Query(q)
                                .DefaultOperator(Operator.And)
                                .AnalyzeWildcard(true)
                            ),
                            sh => sh.Match(m => m
                                .Field("attachment.content")
                                .Query(q)
                                .Operator(Operator.And)
                                .Fuzziness(Fuzziness.Auto)
                                .MinimumShouldMatch("75%")
                            )
                        )
                        .MinimumShouldMatch(1)
                    )
                )
                .Highlight(h => h
                    .PreTags("<em>").PostTags("</em>")
                    .RequireFieldMatch(false)
                    .Fields(f => f.Field("attachment.content").FragmentSize(180).NumberOfFragments(3))
                ),
                ct
            );

            if (!resp.IsValid)
                return Problem($"Recherche échouée: {resp.ServerError?.Error?.Reason ?? resp.OriginalException?.Message}");

            var results = resp.Hits.Select(h =>
            {
                string fileName = string.Empty;
                if (h.Source is IDictionary<string, object> dict &&
                    dict.TryGetValue("fileName", out var v) && v != null)
                    fileName = v.ToString();

                var snippets = Array.Empty<string>();
                if (h.Highlight != null && h.Highlight.TryGetValue("attachment.content", out var hs))
                    snippets = (string[])hs;

                return new { id = h.Id, score = h.Score, fileName, snippets };
            });

            return Ok(new
            {
                total = resp.Total,
                tookMs = resp.Took,
                from,
                size,
                results
            });
        }
    }

    
}

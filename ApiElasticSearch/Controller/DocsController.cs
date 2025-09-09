using Elasticsearch.Net;
using Microsoft.AspNetCore.Mvc;
using Nest;

namespace ApiElasticSearch.Controller;

[ApiController]
[Route("api/docs")]
public class DocsController : ControllerBase
{
    private readonly IElasticClient _es;
    private const string IndexName = "documents";
    private const string PipelineName = "attachments";

    public DocsController(IElasticClient es) => _es = es;



    [HttpPost("init")]
    public async Task<IActionResult> InitAll(CancellationToken ct = default)
    {
        // 1) Créer l’index s’il n’existe pas
        var exists = await _es.Indices.ExistsAsync(IndexName, i => i, ct);
        bool indexCreated = false;

        if (!exists.Exists)
        {
            var indexCreateBody = @"
            {
              ""settings"": {
                ""analysis"": {
                  ""analyzer"": {
                    ""fr_text"": { ""type"": ""french"" }
                  }
                }
              },
              ""mappings"": {
                ""properties"": {
                  ""fileName"": {
                    ""type"": ""text"",
                    ""fields"": { ""keyword"": { ""type"": ""keyword"", ""ignore_above"": 256 } }
                  },
                  ""uploadedAt"": { ""type"": ""date"" },
                  ""attachment"": {
                    ""properties"": {
                      ""content"": {
                        ""type"": ""text"",
                        ""analyzer"": ""fr_text"",
                        ""term_vector"": ""with_positions_offsets"",
                        ""index_prefixes"": { ""min_chars"": 2, ""max_chars"": 10 }
                      }
                    }
                  }
                }
              }
            }";

            var indexCreateResp = await _es.LowLevel.Indices.CreateAsync<StringResponse>(
                IndexName,
                PostData.String(indexCreateBody),
                requestParameters: null,
                ctx: ct
            );

            if (!indexCreateResp.Success)
            {
                var err = indexCreateResp.OriginalException?.Message ?? indexCreateResp.Body;
                return Problem($"Création de l'index échouée: {err}");
            }

            indexCreated = true;
        }

        // 2) (Ré)créer / mettre à jour le pipeline d’ingest
        var pipelineBody = @"
        {
          ""description"": ""Extract text from PDF"",
          ""processors"": [
            {
              ""attachment"": {
                ""field"": ""data"",
                ""target_field"": ""attachment"",
                ""indexed_chars"": -1
              }
            },
            { ""remove"": { ""field"": ""data"", ""ignore_missing"": true } }
          ],
          ""on_failure"": [
            { ""set"": { ""field"": ""ingest_error"", ""value"": ""{{ _ingest.on_failure_message }}"" } }
          ]
        }";

        var pipelineResp = await _es.LowLevel.Ingest.PutPipelineAsync<StringResponse>(
            PipelineName,
            PostData.String(pipelineBody),
            requestParameters: null,
            ctx: ct
        );

        if (!pipelineResp.Success)
        {
            var err = pipelineResp.OriginalException?.Message ?? pipelineResp.Body;
            return Problem($"Création du pipeline échouée: {err}");
        }

        return Ok(new
        {
            index = IndexName,
            indexStatus = indexCreated ? "created" : "already_exists",
            pipeline = PipelineName,
            pipelineStatus = "upserted"
        });
    }


    [HttpPost("upload")]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest("Fichier manquant.");
        if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return BadRequest("Seuls les PDF sont acceptés.");

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var base64 = Convert.ToBase64String(ms.ToArray());
        var id = Guid.NewGuid().ToString("N");

        var indexReq = new IndexRequest<dynamic>(IndexName, id)
        {
            Pipeline = PipelineName,
            Document = new
            {
                fileName = file.FileName,
                data = base64,          // le processor "attachment" lit ce champ
                uploadedAt = DateTime.UtcNow
            },
            Refresh = Refresh.True
        };

        var resp = await _es.IndexAsync(indexReq, ct);
        if (!resp.IsValid) return Problem($"Indexation échouée: {resp.ServerError?.Error?.Reason ?? resp.OriginalException?.Message}");

        return Ok(new { id, file = file.FileName });
    }

   [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string q,
        [FromQuery] int size = 10,
        [FromQuery] int from = 0,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q)) return BadRequest("Paramètre 'q' requis.");
        size = size is > 0 and <= 100 ? size : 10;
        from = Math.Max(0, from);

        var resp = await _es.SearchAsync<dynamic>(s => s
            .Index(IndexName)
            .From(from)
            .Size(size)
            .TrackTotalHits(true) 
            .Query(qry => qry
                .Bool(b => b
                    .Should(
                      
                        sh => sh.SimpleQueryString(sqs => sqs
                            .Fields(f => f
                                .Field("attachment.content")
                                .Field("fileName")        
                            )
                            .Query(q)
                            .DefaultOperator(Operator.And)
                            .AnalyzeWildcard(true)
                        ),
                        // + tolérance faute & proximité
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
                .Fields(f => f
                    .Field("attachment.content")
                    .FragmentSize(50)
                    .NumberOfFragments(200000)
                )
            )

        , ct);

        if (!resp.IsValid)
            return Problem($"Recherche échouée: {resp.ServerError?.Error?.Reason ?? resp.OriginalException?.Message}");

        var results = resp.Hits.Select(h =>
        {
            string fileName = string.Empty;

            if (h.Source is IDictionary<string, object> dict &&
                dict.TryGetValue("fileName", out var v) &&
                v is not null)
            {
                fileName = v.ToString();
            }

            var snippets = Array.Empty<string>();
            if (h.Highlight != null && h.Highlight.TryGetValue("attachment.content", out var hs))
                snippets = (string[])hs;

            return new
            {
                id = h.Id,
                score = h.Score,
                fileName,
                snippets
            };
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
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
        [FromQuery] bool includeOccurrences = true,
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
                            .AnalyzeWildcard(false)
                        ),
                        sh => sh.Match(m => m
                            .Field("attachment.content")
                            .Query(q)
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
                    .FragmentSize(50)
                )
            ),
            ct
        );

        if (!resp.IsValid)
            return Problem($"Recherche échouée: {resp.ServerError?.Error?.Reason ?? resp.OriginalException?.Message}");

      
        string[] queryTokens = Array.Empty<string>();
        if (includeOccurrences)
        {
            var analyze = await _es.Indices.AnalyzeAsync(a => a
                .Index(IndexName)
                .Analyzer("fr_text")
                .Text(q),
                ct
            );

            queryTokens = analyze.Tokens?
                .Select(t => t.Token)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
        }
        var queryTokenSet = new HashSet<string>(queryTokens, StringComparer.Ordinal);

    
        var enriched = new List<object>(resp.Hits.Count);

        foreach (var h in resp.Hits)
        {
            string fileName = string.Empty;
            if (h.Source is IDictionary<string, object> dict &&
                dict.TryGetValue("fileName", out var v) && v is not null)
            {
                fileName = v.ToString();
            }

            var snippets = Array.Empty<string>();
            if (h.Highlight != null &&
                h.Highlight.TryGetValue("attachment.content", out var hs) &&
                hs != null)
            {
                // hs est IReadOnlyCollection<string>
                snippets = hs.ToArray();
            }

            int totalOccurrences = 0;
            Dictionary<string, int>? countsByTerm = null;
            List<object>? occurrences = null;

            if (includeOccurrences && queryTokens.Length > 0)
            {
                // ✅ Pas de lambda .Field(...): on passe le champ directement en string
                var tv = await _es.TermVectorsAsync<object>(t => t
                    .Index(IndexName)
                    .Id(h.Id)
                    .Fields("attachment.content")
                    .Offsets(true)
                    .Positions(true)
                    .Payloads(false)
                    .TermStatistics(false)
                    .FieldStatistics(false),
                    ct
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

                        // On garde seulement les tokens issus de la requête
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

        return Ok(new
        {
            total = resp.Total,
            tookMs = resp.Took,
            from,
            size,
            analyzedQueryTokens = queryTokens,
            results = enriched
        });
    }



}
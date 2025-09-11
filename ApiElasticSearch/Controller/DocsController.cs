using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApiElasticSearch.Service;
using ApiElasticSearch.Worker;
using Elasticsearch.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Nest;

namespace ApiElasticSearch.Controller;

[ApiController]
[Route("api/docs")]
public class DocsController : ControllerBase
{
    private readonly IElasticClient _es;
    private readonly IDistributedCache _cache;
    private readonly IBackgroundTaskQueue _backgroundTaskQueue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DocsController> _logger;

    private const string IndexName = "documents";
    private const string PipelineName = "attachments";

    public DocsController(
        IElasticClient es,
        IDistributedCache cache,
        IBackgroundTaskQueue backgroundTaskQueue,
        IServiceScopeFactory scopeFactory,
        ILogger<DocsController> logger)
    {
        _es = es;
        _cache = cache;
        _backgroundTaskQueue = backgroundTaskQueue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // Clés de cache
    private static string CacheKey(string q, int size, int from, bool includeOccurrences)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes($"{q}|{size}|{from}|{includeOccurrences}"));
        return "search:" + Convert.ToHexString(bytes);
    }

    private static string AnalyzeCacheKey(string q)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes($"analyze:{q}"));
        return "analyze:" + Convert.ToHexString(bytes);
    }

    [HttpPost("init")]
    public async Task<IActionResult> InitAll(CancellationToken ct = default)
    {
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

        var pipelineBody = @"
        {
          ""description"": ""Extract text from PDF"",
          ""processors"": [
            { ""attachment"": { ""field"": ""data"", ""target_field"": ""attachment"", ""indexed_chars"": -1 } },
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
                data = base64,
                uploadedAt = DateTime.UtcNow
            },
            Refresh = Refresh.True
        };

        var resp = await _es.IndexAsync(indexReq, ct);
        if (!resp.IsValid)
            return Problem($"Indexation échouée: {resp.ServerError?.Error?.Reason ?? resp.OriginalException?.Message}");

        // Copie locale (facultatif)
        try
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), file.FileName);
            await using var stream = new FileStream(path, FileMode.Create);
            await file.CopyToAsync(stream, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec de la copie locale du fichier {File}", file.FileName);
        }

        return Ok(new { id, file = file.FileName });
    }

    [HttpGet("search")]
    [OutputCache(PolicyName = "SearchCache")]
    public async Task<IActionResult> Search(
        [FromQuery] string q,
        [FromQuery] int size = 10,
        [FromQuery] int from = 0,
        [FromQuery] bool includeOccurrences = true,
        CancellationToken ct = default)
    {
        // tâche asynchrone annexe (non bloquante)
        _backgroundTaskQueue.QueueBackgroundWorkItem(async ct2 =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var firebase = scope.ServiceProvider.GetRequiredService<FireBaseEnr>();
                await firebase.ExecAsync(q, ct2);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background task FireBaseEnr.ExecAsync failed for query {Q}", q);
            }
        });

        if (string.IsNullOrWhiteSpace(q)) return BadRequest("Paramètre 'q' requis.");
        size = size is > 0 and <= 100 ? size : 10;
        from = Math.Max(0, from);

        var sw = Stopwatch.StartNew();

        // 0) Tentative cache Redis (tolérance pannes)
        var cacheKey = CacheKey(q, size, from, includeOccurrences);
        string? cachedJson = null;
        try
        {
            cachedJson = await _cache.GetStringAsync(cacheKey, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis GET search cache failed for {Key}", cacheKey);
            Response.Headers["X-Cache"] = "BYPASS";
#if DEBUG
            Response.Headers["X-Cache-Error"] = "get:" + ex.GetType().Name;
#endif
        }

        if (cachedJson is not null)
        {
            sw.Stop();
            Response.Headers["X-Cache"] = "HIT";
            Response.Headers["X-Duration-Ms"] = sw.ElapsedMilliseconds.ToString();
            SetCacheHeaders();
            return Content(cachedJson, "application/json");
        }

        // A) _analyze : cacheable 24h
        string[] queryTokens = Array.Empty<string>();
        if (includeOccurrences)
        {
            var analyzeKey = AnalyzeCacheKey(q);
            try
            {
                var cachedAnalyze = await _cache.GetStringAsync(analyzeKey, ct);
                if (cachedAnalyze is not null)
                {
                    queryTokens = JsonSerializer.Deserialize<string[]>(cachedAnalyze) ?? Array.Empty<string>();
                    Response.Headers["X-Analyze-Cache"] = "HIT";
                }
                else
                {
                    Response.Headers["X-Analyze-Cache"] = "MISS";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Redis GET analyze cache failed for {Key}", analyzeKey);
                Response.Headers["X-Analyze-Cache"] = "BYPASS";
#if DEBUG
                Response.Headers["X-Cache-Error"] = "analyze-get:" + ex.GetType().Name;
#endif
            }

            if (queryTokens.Length == 0)
            {
                var analyze = await _es.Indices.AnalyzeAsync(a => a
                    .Index(IndexName)
                    .Analyzer("fr_text")
                    .Text(q), ct);

                queryTokens = analyze.Tokens?
                    .Select(t => t.Token)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.Ordinal)
                    .Take(32)
                    .ToArray() ?? Array.Empty<string>();

                try
                {
                    await _cache.SetStringAsync(
                        analyzeKey,
                        JsonSerializer.Serialize(queryTokens),
                        new DistributedCacheEntryOptions
                        {
                            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24)
                        }, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Redis SET analyze cache failed for {Key}", analyzeKey);
                }
            }
        }

        var queryTokenSet = new HashSet<string>(queryTokens, StringComparer.Ordinal);

        // B) Requête ES optimisée (request cache + highlight raisonnable)
        var resp = await _es.SearchAsync<dynamic>(s => s
                .Index(IndexName)
                .From(from)
                .Size(size)
                .TrackTotalHits(true)
                .RequestCache(true)
                .Source(sf => sf.Includes(i => i.Field("fileName")))
                .Query(qry => qry.SimpleQueryString(sqs => sqs
                    .Fields(f => f.Field("attachment.content").Field("fileName"))
                    .Query(q)
                    .DefaultOperator(Operator.And)
                    .AnalyzeWildcard(false)))
                .Highlight(h => h
                    .PreTags("<em>").PostTags("</em>")
                    .RequireFieldMatch(true)
                    .Fields(hf => hf
                        .Field("attachment.content")
                        .NumberOfFragments(5)
                        .FragmentSize(160)
                        .NoMatchSize(160))),
            ct);

        if (!resp.IsValid)
            return Problem($"Recherche échouée: {resp.ServerError?.Error?.Reason ?? resp.OriginalException?.Message}");

        var enriched = new List<object>(resp.Hits.Count);
        var hitList = resp.Hits.ToList();

        // C) MultiTermVectors en batch (si occurrences demandées)
        Dictionary<string, (int total, Dictionary<string, int> perTerm, List<object> occ)>? tvById = null;

        if (includeOccurrences && queryTokens.Length > 0 && hitList.Count > 0)
        {
            var ops = hitList.Select(h =>
                (IMultiTermVectorOperation)new MultiTermVectorOperation<object>(h.Id)
                {
                    Fields = (Fields)(Field)"attachment.content",
                    Offsets = true,
                    Positions = true,
                    Payloads = false,
                    TermStatistics = false,
                    FieldStatistics = false
                }).ToArray();

            var req = new MultiTermVectorsRequest(IndexName) { Documents = ops };

            var mtv = await _es.MultiTermVectorsAsync(req, ct);

            tvById = new Dictionary<string, (int, Dictionary<string, int>, List<object>)>(StringComparer.Ordinal);
            if (mtv.IsValid && mtv.Documents != null)
            {
                foreach (var d in mtv.Documents)
                {
                    var id = d.Id;
                    int totalOccurrences = 0;
                    var countsByTerm = new Dictionary<string, int>(StringComparer.Ordinal);
                    var occurrences = new List<object>();

                    if (d.Found &&
                        d.TermVectors != null &&
                        d.TermVectors.TryGetValue("attachment.content", out var fieldTv) &&
                        fieldTv.Terms != null)
                    {
                        foreach (var kv in fieldTv.Terms)
                        {
                            var term = kv.Key;
                            if (!queryTokenSet.Contains(term)) continue;

                            var info = kv.Value;
                            var tf = info.TermFrequency;
                            if (tf > 0)
                            {
                                countsByTerm[term] = tf;
                                totalOccurrences += tf;
                            }

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

                    tvById[id] = (totalOccurrences, countsByTerm, occurrences);
                }
            }
        }

        foreach (var h in hitList)
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

            if (includeOccurrences && queryTokens.Length > 0 &&
                tvById is not null && tvById.TryGetValue(h.Id, out var t))
            {
                totalOccurrences = t.total;
                countsByTerm = t.perTerm;
                occurrences = t.occ;
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
            from,
            size,
            analyzedQueryTokens = queryTokens,
            results = enriched
        };

        var json = JsonSerializer.Serialize(payload);

        // 4) Mise en cache Redis (tolérance pannes)
        try
        {
            await _cache.SetStringAsync(
                cacheKey,
                json,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24)
                }, ct);
            Response.Headers["X-Cache-Store"] = "OK";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis SET search cache failed for {Key}", cacheKey);
            Response.Headers["X-Cache-Store"] = "SKIPPED";
#if DEBUG
            Response.Headers["X-Cache-Error"] = "set:" + ex.GetType().Name;
#endif
        }

        sw.Stop();

        if (!Response.Headers.ContainsKey("X-Cache"))
            Response.Headers["X-Cache"] = "MISS";

        Response.Headers["X-Duration-Ms"] = sw.ElapsedMilliseconds.ToString();

        SetCacheHeaders(); // pour CDN/proxy

        return Content(json, "application/json");
    }

    private void SetCacheHeaders()
    {
        // Autorise le cache partagé (CDN/proxy) + SWR
        Response.Headers["Cache-Control"] = "public, max-age=120, s-maxage=300, stale-while-revalidate=30";
        Response.Headers["Vary"] = "Accept-Encoding";
        // Pas de Set-Cookie ici, sinon les proxies refuseront le cache partagé
    }
}
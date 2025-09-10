using Elasticsearch.Net;
using Microsoft.AspNetCore.Mvc;
using Nest;
using System.Text.Json;

namespace ApiElasticSearch.Controller
{
    [ApiController]
    [Route("api/stats")]
    public class StatistiqueController : ControllerBase
    {
        private readonly IElasticClient _es;
        private const string IndexName = "documents";

        public StatistiqueController(IElasticClient es) => _es = es;

        [HttpGet("stats")]
        public async Task<IActionResult> GetStats(CancellationToken ct)
        {
            // Récupérer tous les documents
            var searchResp = await _es.SearchAsync<dynamic>(s => s
                .Index(IndexName)
                .Size(1000)
                .Source(sf => sf.Includes(i => i.Fields("fileName", "attachment.content", "pages"))), ct);

            if (!searchResp.IsValid)
                return Problem($"Impossible de récupérer les documents: {searchResp.ServerError?.Error?.Reason ?? searchResp.OriginalException?.Message}");

            var documents = searchResp.Hits.Select(h =>
            {
                var dict = h.Source as IDictionary<string, object>;
                string fileName = dict?["fileName"]?.ToString() ?? "";
                string content = (dict?["attachment"] as IDictionary<string, object>)?["content"]?.ToString() ?? "";
                int pages = 0;

                if (dict != null && dict.TryGetValue("pages", out var pValue) && int.TryParse(pValue?.ToString(), out var p))
                {
                    pages = p;
                }
                return new { fileName, content, pages };
            }).ToList();
            
            // Fonction utilitaire pour compter les mots
            static Dictionary<string, int> CountWords(string text)
            {
                return (text ?? "")
                    .ToLowerInvariant()
                    .Split(new[] { ' ', '\n', '\r', '\t', ',', '.', ';', ':', '!', '?', '«', '»', '"', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries)
                    .GroupBy(w => w)
                    .ToDictionary(g => g.Key, g => g.Count());
            }

            // Stats par document (avec séparation visible)
            var statsDocs = documents.Select(d =>
            {
                var wordCounts = CountWords(d.content);

                var mostCommon = new Dictionary<string, object>
                {
                    ["------------------- Les mots les plus communs -------------------"] = " ",
                    ["Most_Common_Words"] = wordCounts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value)
                };

                var rarest = new Dictionary<string, object>
                {
                    ["------------------- Les mots les plus rares -------------------"] = " ",
                    ["Rarest_Words"] = wordCounts.OrderBy(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value)
                };

                return new
                {
                    d.fileName,
                    d.pages,
                    totalWords = wordCounts.Count,
                    mostCommonWords = mostCommon,
                    rarestWords = rarest
                };
            }).ToList();

            // Stats globales (avec séparation visible)
            var allWords = documents
                .SelectMany(d => CountWords(d.content))
                .GroupBy(kv => kv.Key)
                .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));

            var globalStats = new
            {
                summary = new
                {
                    totalPdfImported = documents.Count,
                    totalSearches = DocsController._totalSearches,
                    totalPages = documents.Sum(d => d.pages),
                    averagePages = documents.Count > 0 ? documents.Sum(d => d.pages) / documents.Count : 0,
                    totalWordsGlobal = statsDocs.Sum(d => d.totalWords),

                    wordsAnalysis = new Dictionary<string, object>
                    {
                        ["------------------- Les mots les plus communs -------------------"] = " ",
                        ["Most_Common_Words"] = allWords
                            .OrderByDescending(kv => kv.Value)
                            .ToDictionary(kv => kv.Key, kv => kv.Value),

                        ["------------------- Les mots les plus rares -------------------"] = " ",
                        ["Rarest_Words"] = allWords
                            .OrderBy(kv => kv.Value)
                            .ToDictionary(kv => kv.Key, kv => kv.Value)
                    }
                },
                documents = statsDocs
            };

            // Génération du JSON
            var jsonFilePath = Path.Combine(Directory.GetCurrentDirectory(), "stats.json");
            await System.IO.File.WriteAllTextAsync(jsonFilePath, JsonSerializer.Serialize(globalStats, new JsonSerializerOptions { WriteIndented = true }));

            return Ok(new { message = "Statistiques générées avec succès", path = jsonFilePath, data = globalStats });
        }

        [HttpDelete("clear")]
        public async Task<IActionResult> ClearIndex(CancellationToken ct)
        {
            var response = await _es.DeleteByQueryAsync<dynamic>(q => q
                .Index(IndexName)
                .Query(rq => rq.MatchAll()), ct);

            if (!response.IsValid)
                return Problem($"Erreur suppression: {response.ServerError?.Error?.Reason}");

            return Ok(new { message = "Tous les documents ont été supprimés de l'index 'documents'." });
        }
    }
}

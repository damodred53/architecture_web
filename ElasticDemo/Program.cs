using Elastic.Clients.Elasticsearch;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using System;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        // Connexion à Elasticsearch
        var settings = new ElasticsearchClientSettings(new Uri("http://localhost:9200"))
            .DefaultIndex("pdf_index");
        
        

        var client = new ElasticsearchClient(settings);

        // Indexer un document test
        var doc = new { Id = 1, Content = "Bonjour Elastic avec .NET 9 🚀" };
        var indexResponse = await client.IndexAsync(doc);

        Console.WriteLine($"Document indexé : {indexResponse.IsValidResponse}");

        // Recherche d'un mot
        var searchResponse = await client.SearchAsync<object>(s => s
            .Query(q => q.Match(m => m.Field("content").Query("Bonjour")))
        );

        Console.WriteLine($"Nombre de résultats : {searchResponse.Hits.Count}");
        foreach (var hit in searchResponse.Hits)
        {
            Console.WriteLine(hit.Source);
        }
    }
}
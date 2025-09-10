using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using Microsoft.AspNetCore.Mvc;

namespace ApiElasticSearch.Controller;

[ApiController]
[Route("[controller]")]
public class FireBaseRechercheController : ControllerBase
{
    [HttpGet("recherche/{mot}")]
    public async Task<IActionResult> Get(string mot, CancellationToken ct)
    {
        var path = "ledico-d498c-firebase-adminsdk-fbsvc-eef0abcf49.json";

        var credential = GoogleCredential.FromFile(path)
            .CreateScoped("https://www.googleapis.com/auth/datastore");

        var client = new FirestoreClientBuilder { Credential = credential }.Build();
        var db = FirestoreDb.Create("ledico-d498c", client);


        var queryStart = db.Collection("CollectionMot")
            .OrderBy("Mot")
            .StartAt(mot)
            .EndAt(mot + "\uf8ff");

        var snapshotStart = await queryStart.GetSnapshotAsync(ct);

        var resultsStart = snapshotStart.Documents.Select(d => new
        {
            id = d.Id,
            type = "commence",
            mot = d.GetValue<string>("Mot"),
            motInverser = d.GetValue<string>("MotInverser"),
            date = d.GetValue<DateTime>("Date")
        });

       
        var motInverse = new string(mot.Reverse().ToArray());

        var queryEnd = db.Collection("CollectionMot")
            .OrderBy("MotInverser")
            .StartAt(motInverse)
            .EndAt(motInverse + "\uf8ff");

        var snapshotEnd = await queryEnd.GetSnapshotAsync(ct);

        var resultsEnd = snapshotEnd.Documents.Select(d => new
        {
            id = d.Id,
            type = "finit",
            mot = d.GetValue<string>("Mot"),
            motInverser = d.GetValue<string>("MotInverser"),
            date = d.GetValue<DateTime>("Date")
        });


        var merged = resultsStart.Concat(resultsEnd);

// Pré-calcule la fréquence de chaque mot
        var freq = merged
            .GroupBy(x => x.mot)
            .ToDictionary(g => g.Key, g => g.Count());

// Trie par fréquence, puis garde un seul élément par mot
        var top10 = merged
            .OrderByDescending(x => freq[x.mot])
            .ThenBy(x => x.mot)
            .DistinctBy(x => x.mot)
            .Take(10)
            .ToList();

           

        return top10.Count == 0 ? NotFound() : Ok(top10);
    }

}

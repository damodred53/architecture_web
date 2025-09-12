using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;

namespace ApiElasticSearch.Service;

public class FireBaseEnr
{
   
 public async Task ExecAsync(string mot, CancellationToken ct = default)
    {
        var path = "ledico-d498c-firebase-adminsdk-fbsvc-f3f266b947.json";

        var credential = GoogleCredential.FromFile(path)
            .CreateScoped("https://www.googleapis.com/auth/datastore");

        var client = new FirestoreClientBuilder { Credential = credential }.Build();
        var db = FirestoreDb.Create("ledico-d498c", client);

        string? motInverser = new string(mot.Reverse().ToArray());
        var collection = db.Collection("CollectionMot");
        await collection.AddAsync(new { Mot = mot, Date = DateTime.UtcNow, MotInverser = motInverser }, ct);

   
    }
}
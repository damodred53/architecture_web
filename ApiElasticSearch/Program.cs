using ApiElasticSearch.Service;
using ApiElasticSearch.Worker;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Options;
using Nest;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Logs utiles en dev/prod
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// Swagger + MVC
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddControllers();

builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddScoped<FireBaseEnr>();

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// ---------- Redis (support chaîne simple OU cluster via options) ----------
var redisSection = builder.Configuration.GetSection("Redis");
var useRedis = redisSection.GetValue<bool>("UseRedis", true);

// Si "Redis:Configuration" est présent, on l'utilise tel quel (compatibilité)
var redisConn = builder.Configuration.GetValue<string>("Redis:Configuration");

if (useRedis && (!string.IsNullOrWhiteSpace(redisConn) || redisSection.GetSection("Endpoints").Exists()))
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        if (!string.IsNullOrWhiteSpace(redisConn))
        {
            // mode simple "host:port" (compat)
            options.Configuration = redisConn;
        }
        else
        {
            // mode cluster
            var endpoints = redisSection.GetSection("Endpoints").Get<string[]>() ?? Array.Empty<string>();
            var password = redisSection.GetValue<string>("Password");
            var connectTimeout = redisSection.GetValue<int>("ConnectTimeout", 15000);
            var connectRetry = redisSection.GetValue<int>("ConnectRetry", 3);

            var co = new ConfigurationOptions
            {
                AbortOnConnectFail = false,
                ConnectTimeout = connectTimeout,
                ConnectRetry = connectRetry,
                Password = password,
                KeepAlive = 60,
                DefaultDatabase = 0,
                // Ssl = true, // active si ton cluster est TLS
                // CertificateValidation += (s, cert, chain, errs) => true // si self-signed (à éviter en prod)
            };

            foreach (var ep in endpoints) co.EndPoints.Add(ep);

            options.ConfigurationOptions = co;
        }

        options.InstanceName = "docsapi:"; // préfixe
    });
}
else
{
    builder.Services.AddDistributedMemoryCache(); // fallback
}

// ---------- Elasticsearch ----------
var esUrl = builder.Configuration.GetSection("ElasticSearch:Url").Get<string>() ?? "http://localhost:9200";
var esSettings = new ConnectionSettings(new Uri(esUrl))
    .DefaultIndex("documents");
builder.Services.AddSingleton<IElasticClient>(new ElasticClient(esSettings));

// ---------- Output Caching ----------
builder.Services.AddOutputCache(options =>
{
    options.AddPolicy("SearchCache", p => p
        .Expire(TimeSpan.FromMinutes(2))
        .SetVaryByQuery("q", "size", "from", "includeOccurrences")
        .Tag("docs-search"));
});

var app = builder.Build();

// Pipeline HTTP
app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();
app.UseCors();

app.UseOutputCache(); // <— important : avant MapControllers

app.MapControllers();

app.Run();
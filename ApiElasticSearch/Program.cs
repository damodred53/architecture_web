using ApiElasticSearch.Service;
using ApiElasticSearch.Worker;
using Nest;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddControllers();
builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddScoped<FireBaseEnr>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

// Redis (env: Redis__Configuration, fallback dev)
var redisConn = builder.Configuration.GetValue<string>("Redis:Configuration") ?? "localhost:6379";
builder.Services.AddStackExchangeRedisCache(opt =>
{
    opt.Configuration = redisConn;
    opt.InstanceName = "docsapi:";
});

// Elasticsearch (env: Elastic__Uri, fallback dev)
var esUri = builder.Configuration.GetValue<string>("Elastic:Uri") ?? "http://localhost:9200";
var esSettings = new ConnectionSettings(new Uri(esUri))
    .DefaultIndex("documents");
builder.Services.AddSingleton<IElasticClient>(new ElasticClient(esSettings));

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

// En conteneur, tu peux souvent laisser tomber la redirection HTTPS
// app.UseHttpsRedirection();

app.UseCors();
app.MapControllers();

app.Run();
using ApiElasticSearch.Service;
using ApiElasticSearch.Worker;
using Nest;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddControllers();
builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddScoped<FireBaseEnr>();


//test
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// 🔧 (facultatif) appsettings.json → "Redis:Configuration": "localhost:6379"
var redisConn = builder.Configuration.GetValue<string>("Redis:Configuration");

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConn;    
    options.InstanceName = "docsapi:"; // préfixe des clés
});

var esSettings = new ConnectionSettings(new Uri("http://localhost:9200"))
    .DefaultIndex("documents");
builder.Services.AddSingleton<IElasticClient>(new ElasticClient(esSettings));



var app = builder.Build();


// Configure the HTTP request pipeline.

    app.UseSwagger();
    app.UseSwaggerUI();

app.UseHttpsRedirection();
app.UseCors();
app.MapControllers();

app.Run();


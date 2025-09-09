using Nest;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddControllers();

var esSettings = new ConnectionSettings(new Uri("http://localhost:9200"))
    .DefaultIndex("documents");
builder.Services.AddSingleton<IElasticClient>(new ElasticClient(esSettings));



var app = builder.Build();


// Configure the HTTP request pipeline.

    app.UseSwagger();
    app.UseSwaggerUI();

app.UseHttpsRedirection();
app.MapControllers();



app.Run();


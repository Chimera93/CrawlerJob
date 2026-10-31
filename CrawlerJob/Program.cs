using Crawler.Infrastructure;
using MassTransit;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Website Crawl Jobs API",
        Version = "v1",
        Description = "Submit and manage asynchronous website crawl jobs."
    });
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    options.IncludeXmlComments(System.IO.Path.Combine(AppContext.BaseDirectory, xmlFile));
});
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(builder.Configuration["Frontend:Origin"] ?? "http://localhost:5173")
        .AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddSingleton<CrawlStore>();
builder.Services.AddSingleton<ICrawlJobStore>(services => services.GetRequiredService<CrawlStore>());
builder.Services.AddSingleton<ICrawlUrlPolicy, PublicHttpUrlPolicy>();
builder.Services.AddHealthChecks();
builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((_, cfg) =>
    {
        var rabbitMqUri = builder.Configuration["RabbitMq:Uri"];
        if (rabbitMqUri is not null)
            cfg.Host(new Uri(rabbitMqUri));
        else
            cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "localhost");
    });
});

var app = builder.Build();
await app.Services.GetRequiredService<CrawlStore>().InitializeAsync();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.MapHealthChecks("/health");
app.UseCors();
app.MapControllers();
app.Run();

public partial class Program;

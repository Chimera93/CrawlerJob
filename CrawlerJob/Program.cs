using Crawler.Infrastructure;
using MassTransit;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(builder.Configuration["Frontend:Origin"] ?? "http://localhost:5173")
        .AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddSingleton<CrawlStore>();
builder.Services.AddSingleton<ICrawlJobStore>(services => services.GetRequiredService<CrawlStore>());
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
app.MapHealthChecks("/health");
app.UseCors();
app.MapControllers();
app.Run();

public partial class Program;

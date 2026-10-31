using Crawler.Infrastructure;
using Crawler.Worker;
using MassTransit;
using System.Net;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<CrawlStore>();
builder.Services.AddSingleton<ICrawlJobStore>(services => services.GetRequiredService<CrawlStore>());
builder.Services.AddSingleton<ICrawlExecutionStore>(services => services.GetRequiredService<CrawlStore>());
builder.Services.AddHttpClient("crawler", client =>
    client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    });
builder.Services.AddHealthChecks();
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<CrawlConsumer>();
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "localhost");
        cfg.ConfigureCrawlEndpoint(context);
    });
});
var app = builder.Build();
await app.Services.GetRequiredService<CrawlStore>().InitializeAsync();
app.MapHealthChecks("/health");
await app.RunAsync();

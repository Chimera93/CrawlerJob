using Crawler.Infrastructure;
using Crawler.Worker;
using MassTransit;
using System.Net;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<CrawlStore>();
builder.Services.AddSingleton<ICrawlJobStore>(services => services.GetRequiredService<CrawlStore>());
builder.Services.AddSingleton<ICrawlExecutionStore>(services => services.GetRequiredService<CrawlStore>());
builder.Services.AddSingleton<PublicHttpUrlPolicy>();
builder.Services.AddSingleton<ICrawlUrlPolicy>(services => services.GetRequiredService<PublicHttpUrlPolicy>());
builder.Services.AddHttpClient("crawler", client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(services => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectCallback = (context, cancellationToken) =>
            services.GetRequiredService<PublicHttpUrlPolicy>().ConnectAsync(context, cancellationToken)
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

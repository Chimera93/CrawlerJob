using MassTransit;

namespace Crawler.Worker;

public static class CrawlBusConfiguration
{
    public static void ConfigureCrawlEndpoint(this IRabbitMqBusFactoryConfigurator configurator,
        IBusRegistrationContext context, string endpointName = "crawl-jobs",
        int? concurrentMessageLimit = null)
    {
        configurator.ReceiveEndpoint(endpointName, endpoint =>
        {
            if (concurrentMessageLimit is not null)
                endpoint.ConcurrentMessageLimit = concurrentMessageLimit.Value;
            endpoint.UseMessageRetry(retry => retry.Intervals(
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
            endpoint.ConfigureConsumer<CrawlConsumer>(context);
        });
    }
}

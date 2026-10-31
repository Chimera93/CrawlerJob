FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Crawler.Infrastructure/ Crawler.Infrastructure/
COPY Crawler.Worker/ Crawler.Worker/
RUN dotnet publish Crawler.Worker/Crawler.Worker.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8081
ENTRYPOINT ["dotnet", "Crawler.Worker.dll"]

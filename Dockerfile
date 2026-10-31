FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/DocRouter.Host -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
# Шрифты с кириллицей понадобятся шагу ApplyStamp (SkiaSharp)
RUN apt-get update && apt-get install -y --no-install-recommends fonts-dejavu-core fontconfig \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "DocRouter.Host.dll"]

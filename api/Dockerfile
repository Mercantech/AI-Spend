FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY AiSpend.Api.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false
EXPOSE 8080
ENTRYPOINT ["dotnet", "AiSpend.Api.dll"]

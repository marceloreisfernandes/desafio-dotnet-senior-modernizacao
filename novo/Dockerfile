FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY novo/src/NotaCarioca.Consulta/ ./
RUN dotnet publish NotaCarioca.Consulta.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "NotaCarioca.Consulta.dll"]

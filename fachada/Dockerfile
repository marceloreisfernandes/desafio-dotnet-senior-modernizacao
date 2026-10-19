FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY fachada/src/NotaCarioca.Fachada/ ./
RUN dotnet publish NotaCarioca.Fachada.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "NotaCarioca.Fachada.dll"]

# build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# copy everything and restore/publish
COPY . .
RUN dotnet restore "Google_Photos_Integration_API.csproj"
RUN dotnet publish "Google_Photos_Integration_API.csproj" -c Release -o /app/publish --no-restore

# runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# listen on the port Render provides
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT}
ENV ASPNETCORE_ENVIRONMENT=Production

# optional: expose a default port (Render uses PORT env anyway)
EXPOSE 80

ENTRYPOINT ["dotnet", "Google_Photos_Integration_API.dll"]

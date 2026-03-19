FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY global.json ./
COPY server/CipherServer.csproj server/
RUN dotnet restore server/CipherServer.csproj

COPY server/ server/
RUN dotnet publish server/CipherServer.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "CipherServer.dll"]

# The scenario node image, built from the working tree. Build context: the repository root.
#   docker build -f tests/SharpTicker.Scenarios.Host/Dockerfile -t sharpticker-scenarios-host:dev .

# The dashboard front end, built as CI builds it; dist is embedded into SharpTicker.Dashboard by the .NET build.
FROM node:22 AS dashboard
WORKDIR /wwwroot
COPY src/SharpTicker.Dashboard/wwwroot/package.json src/SharpTicker.Dashboard/wwwroot/package-lock.json ./
RUN npm ci
COPY src/SharpTicker.Dashboard/wwwroot/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY . .
COPY --from=dashboard /wwwroot/dist src/SharpTicker.Dashboard/wwwroot/dist
RUN dotnet publish tests/SharpTicker.Scenarios.Host/SharpTicker.Scenarios.Host.csproj \
        -c Release -f net10.0 -o /app \
        -p:GeneratePackageOnBuild=false \
        -p:EnableSourceLink=false \
        -p:EnableSourceControlManagerQueries=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SharpTicker.Scenarios.Host.dll"]

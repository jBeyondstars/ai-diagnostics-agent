FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["src/Agent.Api/Agent.Api.csproj", "src/Agent.Api/"]
COPY ["src/Agent.Core/Agent.Core.csproj", "src/Agent.Core/"]
COPY ["src/Agent.Plugins/Agent.Plugins.csproj", "src/Agent.Plugins/"]
COPY ["src/Agent.ServiceDefaults/Agent.ServiceDefaults.csproj", "src/Agent.ServiceDefaults/"]

RUN dotnet restore "src/Agent.Api/Agent.Api.csproj"

COPY . .

WORKDIR "/src/src/Agent.Api"
RUN dotnet build "Agent.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Agent.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Agent.Api.dll"]

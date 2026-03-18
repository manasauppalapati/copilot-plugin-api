# Stage 1 — build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/CopilotPluginApi.csproj", "src/"]
RUN dotnet restore "src/CopilotPluginApi.csproj"
COPY . .
WORKDIR "/src/src"
RUN dotnet publish -c Release -o /app/publish \
    /p:UseAppHost=false

# Stage 2 — runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
RUN adduser --disabled-password --gecos "" appuser
COPY --from=build /app/publish .
RUN chown -R appuser:appuser /app
USER appuser
ENTRYPOINT ["dotnet", "CopilotPluginApi.dll"]

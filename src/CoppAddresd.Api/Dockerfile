# =========================
# BUILD
# =========================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Capa de restore separada: solo se invalida si cambian los csproj, no con
# cada cambio de código (ahorra el restore frío en cada build de CI con caché).
COPY src/CoppAddresd.Domain/CoppAddresd.Domain.csproj src/CoppAddresd.Domain/
COPY src/CoppAddresd.Application/CoppAddresd.Application.csproj src/CoppAddresd.Application/
COPY src/CoppAddresd.Infrastructure/CoppAddresd.Infrastructure.csproj src/CoppAddresd.Infrastructure/
COPY src/CoppAddresd.WhatsApp/CoppAddresd.WhatsApp.csproj src/CoppAddresd.WhatsApp/
COPY src/CoppAddresd.Api/CoppAddresd.Api.csproj src/CoppAddresd.Api/
COPY src/Services/CoppAddresd.Auth/CoppAddresd.Auth.csproj src/Services/CoppAddresd.Auth/
COPY tools/CoppAddresd.MedicationDemoSeed/CoppAddresd.MedicationDemoSeed.csproj tools/CoppAddresd.MedicationDemoSeed/

RUN dotnet restore "src/CoppAddresd.Api/CoppAddresd.Api.csproj"
RUN dotnet restore "tools/CoppAddresd.MedicationDemoSeed/CoppAddresd.MedicationDemoSeed.csproj"

COPY . .

RUN dotnet publish \
    "src/CoppAddresd.Api/CoppAddresd.Api.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore

# Explicit maintenance executable; never started by the API or its migrations.
RUN dotnet publish "tools/CoppAddresd.MedicationDemoSeed/CoppAddresd.MedicationDemoSeed.csproj" -c Release -o /app/medication-demo --no-restore \
    && find /app/medication-demo -iname 'appsettings*.json' -delete

# =========================
# RUNTIME
# =========================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app

COPY --from=build /app/publish .
COPY --from=build /app/medication-demo ./medication-demo

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080

EXPOSE 8080

ENTRYPOINT ["dotnet", "CoppAddresd.Api.dll"]

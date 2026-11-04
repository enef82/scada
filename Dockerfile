# Опциональная контейнерная сборка. Основной способ установки — systemd (deploy/install.sh).
# Для закрытого контура: собрать образы на машине с интернетом и перенести через docker save/load
# или собственный registry (Docker Hub из РФ доступен нестабильно).

# ---------- фронтенд
FROM node:22-alpine AS frontend
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY frontend/ ./
RUN npx tsc --noEmit && npx vite build --outDir /out/wwwroot --emptyOutDir

# ---------- бэкенд
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
ARG SCADA_OPCUA=true
WORKDIR /src/backend
COPY backend/ ./
RUN dotnet publish src/Scada.Acquisition/Scada.Acquisition.csproj -c Release -p:ScadaOpcUa=${SCADA_OPCUA} -o /out/acquisition \
 && dotnet publish src/Scada.Web/Scada.Web.csproj -c Release -o /out/web
COPY --from=frontend /out/wwwroot /out/web/wwwroot

# ---------- служба сбора
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS acquisition
WORKDIR /app
COPY --from=backend /out/acquisition ./
RUN mkdir -p /var/lib/scada/spool /var/lib/scada/pki && chown -R app:app /var/lib/scada
USER app
ENTRYPOINT ["dotnet", "scada-acquisition.dll"]

# ---------- веб-приложение
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS web
WORKDIR /app
COPY --from=backend /out/web ./
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "scada-web.dll"]

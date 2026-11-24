FROM node:24-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY Noor.sln ./
COPY src/ src/
RUN dotnet publish src/Noor.Api -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /out ./
COPY --from=web /web/dist ./wwwroot
RUN mkdir -p /data/files && chown -R app:app /data
USER app
ENV ASPNETCORE_URLS=http://+:8080 Storage__Root=/data/files
EXPOSE 8080
ENTRYPOINT ["dotnet", "Noor.Api.dll"]

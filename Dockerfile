FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY MarktplaatsNotifier.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

ENV TZ=Europe/Amsterdam
ENV MARKTPLAATS_DATA_DIR=/data
ENV WEB_PORT=5050

RUN mkdir -p /data
COPY --from=build /app/publish ./

EXPOSE 5050
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "MarktplaatsNotifier.dll"]

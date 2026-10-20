# Plataforma de evaluaciones. La misma imagen sirve como ejecutor remoto:
#   docker run ... evaluaciones ejecutor --plataforma https://... --grupo red-finanzas
# Se construye desde la raíz del repositorio (usa dataset/ para los ejemplos):
#   docker build -f plataforma/Dockerfile -t evaluaciones .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY plataforma/EvalPlatform/EvalPlatform.csproj plataforma/EvalPlatform/
RUN dotnet restore plataforma/EvalPlatform/EvalPlatform.csproj
COPY dataset/tickets.csv dataset/
COPY plataforma/EvalPlatform/ plataforma/EvalPlatform/
RUN dotnet publish plataforma/EvalPlatform/EvalPlatform.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir /datos && chown app /datos
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__Eval="Data Source=/datos/eval.db"
VOLUME /datos
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "EvalPlatform.dll"]

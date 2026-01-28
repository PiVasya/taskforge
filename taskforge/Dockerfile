FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . ./
RUN dotnet restore
RUN dotnet publish -c Release -o /out -r linux-x64 --self-contained false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

# Runtime deps for OpenCvSharpExtern (image comparison only, no OCR).
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libtesseract5 \
        liblept5 \
        libglib2.0-0 \
        libgl1 \
    && TESS5="$(ldconfig -p | awk '/libtesseract\.so\.5/{print $NF; exit}')" \
    && if [ -n "$TESS5" ]; then \
           TESS_DIR="${TESS5%/*}"; \
           if [ ! -e "$TESS_DIR/libtesseract.so.4" ]; then ln -s "$TESS5" "$TESS_DIR/libtesseract.so.4"; fi; \
       fi \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet","taskforge.dll"]

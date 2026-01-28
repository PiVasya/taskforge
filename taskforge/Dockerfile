FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . ./
RUN dotnet restore -r linux-x64
RUN dotnet publish -c Release -o /out -r linux-x64 --self-contained false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

# OpenCvSharpExtern (from NuGet) is linked against libtesseract.so.4 on linux-x64.
# Debian bookworm provides libtesseract.so.5, so we install it and add a compatibility symlink.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libtesseract5 \
        liblept5 \
        libglib2.0-0 \
        libgl1 \
        # OpenCvSharp (HighGUI) may require GTK at runtime (libgtk-x11-2.0.so.0).
        libgtk2.0-0 \
	        # OpenCV runtime deps (needed by OpenCvSharpExtern on Debian).
	        libdc1394-25 \
    && ln -sf /usr/lib/x86_64-linux-gnu/libtesseract.so.5 /usr/lib/x86_64-linux-gnu/libtesseract.so.4 \
    && rm -rf /var/lib/apt/lists/*

# Ensure OpenCvSharp native libraries are discoverable at runtime.
# The NuGet runtime package puts them under /app/runtimes/linux-x64/native.
ENV LD_LIBRARY_PATH="/app:/app/runtimes/linux-x64/native:${LD_LIBRARY_PATH}"

COPY --from=build /out ./

# Some .NET native library probing paths do not include the 'runtimes/*/native' folder.
# Provide compatibility symlinks in /app so OpenCvSharp can always dlopen the native library.
RUN if [ -f /app/runtimes/linux-x64/native/libOpenCvSharpExtern.so ]; then \
        ln -sf /app/runtimes/linux-x64/native/libOpenCvSharpExtern.so /app/libOpenCvSharpExtern.so && \
        ln -sf /app/runtimes/linux-x64/native/libOpenCvSharpExtern.so /app/OpenCvSharpExtern.so ; \
    fi
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet","taskforge.dll"]

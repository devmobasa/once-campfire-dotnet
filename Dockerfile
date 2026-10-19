# syntax = docker/dockerfile:1
#
# Production image for the native .NET rewrite. Non-root user (uid 1000), persistent
# storage (/rails/storage/{db,files,keys}) and in-process public HTTP/TLS listeners.
# bin/boot enables public HTTP/TLS and caching in the app process;
# TARGET_PORT (3000) remains available as the direct application listener.
#
# Two variants of the same code (docs/development.md), as final stages:
#
#   docker build -t campfire-dotnet:app --build-arg APP_VERSION=... --build-arg GIT_REVISION=... .
#       `jit`, the default (last stage): ReadyToRun on the pinned ASP.NET Core runtime.
#       JIT is faster on the measured page and posting workloads.
#   docker build -t campfire-dotnet:aot --target aot --build-arg APP_VERSION=... --build-arg GIT_REVISION=... .
#       `aot`: less memory, faster startup and no .NET runtime in the image.
#
# Pinned build dependencies (PROVENANCE.md and docs/development.md):
#   * debian:trixie-slim by index digest, and every apt package from snapshot.debian.org at the
#     same date as the reference image (parity/docker/reference.Dockerfile), so libvips, ffmpeg
#     and everything they link are the same builds: libvips42t64 8.16.1-1+deb13u1 and
#     ffmpeg 7:7.1.5-0+deb13u1. Native AOT's toolchain (clang, zlib) comes from the same snapshot.
#   * The .NET SDK 11.0.100-rc.2.26504.105 and the ASP.NET Core runtime 11.0.0-rc.2.26504.105 from
#     Microsoft's tarballs, checked against the SHA-512s in the 11.0 release metadata.
# Built for linux/amd64 and linux/arm64 (TARGETARCH). Both variants publish for one Linux runtime
# identifier (linux-x64 or linux-arm64), so the output carries only that platform's native libraries.
# The build stages run on the target platform, because Native AOT compiles for the machine it runs on;
# an arm64 image built on amd64 compiles under emulation.

ARG DEBIAN_IMAGE=docker.io/library/debian:trixie-slim@sha256:a99cfc517144bc59b1978475ec53b46ecabec7e43635402ee5b77cc54cd1b20a
ARG DEBIAN_SNAPSHOT=20260918T000000Z


# Debian on the snapshot date.
FROM ${DEBIAN_IMAGE} AS debian
ARG DEBIAN_SNAPSHOT
RUN sed -i \
      -e "s|^URIs: http://deb.debian.org/debian-security\$|URIs: http://snapshot.debian.org/archive/debian-security/${DEBIAN_SNAPSHOT}|" \
      -e "s|^URIs: http://deb.debian.org/debian\$|URIs: http://snapshot.debian.org/archive/debian/${DEBIAN_SNAPSHOT}|" \
      /etc/apt/sources.list.d/debian.sources && \
    ! grep -q deb.debian.org /etc/apt/sources.list.d/debian.sources && \
    printf '%s\n' 'Acquire::Check-Valid-Until "false";' 'Acquire::Retries "5";' > /etc/apt/apt.conf.d/99snapshot


# The pinned SDK with the sources restored, shared by both build stages. clang and zlib are Native
# AOT's prerequisites for the native link.
FROM debian AS sdk
ARG TARGETARCH
ARG DOTNET_SDK_VERSION=11.0.100-rc.2.26504.105
ARG DOTNET_SDK_SHA512_AMD64=54e9463339337cef68f455e97dadac0a9e1b49416a3b15504b577144b24ae4b4eed4e7ba3bac507650b61edf704e977b4627d3804f4c2688cc5bd69b4f2eecdd
ARG DOTNET_SDK_SHA512_ARM64=2ebaea263b9428af20f97fd8b5379e1248f21401e8345b5290387ff5deeda84664394da2f9ec480453056c307ed8a4e86bf675219e904a6e1d06b156525aa958
RUN apt-get update -qq && \
    apt-get install --no-install-recommends -y ca-certificates curl libicu76 clang zlib1g-dev && \
    rm -rf /var/lib/apt/lists /var/cache/apt/archives
RUN case "$TARGETARCH" in \
      amd64) rid=x64; sha512=$DOTNET_SDK_SHA512_AMD64 ;; \
      arm64) rid=arm64; sha512=$DOTNET_SDK_SHA512_ARM64 ;; \
      *) echo "unsupported target architecture $TARGETARCH" >&2; exit 1 ;; \
    esac && \
    curl -fsSL -o /tmp/sdk.tar.gz "https://builds.dotnet.microsoft.com/dotnet/Sdk/${DOTNET_SDK_VERSION}/dotnet-sdk-${DOTNET_SDK_VERSION}-linux-${rid}.tar.gz" && \
    echo "${sha512}  /tmp/sdk.tar.gz" | sha512sum -c - && \
    mkdir -p /usr/share/dotnet && tar -xzf /tmp/sdk.tar.gz -C /usr/share/dotnet && rm /tmp/sdk.tar.gz && \
    ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet && \
    echo "linux-${rid}" > /etc/dotnet-rid
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

WORKDIR /src
# Restore first, from the project files and lock files only, so a source change keeps the layer. The
# lock file has both Linux runtime identifiers' graphs (src/Campfire.Web/Campfire.Web.csproj);
# PublishReadyToRun also fetches the ReadyToRun compiler for the jit stage.
COPY global.json Directory.Build.props Directory.Packages.props CodeMetricsConfig.txt ./
COPY eng/style.editorconfig .editorconfig
COPY src/Campfire.Application/Campfire.Application.csproj src/Campfire.Application/packages.lock.json src/Campfire.Application/
COPY src/Campfire.Web/Campfire.Web.csproj src/Campfire.Web/packages.lock.json src/Campfire.Web/
RUN --mount=type=cache,id=campfire-dotnet-nuget,target=/root/.nuget/packages \
    dotnet restore src/Campfire.Web/Campfire.Web.csproj --locked-mode -p:PublishReadyToRun=true
COPY schema.sql ./
COPY src src


# Native AOT: one native binary (plus the SQLite library Microsoft.Data.Sqlite bundles). Trim and AOT
# warnings are errors, as in eng/aot. The project selects OptimizationPreference=Speed without raising the CPU baseline. The debug symbols (.dbg) stay in this stage.
FROM sdk AS build-aot
RUN --mount=type=cache,id=campfire-dotnet-nuget,target=/root/.nuget/packages \
    dotnet publish src/Campfire.Web/Campfire.Web.csproj -c Release -r "$(cat /etc/dotnet-rid)" --no-restore -o /out && \
    rm -f /out/*.dbg


# JIT: the same code, framework-dependent and ReadyToRun, run by the pinned ASP.NET Core runtime.
FROM sdk AS build-jit
RUN --mount=type=cache,id=campfire-dotnet-nuget,target=/root/.nuget/packages \
    dotnet publish src/Campfire.Web/Campfire.Web.csproj -c Release -r "$(cat /etc/dotnet-rid)" --no-restore \
      -o /out --self-contained false -p:PublishAot=false -p:PublishReadyToRun=true -p:UseAppHost=false


# The ASP.NET Core runtime (jit only) for the target architecture.
FROM debian AS downloads
ARG TARGETARCH
ARG ASPNETCORE_VERSION=11.0.0-rc.2.26504.105
ARG ASPNETCORE_SHA512_AMD64=21a6e21bfeb8c04d3284a3a8a1184ff48fe4f1f95c9fb32036b6e612b4529f1d5dfa7765065724e5d9bf059979df5ab8d393383cf6457b73bcd1a7e91bda2ac1
ARG ASPNETCORE_SHA512_ARM64=76e980606a16edca204cdbe68262205b1e41348fe89d964ea862a3dbd82d5ab017bdba96be8625cc91be71f502bda850e40dbd46fdd8f8d0fa1bf6ba18991114
RUN apt-get update -qq && \
    apt-get install --no-install-recommends -y ca-certificates curl && \
    rm -rf /var/lib/apt/lists /var/cache/apt/archives
RUN case "$TARGETARCH" in \
      amd64) rid=x64; sha512=$ASPNETCORE_SHA512_AMD64 ;; \
      arm64) rid=arm64; sha512=$ASPNETCORE_SHA512_ARM64 ;; \
      *) echo "unsupported target architecture $TARGETARCH" >&2; exit 1 ;; \
    esac && \
    curl -fsSL -o /tmp/aspnetcore.tar.gz "https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/${ASPNETCORE_VERSION}/aspnetcore-runtime-${ASPNETCORE_VERSION}-linux-${rid}.tar.gz" && \
    echo "${sha512}  /tmp/aspnetcore.tar.gz" | sha512sum -c - && \
    mkdir -p /out/dotnet && tar -xzf /tmp/aspnetcore.tar.gz -C /out/dotnet


# Everything both variants share: the native dependencies, the Rails assets and public/
# files, the user, the storage layout, the hooks and the environment.
FROM debian AS runtime

ARG LIBVIPS_VERSION=8.16.1-1+deb13u1
ARG FFMPEG_VERSION=7:7.1.5-0+deb13u1
# What the app loads at run time: glibc, libstdc++ and zlib (for the JIT runtime), OpenSSL, ICU
# (culture-sensitive string operations behave as they do on the reference image: the app keeps
# globalization on), time zones, the system CA store (webhooks, unfurling, Web Push, ACME), libvips
# for NetVips, and ffmpeg/ffprobe. No PDF previewers: the Rails image has neither poppler nor mupdf.
RUN apt-get update -qq && \
    apt-get install --no-install-recommends -y \
      ca-certificates libc6 libgcc-s1 libstdc++6 zlib1g libssl3t64 libicu76 tzdata \
      libvips42t64=${LIBVIPS_VERSION} ffmpeg=${FFMPEG_VERSION} && \
    rm -rf /var/lib/apt/lists /var/cache/apt/archives
# libvips loads its modules into the global symbol scope (GModule's default), and its OpenSlide
# module links the system libsqlite3. Once that is loaded, the SQLite bundled with
# Microsoft.Data.Sqlite (e_sqlite3) binds its not yet resolved internal calls to the system library
# and crashes the process (seen as the first FTS5 insert after a video preview). Rails blocks the
# OpenSlide loader anyway (config/initializers/vips.rb), so the module goes.
RUN rm -f /usr/lib/*/vips-modules-*/vips-openslide.so && \
    ! ldd /usr/lib/*/vips-modules-*/*.so | grep -q libsqlite3

# Image metadata
ARG OCI_DESCRIPTION
LABEL org.opencontainers.image.description="${OCI_DESCRIPTION}"
ARG OCI_SOURCE
LABEL org.opencontainers.image.source="${OCI_SOURCE}"
LABEL org.opencontainers.image.licenses="MIT"

# Run and own only the runtime files as a non-root user, as the Rails image does.
RUN groupadd --system --gid 1000 rails && \
    useradd rails --uid 1000 --gid 1000 --create-home --shell /bin/bash

# The pinned Rails app's browser assets and public/ files, and the vendored gem assets: the asset
# pipeline digests them at boot as Propshaft does, and ActionDispatch::Static serves public/.
COPY reference/app/assets /app/reference/app/assets
COPY reference/app/javascript /app/reference/app/javascript
COPY reference/vendor/javascript /app/reference/vendor/javascript
COPY reference/config/importmap.rb /app/reference/config/importmap.rb
COPY reference/config/initializers/assets.rb /app/reference/config/initializers/assets.rb
COPY reference/public /app/reference/public
COPY vendor/assets /app/vendor/assets
COPY LICENSE /app/LICENSE
COPY licenses /app/licenses
COPY reference/MIT-LICENSE /app/reference/MIT-LICENSE
ENV CAMPFIRE_REFERENCE_DIR=/app/reference \
    CAMPFIRE_VENDOR_ASSETS_DIR=/app/vendor/assets \
    CAMPFIRE_PUBLIC_PATH=/app/reference/public

WORKDIR /rails
COPY --chmod=755 bin/boot bin/start-app /rails/bin/

# The storage root is /rails/storage: storage/db/<env>.sqlite3, storage/files, storage/backups,
# and autocert-compatible certificates in storage/thruster. Writable by any uid: the parity harness runs
# containers as the host user.
RUN mkdir -p /rails/storage/db /rails/storage/files /rails/storage/backups && \
    chown -R 1000:1000 /rails && \
    chmod -R a+rwX /rails/storage

USER 1000:1000

# Configure environment defaults
ENV RAILS_ENV="production"
ENV HTTP_IDLE_TIMEOUT=60
ENV HTTP_READ_TIMEOUT=300
ENV HTTP_WRITE_TIMEOUT=300

# Set version and revision
ARG APP_VERSION
ENV APP_VERSION=$APP_VERSION
ARG GIT_REVISION
ENV GIT_REVISION=$GIT_REVISION

# Expose ports for HTTP and HTTPS
EXPOSE 80 443

# Start the server by default, this can be overwritten at runtime
CMD ["bin/boot"]


# `docker build --target aot`: the Native AOT binary needs no .NET runtime.
FROM runtime AS aot
COPY --from=build-aot /out /app
COPY --chmod=755 <<'EOF' /usr/local/bin/campfire
#!/bin/sh
exec /app/campfire "$@"
EOF


# The default jit stage: the ASP.NET Core runtime runs /app/campfire.dll. `campfire [command]`
# runs the app. Processes show as `dotnet`.
FROM runtime AS jit
COPY --from=downloads /out/dotnet /usr/share/dotnet
COPY --from=build-jit /out /app
COPY --chmod=755 <<'EOF' /usr/local/bin/campfire
#!/bin/sh
exec /usr/share/dotnet/dotnet /app/campfire.dll "$@"
EOF
ENV DOTNET_ROOT=/usr/share/dotnet \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

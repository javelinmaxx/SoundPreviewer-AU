#!/bin/bash
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
cd "$project_dir"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$project_dir/.build/dotnet}"
# Share the publish cache on new checkouts, while reusing an existing launch cache.
if [[ -z "${NUGET_PACKAGES:-}" ]]; then
    if [[ -d "$project_dir/.build/nuget" && ! -d "$project_dir/.build/nuget-packages" ]]; then
        export NUGET_PACKAGES="$project_dir/.build/nuget"
    else
        export NUGET_PACKAGES="$project_dir/.build/nuget-packages"
    fi
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export AVALONIA_TELEMETRY_OPTOUT=1
if [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then
    dotnet_command="$DOTNET_ROOT/dotnet"
elif command -v dotnet >/dev/null 2>&1; then
    dotnet_command="$(command -v dotnet)"
elif [[ -x /opt/homebrew/opt/dotnet@8/bin/dotnet ]]; then
    dotnet_command=/opt/homebrew/opt/dotnet@8/bin/dotnet
elif [[ -x /usr/local/share/dotnet/dotnet ]]; then
    dotnet_command=/usr/local/share/dotnet/dotnet
else
    echo '请先安装 .NET 8 SDK，或直接使用已发布版本的 Start-SoundPreviewer.command。'
    exit 1
fi
# Build performs an incremental restore for the app and worker only. Restoring
# the solution separately starts another SDK process and includes test projects.
"$dotnet_command" build src/SoundPreviewer.App/SoundPreviewer.App.csproj -c Release \
    -p:RestoreConfigFile="$project_dir/NuGet.Config"
exec "$dotnet_command" "$project_dir/src/SoundPreviewer.App/bin/Release/net8.0/SoundPreviewer.dll" "$@"

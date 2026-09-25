#!/usr/bin/env bash
# Consumes the built packages the way a real user would, from a clean project with no reference
# to this repository's source, optionally under Native AOT.
#
# This catches a package that builds, tests and installs cleanly and then fails at the consumer's
# first call, and trim or AOT warnings that only a consumer sees. Adapted from
# sanamhub/ada-csharp tests/packaging/verify-package.sh.
set -euo pipefail

PACKAGE_DIR=""
AOT="false"

while [ $# -gt 0 ]; do
  case "$1" in
    --package-dir) PACKAGE_DIR="$2"; shift 2 ;;
    --aot)         AOT="true";       shift 1 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

[ -n "$PACKAGE_DIR" ] || { echo "--package-dir is required" >&2; exit 2; }
PACKAGE_DIR="$(cd "$PACKAGE_DIR" && pwd)"

# The core package's file name has the version digit right after "Jharicast.". The other packages
# (Jharicast.Something.x.y.z) do not, so this glob finds the core alone.
PACKAGES=("$PACKAGE_DIR"/Jharicast.[0-9]*.nupkg)
if [ ! -f "${PACKAGES[0]}" ]; then
  echo "no Jharicast package found in $PACKAGE_DIR" >&2
  exit 1
fi
VERSION="$(basename "${PACKAGES[0]}" .nupkg)"
VERSION="${VERSION#Jharicast.}"

# Under Git Bash, pwd returns an MSYS path that .NET cannot read.
NATIVE_PACKAGE_DIR="$PACKAGE_DIR"
if command -v cygpath >/dev/null 2>&1; then
  NATIVE_PACKAGE_DIR="$(cygpath -w "$PACKAGE_DIR")"
fi

echo "consuming Jharicast $VERSION from $NATIVE_PACKAGE_DIR (aot=$AOT)"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cd "$WORK"

# Our packages can only come from the local folder, everything else only from nuget.org. A
# local-only feed would starve Native AOT, which restores the ILCompiler packages from nuget.org.
cat > NuGet.Config <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$NATIVE_PACKAGE_DIR" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="Jharicast" />
      <package pattern="Jharicast.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
XML

# Warnings are errors, so a trim or AOT warning (IL2xxx, IL3xxx) from our packages fails here.
cat > consumer.csproj <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <!-- Set explicitly. This project lives in a temp directory on purpose, so it inherits none
         of the repository's Directory.Build.props. -->
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>Consumer</RootNamespace>
    <PublishAot>$AOT</PublishAot>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Jharicast" Version="$VERSION" />
  </ItemGroup>
</Project>
XML

cat > Program.cs <<'CS'
using Jharicast;

// The core is pure logic, so this checks that rules evaluate and that an official level is never
// lowered, in a trimmed and AOT compiled binary.
double?[] members = [70, 90, 110, 130, 150, 40, 60, 80, 100, 120];
AlertLevel model = RainAlertRule.V1.Evaluate(members);
AlertLevel combined = AlertLevels.Combine(AlertLevel.Red, model);

if (combined != AlertLevel.Red) { Console.Error.WriteLine($"FAIL: official Red became {combined}"); return 1; }
if (EnsembleStats.Quantile(members, 0.5) <= 0) { Console.Error.WriteLine("FAIL: median"); return 1; }

Console.WriteLine($"model level: {model}");
Console.WriteLine("PASS");
return 0;
CS

dotnet restore --verbosity quiet

if [ "$AOT" = "true" ]; then
  dotnet publish -c Release -o out --verbosity quiet
  ./out/consumer
else
  dotnet run -c Release --verbosity quiet
fi

echo "package consumption OK"

{ pkgs ? import <nixpkgs> { } }:

let
  dotnet = pkgs.dotnetCorePackages.sdk_10_0;

  # Native libraries dlopen'd at runtime by prebuilt binaries that come from
  # NuGet or libs/OpenFreqAudio, so Nix can't rewrite their library paths:
  #   - Avalonia's X11 backend and SkiaSharp (client UI)
  #   - SharpHook's libuiohook (global hotkeys)
  #   - BASS (libs/OpenFreqAudio/OpenFreqAudio/Assets/linux/*.so)
  runtimeLibs = with pkgs; [
    # Avalonia / SkiaSharp
    fontconfig
    freetype
    libglvnd # libGL / libEGL
    libx11
    libxext
    libxrandr
    libxi
    libxcursor
    libice
    libsm
    libxkbcommon
    # SharpHook (libuiohook)
    libxtst
    libxinerama
    libxt
    libxkbfile
    # BASS audio
    alsa-lib
    # For running framework-dependent publish output under this SDK
    openssl
    icu
  ];
in
pkgs.mkShell {
  packages = [ dotnet ];

  DOTNET_ROOT = "${dotnet}/share/dotnet";
  DOTNET_CLI_TELEMETRY_OPTOUT = "1";

  shellHook = ''
    # /run/opengl-driver/lib is where NixOS exposes the system's GPU drivers;
    # without it Skia falls back to software rendering.
    export LD_LIBRARY_PATH=${pkgs.lib.makeLibraryPath runtimeLibs}:/run/opengl-driver/lib''${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}
  '';
}

# Third-party notices

## WorkshopAndroidDownloader

- Upstream: https://github.com/Apricityx/WorkshopAndroidDownloader
- License: Apache License 2.0
- Reference areas: Steam depot manifest framing and protobuf field layout, Steam CDN manifest/chunk request flow, AES depot-key handling, and the Steam chunk processing pipeline.
- Local adaptation: `SteamDepotManifest.cs`, `SteamDepotChunkProcessor.cs`, `SupplyStationModAssembler.cs` and the UGC transfer code are independently implemented in C# for this project. They are not copied Kotlin source files. Protocol behavior and data structures were cross-checked against the upstream implementation.
- Modifications: the local implementation adds bounded parsing and decompression, descriptor identity checks, CDN host allowlisting, token refresh, report redaction, safe Windows output paths, SHA-256/Adler32/SHA-1 verification, and isolated staging/assembly.

The upstream license is reproduced in `third_party/WorkshopAndroidDownloader-LICENSE.txt`.

## Sts2MobileLauncher

- Upstream: https://github.com/ModinMobileSTS/Sts2MobileLauncher
- Reference areas: its public `SpireSupplyStationClient.kt`, `DepotManifest.kt`, `WorkshopChunkProcessor.kt`, and `SteamDepotDirectoryDownloader.kt` implementations.
- The project-specific reference was consulted for compatibility behavior; no Android application UI or account-authentication code is being ported into this manager.

## SharpCompress

- Package: `SharpCompress` 0.50.7
- Upstream: https://github.com/adamhathcock/sharpcompress
- License: MIT
- Use: managed LZMA and Zstandard decompression for Steam VZip/VZstd UGC Chunk payloads.
- The package is referenced by the WPF manager and the isolated Windows probe project. No native DLL or external executable is required.

The MIT license notice is reproduced in `third_party/SharpCompress-LICENSE.txt`.

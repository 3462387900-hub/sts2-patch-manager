# Third-party notices

## WorkshopAndroidDownloader

- Upstream: https://github.com/Apricityx/WorkshopAndroidDownloader
- License: Apache License 2.0
- Reference areas: Steam depot manifest framing and protobuf field layout, Steam CDN manifest/chunk request flow, and the Steam chunk processing pipeline.
- Local adaptation: SteamDepotManifest.cs and the phase-two UGC transfer code are independently implemented in C# for this project. They are not copied Kotlin source files. Protocol behavior and data structures were cross-checked against the upstream implementation.
- Modifications: the local implementation adds bounded archive/section parsing, descriptor identity checks, CDN host allowlisting, token refresh, report redaction, and Windows-specific validation.

The upstream license is reproduced in third_party/WorkshopAndroidDownloader-LICENSE.txt.

## Sts2MobileLauncher

- Upstream: https://github.com/ModinMobileSTS/Sts2MobileLauncher
- Reference area: its public SpireSupplyStationClient.kt implementation and Steam CDN request sequencing.
- The project-specific reference was consulted for compatibility behavior; no Android application UI or account-authentication code is being ported into this manager.


## Compression libraries planned for the next processing phase

Raw Chunk download in this phase does not add third-party compression dependencies. Any later AES/decompression implementation will list its exact package versions and licenses here before it is included in a release.

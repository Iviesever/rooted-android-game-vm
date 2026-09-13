# Third-party notices

The Release installer does not bundle the following runtime dependencies. After the user accepts the relevant terms, it downloads fixed versions from their upstream HTTPS locations and verifies SHA-256 before use.

- Android SDK Command-line Tools, Emulator 37.1.11, Platform Tools 37.0.1 and Android 15 Google Play x86_64 system image revision 9 — Google Android SDK License Agreement. Each fixed archive is downloaded directly and verified with a product-side SHA-256 digest.
- Microsoft Build of OpenJDK 21 — GPL-2.0 with Classpath Exception.
- rootAVD — GPL-3.0; source revision 92df40eafa2f117053f56015e3c32ca706a55fa9.
- Magisk 30.6 — GPL-3.0.
- Material Files 1.7.4 universal APK — GPL-3.0-or-later; downloaded only by the post-package E2E test and never included in Release assets.

The Windows installer is built with Inno Setup 6.7.3 under its non-commercial license terms. The application itself uses the MIT License.
Release-time SPDX 2.3 validation uses SPDX Tools Python 0.8.5 under Apache-2.0; it is a build tool and is not bundled in the installer.

## Libraries bundled with 0.3.0

- Google.Protobuf 3.33.5 — BSD-3-Clause, Copyright 2015 Google Inc.
- Grpc.Net.Client, Grpc.Net.Common and Grpc.Core.Api 2.76.0 — Apache-2.0, the gRPC Authors.
- Microsoft.Extensions.Logging.Abstractions and DependencyInjection.Abstractions 8.0.0 — MIT, .NET Foundation and Contributors.
- The vendored emulator_controller.proto is taken from the fixed Emulator 37.1.11 distribution; Apache-2.0, Copyright 2018 The Android Open Source Project.
- Grpc.Tools 2.76.0 is a build-only dependency (Apache-2.0); its compiler is not installed with the product.

Exact NuGet archive hashes and versions are recorded in profiles/dependencies.json and the SPDX SBOM. No third-party games, APKs, skins or charts are bundled. Upstream license texts are included under licenses/ in the source repository.

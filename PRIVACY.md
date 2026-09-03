# Privacy

Rooted Android Game VM does not include telemetry, advertising, analytics or a project-operated network service.

The application accesses the network only when the user starts installation and accepts the Android SDK terms. It then downloads the fixed dependencies listed in `profiles/dependencies.json` from their upstream providers and verifies their recorded checksums.

Installed APKs, Android accounts and application data remain inside the local product-scoped virtual device unless the user explicitly chooses a Windows export destination. Private-data export reads only the package name and relative directory selected by the user. The project does not receive those files.

Third-party Android applications and upstream download providers have their own privacy practices and terms. Users are responsible for reviewing them and for accessing only applications and data they are authorized to use.

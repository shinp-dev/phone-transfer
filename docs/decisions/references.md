# Platform references checked 2026-09-07

- [Android NSD](https://developer.android.com/develop/connectivity/wifi/use-nsd)
- [Android foreground service timeouts](https://developer.android.com/develop/background-work/services/fgs/timeout)
- [Android foreground service types](https://developer.android.com/develop/background-work/services/fgs/service-types)
- [ASP.NET Core certificate authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/certauth?view=aspnetcore-10.0)
- [AGP 8.13 compatibility](https://developer.android.com/build/releases/agp-8-13-0-release-notes)

Pin build tooling deliberately: .NET 10 LTS, JDK 17, Gradle 8.13, AGP 8.13.2, Kotlin 2.2.21, compile/target SDK 36. Avoid adopting newly released AGP 9.x during initial architecture construction. Update versions through a dedicated verified change.

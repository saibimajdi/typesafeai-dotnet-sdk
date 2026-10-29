# Migrate to the TypeSafeAI packages

The TypeSafe AI .NET SDK now publishes under new NuGet package IDs. NuGet package IDs cannot be
renamed in place, so these are new package identities rather than new versions of the old IDs.

| Former package | New package |
| --- | --- |
| `TypeSafe.Sdk` | `TypeSafeAI.Sdk` |
| `TypeSafe.Sdk.DependencyInjection` | `TypeSafeAI.Sdk.DependencyInjection` |

Replace the old core package reference:

```bash
dotnet remove package TypeSafe.Sdk
dotnet add package TypeSafeAI.Sdk --prerelease
```

Only prerelease versions are currently published, so `--prerelease` is required. Once a stable
version is available, omit the flag to install the latest stable release.

Or update the project file directly, replacing `VERSION` with a published package version
(including its prerelease suffix):

```xml
<PackageReference Include="TypeSafeAI.Sdk" Version="VERSION" />
```

For dependency injection, replace the old integration package in the same way:

```bash
dotnet remove package TypeSafe.Sdk.DependencyInjection
dotnet add package TypeSafeAI.Sdk.DependencyInjection --prerelease
```

```xml
<PackageReference Include="TypeSafeAI.Sdk.DependencyInjection" Version="VERSION" />
```

The public C# namespaces changed with the package identity. Replace the former directives:

```csharp
using TypeSafe;
using TypeSafe.DependencyInjection;
```

with:

```csharp
using TypeSafeAI;
using TypeSafeAI.DependencyInjection;
```

The serialization namespace is now `TypeSafeAI.Serialization`. Public type and member names, wire
formats, configuration keys, and assembly names remain unchanged. This is a source-breaking
namespace migration, so fully qualified references must also change from `TypeSafe.*` to
`TypeSafeAI.*`. Do not install both the former and new package IDs in the same project; they
contain assemblies with the same identity.

The former package versions remain useful for repeatable restores and should remain available on
NuGet.org. Maintainers should deprecate them there, set their replacement packages to the new IDs,
and may unlist old versions after a suitable migration period rather than deleting them.

The canonical repository is now:

`https://github.com/saibimajdi/typesafeai-dotnet-sdk`

Existing GitHub URLs redirect after a repository rename, but contributors should update their
local remote:

```bash
git remote set-url origin https://github.com/saibimajdi/typesafeai-dotnet-sdk.git
```

Context7 state is external to this repository. After the GitHub rename is complete, submit the new
repository URL at [Context7's Add Library page](https://context7.com/add-library), confirm the new
library ID `/saibimajdi/typesafeai-dotnet-sdk`, and trigger a refresh from its library page. Update
any saved prompts or client configuration that still pins `/saibimajdi/typesafe-dotnet-sdk`.

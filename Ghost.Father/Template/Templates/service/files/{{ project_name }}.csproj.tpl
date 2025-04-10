<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>{{ safe_name }}</AssemblyName>
    <RootNamespace>{{ defaultNamespace }}</RootNamespace>
    <GhostInstallDir>{{ ghost_install_dir }}</GhostInstallDir>
  </PropertyGroup>

  <!-- Define common dependency versions to use throughout the project -->
  <PropertyGroup>
    <MicrosoftExtensionsVersion>9.0.0</MicrosoftExtensionsVersion>
  </PropertyGroup>

  <ItemGroup>
    <!-- Option 1: Use local Ghost SDK if available -->
    <Reference Include="Ghost.SDK" Condition="'{{ use_local_libs }}' == 'true'">
      <HintPath>$(GhostInstallDir)\libs\Ghost.SDK.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Ghost.Core" Condition="'{{ use_local_libs }}' == 'true'">
      <HintPath>$(GhostInstallDir)\libs\Ghost.Core.dll</HintPath>
      <Private>false</Private>
    </Reference>

    <!-- Required dependencies when using local libs -->
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="$(MicrosoftExtensionsVersion)" Condition="'{{ use_local_libs }}' == 'true'" />
    <PackageReference Include="Microsoft.Extensions.Logging" Version="$(MicrosoftExtensionsVersion)" Condition="'{{ use_local_libs }}' == 'true'" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="$(MicrosoftExtensionsVersion)" Condition="'{{ use_local_libs }}' == 'true'" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="$(MicrosoftExtensionsVersion)" Condition="'{{ use_local_libs }}' == 'true'" />

    <!-- Option 2: Use NuGet packages if not using local libs -->
    <PackageReference Include="Ghost.SDK" Version="{{ sdk_version }}" Condition="'{{ use_local_libs }}' != 'true'" />

    <!-- Common dependencies always needed -->
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="$(MicrosoftExtensionsVersion)" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="$(MicrosoftExtensionsVersion)" />
  </ItemGroup>

  <ItemGroup>
    <Content Include="appsettings.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
    <Content Include="appsettings.*.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
      <DependentUpon>appsettings.json</DependentUpon>
    </Content>
  </ItemGroup>
</Project>
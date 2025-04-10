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

  <ItemGroup>
    <!-- Use local Ghost SDK if available, otherwise use NuGet -->
    <Reference Include="Ghost.SDK" Condition="'{{ use_local_libs }}' == 'true'">
      <HintPath>$(GhostInstallDir)\libs\Ghost.SDK.dll</HintPath>
    </Reference>
    <Reference Include="Ghost.Core" Condition="'{{ use_local_libs }}' == 'true'">
      <HintPath>$(GhostInstallDir)\libs\Ghost.Core.dll</HintPath>
    </Reference>
    <PackageReference Include="Ghost.SDK" Version="{{ sdk_version }}" Condition="'{{ use_local_libs }}' != 'true'" />

    <!-- Additional dependencies -->
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.0" />
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